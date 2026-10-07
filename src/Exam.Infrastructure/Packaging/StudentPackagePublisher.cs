using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Exam.Contracts.Importing;
using Exam.Domain.Importing;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Compilation;
using Exam.Infrastructure.Security;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Infrastructure.Packaging;

/// <summary>
/// 从本次 依赖与编译检查 成功编译产物抽取坐标，并使用 选项区域与题包 允许清单生成完整加密 v2 学生包。
/// 发布必须指定密文路径与接收节点；服务器侧明文预览应显式调用 StudentPackageBuilder。
/// 调用方须独占本次导入/编译工作目录，直到抽取和组包结束，不得并行改写这些输入。
/// 本服务不负责版本入库、密钥下发或 试题修订/CLI 产品入口。
/// </summary>
public sealed class StudentPackagePublisher(PackageEncryptionService encryption)
{
    private readonly PackageEncryptionService _encryption =
        encryption ?? throw new ArgumentNullException(nameof(encryption));

    public async Task<StudentPackageResult> PublishAsync(
        ExamManifest manifest, StudentPackageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.CompiledQuestions is null)
            return Failed(manifest, request, StudentPackageIssueCode.OptionRegionInvalid,
                "发布学生包必须提供本次逐题成功编译结果，不能退回旧 PDF 或无坐标包。");

        try
        {
            if (!Path.IsPathFullyQualified(request.PackageRoot) ||
                !Path.IsPathFullyQualified(request.CompilationOutputDirectory))
                return Failed(manifest, request, StudentPackageIssueCode.PathEscaped,
                    "导入与编译目录必须为绝对路径。");
            var packageRoot = Path.GetFullPath(request.PackageRoot);
            var compilationRoot = Path.GetFullPath(request.CompilationOutputDirectory);
            PathBoundary.RejectExistingReparsePoints(packageRoot, packageRoot);
            PathBoundary.RejectExistingReparsePoints(compilationRoot, compilationRoot);
            var manifestPath = PathBoundary.ResolveWithinRoot(packageRoot,
                Path.Combine(packageRoot, StudentPackagePlan.MetadataFileName), "导入清单路径越界。");
            var manifestText = await ReadManifestAsync(manifestPath, cancellationToken);
            var validation = new ExamManifestValidator().ValidateJson(manifestText);
            if (!validation.IsValid)
                return Failed(manifest, request, StudentPackageIssueCode.MetadataInvalid,
                    "导入目录中的清单未通过结构和语义校验。");
            var json = ImportJson.Create();
            var stored = JsonSerializer.Deserialize<ExamManifest>(manifestText, json)!;
            if (!JsonNode.DeepEquals(JsonSerializer.SerializeToNode(manifest, json),
                    JsonSerializer.SerializeToNode(stored, json)))
                return Failed(manifest, request, StudentPackageIssueCode.MetadataInvalid,
                    "传入清单与本次导入目录不一致，拒绝用不同清单抽取与组包。");
            manifest = stored;
            var compiled = request.CompiledQuestions.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var structure = ImportedGradingRules.BuildStructure(manifest);
            if (compiled.Count != structure.Questions.Count ||
                structure.Questions.Any(question => !compiled.ContainsKey(question.QuestionId)))
                return Failed(manifest, request, StudentPackageIssueCode.OptionRegionInvalid,
                    "成功编译结果必须与本次清单题目逐一对应。");
            var sources = new List<OptionRegionSourceQuestion>();
            long totalBytes = 0;
            foreach (var question in structure.Questions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = compiled[question.QuestionId];
                if (result is null || !result.Succeeded || result.TimedOut || result.ExitCode != 0 ||
                    result.Issues is null || result.Issues.Count > 0 || string.IsNullOrWhiteSpace(result.OutputPdfPath))
                    return Failed(manifest, request, StudentPackageIssueCode.OptionRegionInvalid,
                        $"题目 {question.QuestionId} 缺少本次成功编译结果，不发布学生包。");
                if (!Path.IsPathFullyQualified(result.OutputPdfPath))
                    return Failed(manifest, request, StudentPackageIssueCode.PathEscaped,
                        "逐题 PDF 必须使用编译目录内的绝对路径。");
                var pdf = PathBoundary.ResolveWithinRoot(compilationRoot, result.OutputPdfPath,
                    "逐题 PDF 超出本次编译目录，未读取该文件。");
                var size = new FileInfo(pdf).Length;
                if (size <= 0 || size > StudentPackageBuilder.MaxPackageBytes ||
                    totalBytes > StudentPackageBuilder.MaxPackageBytes - size)
                    return Failed(manifest, request, StudentPackageIssueCode.OptionRegionInvalid,
                        "逐题 PDF 为空或超过学生包容量上限，未执行坐标抽取。");
                totalBytes += size;
                sources.Add(new OptionRegionSourceQuestion(question.QuestionId, pdf, question.OptionIds));
            }

            if (string.IsNullOrWhiteSpace(request.EncryptedPackagePath) || request.Recipients is not { Count: > 0 })
                return Failed(manifest, request, StudentPackageIssueCode.EncryptionFailed,
                    "发布必须指定密文路径和接收节点；明文预览不是已发布学生包。");
            cancellationToken.ThrowIfCancellationRequested();
            var extraction = new OptionRegionExtractor().Extract(
                new OptionRegionRequest(manifest.ExamId, request.PaperVersion, sources));
            cancellationToken.ThrowIfCancellationRequested();
            if (!extraction.Succeeded)
                return new(false, new StudentPackagePlan(manifest.ExamId, request.PaperVersion, [], []), null, null,
                    extraction.Issues.Select(issue => new StudentPackageIssue(StudentPackageIssueCode.OptionRegionInvalid,
                        $"题目 {issue.QuestionId} 选项 {issue.OptionId} 页 {issue.PageIndex}: {issue.Message}",
                        OptionRegionArtifact.FileName)).ToArray());
            return await new StudentPackageBuilder(_encryption).BuildAsync(
                request with { CompiledQuestions = compiled, OptionRegions = extraction.Artifact }, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return Failed(manifest, request, StudentPackageIssueCode.PathEscaped,
                "导入或编译路径越界、经过链接或无权读取，未发布学生包。");
        }
        catch (Exception error) when (error is IOException or ArgumentException or JsonException)
        {
            return Failed(manifest, request, StudentPackageIssueCode.MetadataInvalid,
                "本次导入清单或编译产物无效、缺失或读取失败，未发布学生包。");
        }
    }

    private static async Task<string> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > StudentPackageLayout.MaxMetadataBytes)
            throw new InvalidDataException("导入清单超过大小限制或为空。");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.ReadByte() != -1) throw new InvalidDataException("读取期间清单发生变化。");
        return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
    }

    private static StudentPackageResult Failed(ExamManifest manifest, StudentPackageRequest request,
        StudentPackageIssueCode code, string message) =>
        new(false, new StudentPackagePlan(manifest.ExamId, request.PaperVersion, [], []), null, null,
            [new StudentPackageIssue(code, message)]);
}
