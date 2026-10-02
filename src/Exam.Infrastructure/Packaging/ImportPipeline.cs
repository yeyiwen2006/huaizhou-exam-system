using System.Security.Cryptography;
using System.Text.Json;
using Exam.Contracts.Administration;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Domain.Importing;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Compilation;
using Exam.Infrastructure.Postgres.Administration;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Infrastructure.Packaging;

/// <summary>
/// C05 的真实导入流水线：源材料校验 → 逐题编译 → 安全组包加密（D03 发布）→ 版本资源入库 →
/// **A06 整体发布（试卷版本与计分规则同一事务）**。
///
/// 每一步失败都**停在那一阶段**并如实报告：不产出部分结果、不把失败当成功、不跳过依赖检查。
/// 发布走 A06 既有的原子入口 <see cref="PostgresBundlePublicationService.PublishAsync"/>：
/// 该入口会**真正解密**本次加密包、核对清单结构与参考答案一致后才发布，
/// 且试卷版本与计分规则在同一事务里生效——不存在「只发了试卷、没有答案规则」的半套状态。
/// 取消沿调用链传播：已加密但未入库的包不进入结果，入库但未发布的资源不会被当成已发布。
///
/// <see cref="InspectAsync"/> 只做到编译与坐标抽取为止（不组包、不入库、不发布），供导入预览使用；
/// <see cref="RunAsync"/> 在同一个准备结果上继续完成组包与发布，两步不会取得不同的清单或编译结果。
/// </summary>
public sealed class ImportPipeline(
    StudentPackagePublisher publisher,
    PostgresVersionedResourceStore resources,
    IExamVersioningService versioning,
    PostgresBundlePublicationService bundles,
    IPublicationCertificateProvider certificates,
    ManagedNodeEncryptionPolicy? encryptionPolicy = null,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ManagedNodeEncryptionPolicy _policy = encryptionPolicy ?? ManagedNodeEncryptionPolicy.None;

    /// <summary>
    /// 预检：校验清单与答案、逐题编译、逐题抽取选项坐标，返回逐题摘要供预览确认。
    /// **不组包、不入库、不发布**——预览确认后才由 <see cref="RunAsync"/> 发布。
    /// </summary>
    public async Task<ImportPipelineResult> InspectAsync(
        ImportPipelineRequest request,
        CancellationToken cancellationToken = default)
    {
        if (ValidatePaths(request, forPublish: false) is { } pathFailure)
        {
            return pathFailure;
        }

        var prepared = await PrepareAsync(request, cancellationToken);
        if (prepared.Failure is not null)
        {
            return prepared.Failure;
        }

        var manifest = prepared.Manifest!;
        var extractor = new OptionRegionExtractor();
        var preview = new List<ImportQuestionPreview>(manifest.Questions.Count);
        var issues = new List<ImportPipelineIssue>();
        foreach (var question in manifest.Questions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pdfPath = prepared.Compiled![question.QuestionId].OutputPdfPath!;
            var extraction = extractor.Extract(new OptionRegionRequest(manifest.ExamId, request.ExpectedPaperVersion,
                [new OptionRegionSourceQuestion(question.QuestionId, pdfPath, question.Options!)]));
            preview.Add(new ImportQuestionPreview(
                question.QuestionId, true, question.Options!.Count,
                extraction.Succeeded ? question.Options.Count : 0));
            if (!extraction.Succeeded)
            {
                issues.AddRange(extraction.Issues.Select(issue => new ImportPipelineIssue(
                    issue.Code.ToString(), issue.Message, question.QuestionId, issue.OptionId)));
            }
        }

        return new ImportPipelineResult(
            issues.Count == 0,
            issues.Count == 0 ? ImportStage.Completed : ImportStage.Package,
            issues,
            Preview: preview);
    }

    /// <summary>
    /// 预检之后继续：组包加密 → 版本资源入库 → **整体发布**（试卷版本与计分规则同一事务）。
    ///
    /// 收件人取**服务器受管节点**的公钥，不由操作者挑选：主备任一台都必须能解密，
    /// 加密层还会再校验一次覆盖，漏节点在这里就失败，不会产出解不开的包。
    /// </summary>
    public async Task<ImportPipelineResult> RunAsync(
        ImportPipelineRequest request,
        CancellationToken cancellationToken = default)
    {
        if (ValidatePaths(request, forPublish: true) is { } pathFailure)
        {
            return pathFailure;
        }

        // 没有可用发布公钥就不必编译：早失败，不浪费一次全量编译。
        PublicationCertificateLease lease;
        try
        {
            lease = certificates.Acquire();
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidOperationException)
        {
            return Failed(ImportStage.Package, "RecipientMissing",
                $"没有可用的发布公钥证书，未组包：{exception.Message}");
        }

        using (lease)
        {
            if (lease.Recipients.Count == 0)
            {
                return Failed(ImportStage.Package, "RecipientMissing", "没有可用的加密接收节点证书。");
            }

            if (_policy.RequiredNodeIds.Count > 0 &&
                !_policy.RequiredNodeIds.IsSubsetOf(
                    lease.Recipients.Select(recipient => recipient.NodeId).ToHashSet(StringComparer.Ordinal)))
            {
                return Failed(ImportStage.Package, "RecipientMissing",
                    "发布公钥未覆盖全部受管主备节点，未组包；请先补全节点证书配置。");
            }

            var prepared = await PrepareAsync(request, cancellationToken);
            if (prepared.Failure is not null)
            {
                return prepared.Failure;
            }

            var manifest = prepared.Manifest!;
            var compiled = prepared.Compiled!;
            // 请求给出的是**本次发布出的版本**；A06 的原子入口按「当前版本 = 目标版本 − 1」判定并发冲突。
            var targetVersion = request.ExpectedPaperVersion;
            // 资源标识与包标识必须一致：整体发布按资源标识解密核对学生包内容。
            var resourceId = $"student-package-{request.ExamId}-{targetVersion}";
            var encryptedPath = Path.Combine(request.PackageOutputDirectory,
                $"{request.ExamId}-{targetVersion}.epkg");
            var packaged = await publisher.PublishAsync(
                manifest,
                new StudentPackageRequest(
                    request.PackageRoot, request.CompilationOutputDirectory, request.PackageOutputDirectory,
                    resourceId, targetVersion, encryptedPath, lease.Recipients)
                {
                    CompiledQuestions = compiled
                },
                cancellationToken);
            if (!packaged.Succeeded || packaged.Envelope is null || packaged.EncryptedPackagePath is null)
            {
                return new ImportPipelineResult(false, ImportStage.Package,
                    packaged.Issues
                        .Select(issue => new ImportPipelineIssue(issue.Code.ToString(), issue.Message, Path: issue.Path))
                        .ToList(),
                    packaged.Plan);
            }

            // 4. 版本资源入库：入库失败不发布。
            var now = _timeProvider.GetUtcNow();
            try
            {
                await resources.SaveAsync(new VersionedEncryptedResource(
                    resourceId,
                    request.ExamId,
                    "paper-package",
                    targetVersion,
                    await File.ReadAllBytesAsync(packaged.EncryptedPackagePath, cancellationToken),
                    packaged.Envelope,
                    now,
                    request.Operator), cancellationToken);
            }
            catch (Exception exception)
                when (exception is IOException or InvalidOperationException or Npgsql.NpgsqlException)
            {
                return new ImportPipelineResult(false, ImportStage.Store,
                    [new ImportPipelineIssue("ResourceStoreFailed", "加密包入库失败，未发布版本。")], packaged.Plan);
            }

            // 5. 整体发布：A06 在同一事务里发布**试卷版本与计分规则**，并真正解密核对本次加密包。
            //    并发门取服务器当前版本，不由调用方申报：读数之后版本若被他人推进，整体发布会拒绝而不是覆盖。
            var snapshot = versioning.GetExam(request.ExamId);
            if (!snapshot.Succeeded || snapshot.Value is null)
            {
                return new ImportPipelineResult(false, ImportStage.Publish,
                    [new ImportPipelineIssue(snapshot.ErrorCode.ToString(),
                        snapshot.Error ?? "读取考试版本状态失败，未发布。")],
                    packaged.Plan, packaged.EncryptedPackagePath, resourceId);
            }

            var changed = ResolveChangedQuestionIds(request, manifest, snapshot.Value.Versions.PaperVersion);
            if (changed.Error is not null)
            {
                return new ImportPipelineResult(false, ImportStage.Publish,
                    [changed.Error], packaged.Plan, packaged.EncryptedPackagePath, resourceId);
            }

            // 预览已确认：RunAsync 自己完成了逐题编译（PrepareAsync）与逐题选项定位（组包器），
            // 这里不是把调用方的声明当成事实。
            var published = await bundles.PublishAsync(
                request.ExamId,
                new PublishExamBundleApiRequest(
                    request.RequestId,
                    targetVersion - 1,
                    snapshot.Value.Versions.GradingVersion,
                    resourceId,
                    prepared.ManifestJson!,
                    prepared.AnswerKeyJson!,
                    changed.Changed!,
                    request.Reason,
                    PreviewApproved: true),
                request.Operator,
                cancellationToken);
            if (!published.Succeeded)
            {
                return new ImportPipelineResult(false, ImportStage.Publish,
                    [new ImportPipelineIssue(
                        published.ErrorCode.ToString(),
                        published.Error ?? "试卷版本整体发布被拒绝。")],
                    packaged.Plan,
                    packaged.EncryptedPackagePath,
                    resourceId);
            }

            return new ImportPipelineResult(true, ImportStage.Completed, [], packaged.Plan,
                packaged.EncryptedPackagePath, resourceId, published.Value!.Versions.PaperVersion);
        }
    }

    /// <summary>
    /// 本次发布声明变更的题目。首次发布（服务器尚无试卷版本）整卷都是新的；
    /// **开考后修订必须由调用方逐题声明**：默认整卷都变会让全体考生被要求复核全卷，
    /// 而漏报变更题会让该题考生根本收不到复核提示——两种偏差都不接受，所以这里要求显式声明。
    /// 返回 (变更题号, 拒绝原因)；两者恰有一个非空。本方法是纯函数，不保存跨请求状态。
    /// </summary>
    private static (IReadOnlyList<string>? Changed, ImportPipelineIssue? Error) ResolveChangedQuestionIds(
        ImportPipelineRequest request, ExamManifest manifest, long currentPaperVersion)
    {
        if (currentPaperVersion < 1)
        {
            return (manifest.Questions.Select(question => question.QuestionId).ToList(), null);
        }

        var declared = (request.DeclaredChangedQuestionIds ?? [])
            .Where(questionId => !string.IsNullOrWhiteSpace(questionId))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (declared.Count == 0)
        {
            return (null, new ImportPipelineIssue("ChangedQuestionsNotDeclared",
                "该考试已发布过试卷版本：开考后修订必须逐题声明本次变更的题号，不能默认整卷都变。"));
        }

        var known = manifest.Questions.Select(question => question.QuestionId).ToHashSet(StringComparer.Ordinal);
        var unknown = declared.Where(questionId => !known.Contains(questionId)).ToList();
        return unknown.Count > 0
            ? (null, new ImportPipelineIssue("ChangedQuestionsUnknown",
                $"声明的变更题号不在本次材料清单中：{string.Join("、", unknown.Take(5))}。"))
            : (declared, null);
    }

    /// <summary>
    /// 请求边界校验：路径必须齐全、绝对，产物目录不得写回材料目录。
    /// 在读取任何材料之前做完——越界或缺失的参数不该等到编译完整卷才发现。
    /// </summary>
    private static ImportPipelineResult? ValidatePaths(ImportPipelineRequest request, bool forPublish)
    {
        if (!IsUsablePath(request.PackageRoot) || !IsUsablePath(request.CompilationOutputDirectory) ||
            (forPublish && !IsUsablePath(request.PackageOutputDirectory)))
        {
            return Failed(ImportStage.Validate, "PathInvalid",
                "导入目录、编译输出目录与包输出目录都必须是完整的绝对路径。");
        }

        if (SamePath(request.CompilationOutputDirectory, request.PackageRoot) ||
            (forPublish && SamePath(request.PackageOutputDirectory, request.PackageRoot)))
        {
            return Failed(ImportStage.Validate, "PathInvalid",
                "编译产物与包产物必须写到材料目录之外，不写回材料目录。");
        }

        return null;
    }

    private static bool IsUsablePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private static bool SamePath(string left, string right) =>
        string.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>步骤 1–2：源材料校验与逐题编译。失败时返回带阶段的结果，不继续后续步骤。</summary>
    private async Task<(ImportPipelineResult? Failure, ExamManifest? Manifest,
        Dictionary<string, TeXCompileResult>? Compiled, string? ManifestJson, string? AnswerKeyJson)> PrepareAsync(
        ImportPipelineRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. 源材料校验：清单与完整参考答案都必须过 D01；缺答案不允许发布成品包。
        var manifestPath = Path.Combine(request.PackageRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return (Failed(ImportStage.Validate, "MaterialMissing", $"找不到导入清单：{manifestPath}"), null, null, null, null);
        }

        var manifestText = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var answerKeyPath = Path.Combine(request.PackageRoot, "server-only", "answer-key.json");
        var answerKeyText = File.Exists(answerKeyPath)
            ? await File.ReadAllTextAsync(answerKeyPath, cancellationToken)
            : null;
        var validation = new ExamManifestValidator().ValidateJson(manifestText, answerKeyText);
        if (!validation.IsValid)
        {
            return (Failed(ImportStage.Validate, "ManifestInvalid",
                string.Join("；", validation.Issues.Take(10).Select(issue => $"{issue.Rule}@{issue.Location}"))),
                null, null, null, null);
        }

        if (answerKeyText is null || validation.RequiresAnswerKeyArithmetic)
        {
            return (Failed(ImportStage.Validate, "AnswerKeyIncomplete", "缺少完整参考答案，不能发布成品包。"),
                null, null, null, null);
        }

        var manifest = JsonSerializer.Deserialize<ExamManifest>(manifestText, ImportJson.Create())!;
        if (!string.Equals(manifest.ExamId, request.ExamId, StringComparison.Ordinal))
        {
            return (Failed(ImportStage.Validate, "ExamMismatch", "清单的考试标识与请求不一致。"), null, null, null, null);
        }

        // 2. 逐题编译：工具链缺失或任一题编译失败都不继续组包。
        var toolchain = XeLaTeXToolchain.TryDetect(XeLaTeXPlatform.TeXLive);
        if (toolchain is null)
        {
            return (Failed(ImportStage.Compile, "ToolchainMissing", "找不到可用的 XeLaTeX，无法编译逐题源文件。"),
                null, null, null, null);
        }

        var runner = new ExternalProcessRunner();
        var version = await runner.RunAsync(toolchain.ExecutablePath, ["--version"],
            new Dictionary<string, string>(), TimeSpan.FromSeconds(10), cancellationToken);
        var compiler = new XeLaTeXCompiler(
            runner,
            toolchain with
            {
                Platform = version.StandardOutput.Contains("MiKTeX", StringComparison.OrdinalIgnoreCase)
                    ? XeLaTeXPlatform.MiKTeX
                    : XeLaTeXPlatform.TeXLive
            },
            TimeSpan.FromMinutes(3));

        var compiled = new Dictionary<string, TeXCompileResult>(StringComparer.Ordinal);
        foreach (var question in manifest.Questions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await compiler.CompileAsync(
                new TeXCompileRequest(request.PackageRoot, question.Source, request.CompilationOutputDirectory),
                cancellationToken);
            if (!result.Succeeded)
            {
                return (Failed(ImportStage.Compile, "QuestionCompileFailed",
                    string.Join("；", result.Issues.Select(issue => issue.Code.ToString())), question.QuestionId),
                    null, null, null, null);
            }

            compiled[question.QuestionId] = result;
        }

        return (null, manifest, compiled, manifestText, answerKeyText);
    }

    private static ImportPipelineResult Failed(ImportStage stage, string code, string message, string? questionId = null) =>
        new(false, stage, [new ImportPipelineIssue(code, message, questionId)]);
}
