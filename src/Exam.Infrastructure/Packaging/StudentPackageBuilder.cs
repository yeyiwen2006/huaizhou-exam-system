using System.IO.Compression;
using System.Text;
using System.Text.Json;

using Exam.Contracts.Importing;
using Exam.Domain.Importing;
using Exam.Infrastructure.Security;

namespace Exam.Infrastructure.Packaging;

/// <summary>一次学生端组包的输入。产物目录与加密产物都必须在导入包之外。</summary>
public sealed record StudentPackageRequest(
    string PackageRoot,
    string CompilationOutputDirectory,
    string StudentPackageDirectory,
    string PackageId,
    long PaperVersion,
    string? EncryptedPackagePath = null,
    IReadOnlyList<NodeKeyRecipient>? Recipients = null);

public sealed record StudentPackageResult(
    bool Succeeded,
    StudentPackagePlan Plan,
    string? EncryptedPackagePath,
    EncryptedPackageEnvelope? Envelope,
    IReadOnlyList<StudentPackageIssue> Issues);

/// <summary>
/// 生成学生端包并加密。依据 requirements.md「逐题导入、原版显示与加密」：
/// 按允许文件清单生成，仅含试题 PDF、题目所需图片、必要显示资源和无答案元数据；
/// 答案、解析及其专用资源留在服务器；整个包用 AES-GCM 加密，每个发布版本独立随机密钥，
/// 同一产物可重复下载（重复下载复用已加密的产物，不重新加密）。
///
/// 加密直接复用基础设施里已有的 <see cref="PackageEncryptionService"/>（HEXAMPKG v2）与既有信封格式，
/// 不另造格式：服务端与学生端已按同一格式编解码。
///
/// 包内明文是一个 ZIP，条目名即包内相对路径：
///   manifest.json（无答案元数据）、questions/&lt;考试名称_题号&gt;.pdf、以及清单 resourceRoots 下的资源。
/// **该布局由 D03 定义，仍需与 B03 一起确认后定稿**；确认前不把它写进需求侧文档当作已定契约。
/// 明文始终留在内存里，不落盘，避免加密前出现可读的中间文件。
/// </summary>
public sealed class StudentPackageBuilder(PackageEncryptionService encryption)
{
    /// <summary>内容检查的取样长度：只读前 64 KiB 判断是否夹带答案字段。</summary>
    public const int ContentSampleBytes = 64 * 1024;

    private const string QuestionsPrefix = "questions/";

    private static readonly string[] TextLikeExtensions =
    [
        ".json", ".txt", ".tex", ".sty", ".cls", ".md", ".csv", ".xml"
    ];

    private readonly PackageEncryptionService _encryption =
        encryption ?? throw new ArgumentNullException(nameof(encryption));

    public async Task<StudentPackageResult> BuildAsync(
        StudentPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var packageRoot = RequireDirectory(request.PackageRoot, nameof(request.PackageRoot));
        var compilationOutput = RequireDirectory(
            request.CompilationOutputDirectory, nameof(request.CompilationOutputDirectory));
        RequireOutsidePackageRoot(
            request.StudentPackageDirectory, packageRoot, nameof(request.StudentPackageDirectory));
        var studentDirectory = RequireEmptyDirectory(request.StudentPackageDirectory);

        if (request.EncryptedPackagePath is { } encryptedPath)
        {
            RequireOutsidePackageRoot(encryptedPath, packageRoot, nameof(request.EncryptedPackagePath));
        }

        var issues = new List<StudentPackageIssue>();
        var included = new List<string>();
        var excluded = new List<StudentPackageExclusion>();

        var (manifest, manifestText) = ReadManifest(packageRoot, issues);
        if (manifest is null || manifestText is null)
        {
            return Failed(string.Empty, request.PaperVersion, included, excluded, issues);
        }

        if (manifest.Questions.Count == 0)
        {
            issues.Add(new StudentPackageIssue(
                StudentPackageIssueCode.NoQuestions, "清单里没有题目，不能组包。"));
        }

        // 1. 无答案元数据。清单本身不含答案，但仍按内容规则核对一次，避免源头被改。
        if (StudentPackageAllowlist.ContainsAnswerBearingJson(manifestText, out var manifestReason))
        {
            issues.Add(new StudentPackageIssue(
                StudentPackageIssueCode.AnswerResourceDetected,
                $"清单内容被判定夹带答案，已停止组包：{manifestReason}",
                StudentPackagePlan.MetadataFileName));
        }
        else
        {
            included.Add(StudentPackagePlan.MetadataFileName);
        }

        // 2. 逐题编译好的 PDF。jobname 与源文件基名一致，与 D02 的编译产物命名对齐。
        foreach (var question in manifest.Questions)
        {
            var jobName = Path.GetFileNameWithoutExtension(question.Source);
            var destination = $"{QuestionsPrefix}{jobName}.pdf";
            var source = Path.Combine(compilationOutput, jobName + ".pdf");

            if (!File.Exists(source))
            {
                issues.Add(new StudentPackageIssue(
                    StudentPackageIssueCode.QuestionPdfMissing,
                    $"找不到题目 {question.QuestionNo} 编译好的 PDF（{jobName}.pdf）。漏题或编译失败不得发布半份试卷。",
                    destination));
                continue;
            }

            if (StudentPackageAllowlist.IsAllowed(destination, null, out var reason))
            {
                included.Add(destination);
            }
            else
            {
                // 必需文件被允许清单挡下：不是「排除可疑文件」，而是包本身有问题，必须停止。
                issues.Add(new StudentPackageIssue(
                    StudentPackageIssueCode.RequiredResourceRejected,
                    $"必需的题目 PDF 被允许清单挡下：{reason}",
                    destination));
            }
        }

        // 3. 清单声明的资源目录（题图与共享样式），逐个文件按允许清单判定。
        foreach (var resourceRoot in manifest.ResourceRoots)
        {
            CollectResourceRoot(packageRoot, resourceRoot, included, excluded, issues);
        }

        if (issues.Count > 0)
        {
            return Failed(manifest.ExamId, request.PaperVersion, included, excluded, issues);
        }

        var plan = new StudentPackagePlan(
            manifest.ExamId,
            request.PaperVersion,
            included.OrderBy(path => path, StringComparer.Ordinal).ToList(),
            excluded.OrderBy(exclusion => exclusion.Path, StringComparer.Ordinal).ToList());

        try
        {
            WriteStudentPackage(packageRoot, compilationOutput, studentDirectory, plan);

            string? artifactPath = null;
            EncryptedPackageEnvelope? envelope = null;
            if (request.EncryptedPackagePath is { } target)
            {
                (artifactPath, envelope) = await EncryptAsync(
                    request, plan, studentDirectory, target, cancellationToken);
            }

            return new StudentPackageResult(true, plan, artifactPath, envelope, issues);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            issues.Add(new StudentPackageIssue(
                StudentPackageIssueCode.EncryptionFailed,
                $"组包或加密失败：{exception.Message}"));
            return Failed(manifest.ExamId, request.PaperVersion, included, excluded, issues);
        }
    }

    private async Task<(string Path, EncryptedPackageEnvelope Envelope)> EncryptAsync(
        StudentPackageRequest request,
        StudentPackagePlan plan,
        string studentDirectory,
        string encryptedPath,
        CancellationToken cancellationToken)
    {
        var recipients = request.Recipients ?? [];
        var temporary = encryptedPath + ".partial";
        EncryptedPackageEnvelope envelope;

        using (var plaintext = new MemoryStream())
        {
            WriteZip(studentDirectory, plan, plaintext);
            plaintext.Position = 0;

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(encryptedPath))!);
            await using (var ciphertext = new FileStream(
                             temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                envelope = await _encryption.EncryptAsync(
                    plaintext,
                    ciphertext,
                    request.PackageId,
                    plan.PaperVersion,
                    recipients,
                    cancellationToken);
            }
        }

        // 半成品不得替换当前资源：先写临时文件，完整加密后再改名。
        File.Move(temporary, encryptedPath, overwrite: true);
        return (encryptedPath, envelope);
    }

    /// <summary>把计划内的条目按确定性顺序写成一个 ZIP；顺序固定使相同输入得到相同的明文。</summary>
    private static void WriteZip(string studentDirectory, StudentPackagePlan plan, Stream destination)
    {
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var relativePath in plan.IncludedPaths)
        {
            var source = Path.Combine(studentDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            using var file = File.OpenRead(source);
            file.CopyTo(entryStream);
        }
    }

    /// <summary>
    /// 按计划把文件写进学生端包目录。计划里只存相对路径，来源在这里区分：
    /// 无答案元数据取自包根，questions/ 下的 PDF 取自编译产物目录，其余资源按包内相对路径取自包根。
    /// </summary>
    private static void WriteStudentPackage(
        string packageRoot,
        string compilationOutput,
        string studentDirectory,
        StudentPackagePlan plan)
    {
        foreach (var relativePath in plan.IncludedPaths)
        {
            var destination = Path.Combine(
                studentDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            var source = ResolveSource(packageRoot, compilationOutput, relativePath);
            File.Copy(source, destination, overwrite: true);
        }
    }

    private static string ResolveSource(string packageRoot, string compilationOutput, string relativePath)
    {
        if (string.Equals(relativePath, StudentPackagePlan.MetadataFileName, StringComparison.Ordinal))
        {
            return Path.Combine(packageRoot, StudentPackagePlan.MetadataFileName);
        }

        if (relativePath.StartsWith(QuestionsPrefix, StringComparison.Ordinal))
        {
            return Path.Combine(compilationOutput, relativePath[QuestionsPrefix.Length..]);
        }

        return Path.Combine(packageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void CollectResourceRoot(
        string packageRoot,
        string resourceRoot,
        List<string> included,
        List<StudentPackageExclusion> excluded,
        List<StudentPackageIssue> issues)
    {
        var directory = Path.Combine(packageRoot, resourceRoot.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(directory))
        {
            issues.Add(new StudentPackageIssue(
                StudentPackageIssueCode.ResourceRootMissing,
                $"清单声明的资源目录不存在：{resourceRoot}。",
                resourceRoot));
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(packageRoot, file).Replace('\\', '/');
            if (StudentPackageAllowlist.IsAllowed(relativePath, SampleText(file), out var reason))
            {
                included.Add(relativePath);
            }
            else
            {
                // 排除可疑文件是预期行为，不是缺陷；原因记进计划供导入预览展示。
                excluded.Add(new StudentPackageExclusion(relativePath, reason!));
            }
        }
    }

    private static string? SampleText(string path)
    {
        var extension = Path.GetExtension(path);
        if (!TextLikeExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = (int)Math.Min(stream.Length, ContentSampleBytes);
        var buffer = new byte[length];
        var read = stream.ReadAtLeast(buffer, length, throwOnEndOfStream: false);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static (ExamManifest? Manifest, string? Text) ReadManifest(
        string packageRoot,
        List<StudentPackageIssue> issues)
    {
        var path = Path.Combine(packageRoot, StudentPackagePlan.MetadataFileName);
        if (!File.Exists(path))
        {
            issues.Add(new StudentPackageIssue(
                StudentPackageIssueCode.MetadataInvalid,
                $"导入包里没有清单：{StudentPackagePlan.MetadataFileName}"));
            return (null, null);
        }

        var text = File.ReadAllText(path);
        try
        {
            // 必须用 D01 的严格设置：它拒绝未定义字段、重复字段、带引号的整数与非法 Unicode。
            var manifest = JsonSerializer.Deserialize<ExamManifest>(text, ImportJson.Create());
            if (manifest is null)
            {
                issues.Add(new StudentPackageIssue(
                    StudentPackageIssueCode.MetadataInvalid, "清单内容为空。"));
                return (null, null);
            }

            return (manifest, text);
        }
        catch (JsonException exception)
        {
            issues.Add(new StudentPackageIssue(
                StudentPackageIssueCode.MetadataInvalid,
                $"清单不符合约定：{exception.Message}"));
            return (null, null);
        }
    }

    private static StudentPackageResult Failed(
        string examId,
        long paperVersion,
        List<string> included,
        List<StudentPackageExclusion> excluded,
        List<StudentPackageIssue> issues) =>
        new(
            false,
            new StudentPackagePlan(examId, paperVersion, included, excluded),
            null,
            null,
            issues);

    private static string RequireDirectory(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("必须是绝对路径。", parameterName);
        }

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"目录不存在：{path}");
        }

        return Path.GetFullPath(path);
    }

    private static string RequireEmptyDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("学生端包目录必须是绝对路径。", nameof(path));
        }

        var full = Path.GetFullPath(path);
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any())
        {
            throw new InvalidOperationException(
                $"学生端包目录必须为空，避免上一次的残留内容混入新包：{full}");
        }

        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>产物目录不得位于导入包之内：材料目录必须保持零改动。</summary>
    private static void RequireOutsidePackageRoot(string path, string packageRoot, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("必须是绝对路径。", parameterName);
        }

        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(packageRoot, full);
        var inside = relative == "." ||
                     (!Path.IsPathFullyQualified(relative) &&
                      relative != ".." &&
                      !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        if (inside)
        {
            throw new ArgumentException(
                $"产物不得写回导入包目录，材料目录必须保持零改动：{full}",
                parameterName);
        }
    }
}
