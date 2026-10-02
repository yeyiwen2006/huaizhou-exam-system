using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Domain.Importing;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Compilation;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Infrastructure.Packaging;

/// <summary>
/// C05 的真实导入流水线：源材料校验 → 逐题编译 → 安全组包加密（D03 发布）→ 版本资源入库 → 试卷版本发布。
///
/// 每一步失败都**停在那一阶段**并如实报告：不产出部分结果、不把失败当成功、不跳过依赖检查。
/// 发布时按 A06 的要求同时给足四道门（清单已校验、已编译、预览已确认、加密包已就绪），缺任何一道都不发布。
/// 取消沿调用链传播：已加密但未入库的包不进入结果，入库但未发布的资源不会被当成已发布。
///
/// <see cref="InspectAsync"/> 只做到编译与坐标抽取为止（不组包、不入库、不发布），供导入预览使用；
/// <see cref="RunAsync"/> 在同一个准备结果上继续完成组包与发布，两步不会取得不同的清单或编译结果。
/// </summary>
public sealed class ImportPipeline(
    StudentPackagePublisher publisher,
    PostgresVersionedResourceStore resources,
    IExamVersioningService versioning,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// 预检：校验清单与答案、逐题编译、逐题抽取选项坐标，返回逐题摘要供预览确认。
    /// **不组包、不入库、不发布**——预览确认后才由 <see cref="RunAsync"/> 发布。
    /// </summary>
    public async Task<ImportPipelineResult> InspectAsync(
        ImportPipelineRequest request,
        CancellationToken cancellationToken = default)
    {
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

    public async Task<ImportPipelineResult> RunAsync(
        ImportPipelineRequest request,
        CancellationToken cancellationToken = default)
    {
        // 没有可用收件人就不必编译：早失败，不浪费一次全量编译。
        var recipients = BuildRecipients(request.Recipients);
        if (recipients.Count == 0)
        {
            return Failed(ImportStage.Package, "RecipientMissing", "没有可用的加密接收节点证书。");
        }

        var prepared = await PrepareAsync(request, cancellationToken);
        if (prepared.Failure is not null)
        {
            return prepared.Failure;
        }

        var manifest = prepared.Manifest!;
        var compiled = prepared.Compiled!;

        var encryptedPath = Path.Combine(request.PackageOutputDirectory,
            $"{request.ExamId}-{request.ExpectedPaperVersion}.epkg");
        var packaged = await publisher.PublishAsync(
            manifest,
            new StudentPackageRequest(
                request.PackageRoot, request.CompilationOutputDirectory, request.PackageOutputDirectory,
                $"package-{request.ExamId}-{request.ExpectedPaperVersion}", request.ExpectedPaperVersion,
                encryptedPath, recipients)
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

        // 4. 版本资源入库：先入库再发布；入库失败不发布。
        var now = _timeProvider.GetUtcNow();
        var resourceId = $"student-package-{request.ExamId}-{request.ExpectedPaperVersion}";
        try
        {
            await resources.SaveAsync(new VersionedEncryptedResource(
                resourceId,
                request.ExamId,
                "student-package",
                request.ExpectedPaperVersion,
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

        // 5. 发布试卷版本：四道门同时给足，缺一不发布。
        var structure = ImportedGradingRules.BuildStructure(manifest);
        var published = versioning.PublishPaper(new PublishPaperRequest(
            request.ExamId,
            request.ExpectedPaperVersion - 1,
            resourceId,
            structure,
            structure.Questions.Select(question => question.QuestionId).ToList(),
            new PaperPublicationValidation(
                ManifestValidated: true,
                Compiled: true,
                PreviewApproved: true,
                EncryptedPackageReady: true),
            request.Operator,
            request.Reason,
            now,
            request.RequestId));
        if (!published.Succeeded)
        {
            return new ImportPipelineResult(false, ImportStage.Publish,
                [new ImportPipelineIssue(
                    published.ErrorCode.ToString(),
                    published.Error ?? "试卷版本发布被拒绝。")],
                packaged.Plan,
                packaged.EncryptedPackagePath,
                resourceId);
        }

        return new ImportPipelineResult(true, ImportStage.Completed, [], packaged.Plan,
            packaged.EncryptedPackagePath, resourceId, request.ExpectedPaperVersion);
    }

    /// <summary>步骤 1–2：源材料校验与逐题编译。失败时返回带阶段的结果，不继续后续步骤。</summary>
    private async Task<(ImportPipelineResult? Failure, ExamManifest? Manifest,
        Dictionary<string, TeXCompileResult>? Compiled)> PrepareAsync(
        ImportPipelineRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1. 源材料校验：清单与完整参考答案都必须过 D01；缺答案不允许发布成品包。
        var manifestPath = Path.Combine(request.PackageRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return (Failed(ImportStage.Validate, "MaterialMissing", $"找不到导入清单：{manifestPath}"), null, null);
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
                null, null);
        }

        if (answerKeyText is null || validation.RequiresAnswerKeyArithmetic)
        {
            return (Failed(ImportStage.Validate, "AnswerKeyIncomplete", "缺少完整参考答案，不能发布成品包。"),
                null, null);
        }

        var manifest = JsonSerializer.Deserialize<ExamManifest>(manifestText, ImportJson.Create())!;
        if (!string.Equals(manifest.ExamId, request.ExamId, StringComparison.Ordinal))
        {
            return (Failed(ImportStage.Validate, "ExamMismatch", "清单的考试标识与请求不一致。"), null, null);
        }

        // 2. 逐题编译：工具链缺失或任一题编译失败都不继续组包。
        var toolchain = XeLaTeXToolchain.TryDetect(XeLaTeXPlatform.TeXLive);
        if (toolchain is null)
        {
            return (Failed(ImportStage.Compile, "ToolchainMissing", "找不到可用的 XeLaTeX，无法编译逐题源文件。"),
                null, null);
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
                    null, null);
            }

            compiled[question.QuestionId] = result;
        }

        return (null, manifest, compiled);
    }

    private static IReadOnlyList<NodeKeyRecipient> BuildRecipients(IReadOnlyList<PackageRecipient> recipients) =>
        recipients
            .Where(recipient => !string.IsNullOrWhiteSpace(recipient.NodeId) && recipient.CertificateDer.Length != 0)
            .Select(recipient => new NodeKeyRecipient(
                recipient.NodeId, X509CertificateLoader.LoadCertificate(recipient.CertificateDer)))
            .ToList();

    private static ImportPipelineResult Failed(ImportStage stage, string code, string message, string? questionId = null) =>
        new(false, stage, [new ImportPipelineIssue(code, message, questionId)]);
}
