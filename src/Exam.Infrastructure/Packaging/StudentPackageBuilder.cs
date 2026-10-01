using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Domain.Importing;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Compilation;
using Exam.Infrastructure.Security;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Infrastructure.Packaging;

/// <summary>服务器侧组包输入。输出必须与材料、编译目录分离。</summary>
public sealed record StudentPackageRequest(
    string PackageRoot,
    string CompilationOutputDirectory,
    string StudentPackageDirectory,
    string PackageId,
    long PaperVersion,
    string? EncryptedPackagePath = null,
    IReadOnlyList<NodeKeyRecipient>? Recipients = null)
{
    /// <summary>由导入预览确认的逐文件显示资源清单。resourceRoots 只限定可选范围；默认不附带原始资源。</summary>
    public IReadOnlyList<string> DisplayResourcePaths { get; init; } = [];

    /// <summary>
    /// 按 questionId 传入本次 D02 成功编译结果，支持 compile-* 独立目录。
    /// 省略时读取调用方准备的平铺 PDF 目录；调用方须保证仅含本次已验收产物。
    /// </summary>
    public IReadOnlyDictionary<string, TeXCompileResult>? CompiledQuestions { get; init; }

    /// <summary>
    /// 本次组包的选项坐标产物。传入即产出学生清单 v2（必须附带根目录 option-regions.json）；
    /// 不传维持 v1（学生端不猜测点击位置）。产物必须与清单的题目及选项逐一对应，
    /// 由调用方用 OptionRegionExtractor 从本次已验收的编译产物抽取。
    /// </summary>
    public OptionRegionArtifact? OptionRegions { get; init; }
}

public sealed record StudentPackageResult(
    bool Succeeded,
    StudentPackagePlan Plan,
    string? EncryptedPackagePath,
    EncryptedPackageEnvelope? Envelope,
    IReadOnlyList<StudentPackageIssue> Issues);

/// <summary>
/// 仅打包重新生成的清单、逐题 PDF 和逐文件确认的显示资源。
/// 加密模式从内存快照生成 ZIP，不写出明文目录；未指定加密路径时显式生成服务器预览目录。
/// ZIP 布局由 StudentPaperPackage 读取。本服务不承担 PDF 正文、图片语义或选项坐标验收。
/// </summary>
public sealed class StudentPackageBuilder(PackageEncryptionService encryption)
{
    public const int MaxPackageBytes = StudentPackageLayout.MaxContentBytes;
    private const int MaxMetadataBytes = StudentPackageLayout.MaxMetadataBytes;
    private readonly PackageEncryptionService _encryption =
        encryption ?? throw new ArgumentNullException(nameof(encryption));

    public async Task<StudentPackageResult> BuildAsync(
        StudentPackageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PackageId);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.PaperVersion, 1);
        ArgumentNullException.ThrowIfNull(request.DisplayResourcePaths);
        var packageRoot = RequireDirectory(request.PackageRoot);
        var compilationRoot = RequireDirectory(request.CompilationOutputDirectory);
        var studentDirectory = RequireAbsolute(request.StudentPackageDirectory);
        RequireOutside(studentDirectory, packageRoot);
        RequireOutside(studentDirectory, compilationRoot);
        RequireEmpty(studentDirectory);
        var target = request.EncryptedPackagePath is null ? null : RequireAbsolute(request.EncryptedPackagePath);
        if (target is not null)
        {
            RequireOutside(target, packageRoot);
            RequireOutside(target, compilationRoot);
            RequireOutside(target, studentDirectory);
        }
        var issues = new List<StudentPackageIssue>();
        var excluded = new List<StudentPackageExclusion>();
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var examId = string.Empty;
        StudentPackageResult Fail() => new(false,
            new StudentPackagePlan(examId, request.PaperVersion, files.Keys.ToArray(), excluded),
            null, null, issues);
        try
        {
            PathBoundary.RejectExistingReparsePoints(packageRoot, packageRoot);
            PathBoundary.RejectExistingReparsePoints(compilationRoot, compilationRoot);
            PathBoundary.RejectExistingReparsePoints(studentDirectory, studentDirectory);
            if (target is not null)
            {
                PathBoundary.RejectExistingReparsePoints(target, target);
                if (File.Exists(target) || Directory.Exists(target))
                {
                    issues.Add(new(StudentPackageIssueCode.EncryptionFailed, "加密产物已存在，请复用既有产物或使用新的版本路径。"));
                    return Fail();
                }
            }
            var manifestPath = Resolve(packageRoot, StudentPackagePlan.MetadataFileName);
            if (!File.Exists(manifestPath))
            {
                issues.Add(new(StudentPackageIssueCode.MetadataInvalid, "导入包缺少 manifest.json。"));
                return Fail();
            }
            var manifestText = ReadUtf8(ReadBounded(manifestPath, MaxMetadataBytes));
            var validation = new ExamManifestValidator().ValidateJson(manifestText);
            if (!validation.IsValid)
            {
                issues.AddRange(validation.Issues.Select(issue => new StudentPackageIssue(
                    StudentPackageIssueCode.MetadataInvalid, issue.Message, issue.Location)));
                return Fail();
            }
            var manifest = JsonSerializer.Deserialize<ExamManifest>(manifestText, ImportJson.Create())!;
            examId = manifest.ExamId;
            // 学生清单仅包含显示和作答所需字段，不携带导入源路径及计分规则。
            var structure = ImportedGradingRules.BuildStructure(manifest);
            var schemaVersion = request.OptionRegions is null
                ? StudentPaperPackage.LegacySchemaVersion
                : StudentPaperPackage.OptionRegionSchemaVersion;
            var studentManifest = new StudentPaperManifest(schemaVersion, examId, manifest.ExamName, request.PaperVersion,
                manifest.Subjects.Select(s => new StudentPaperSubject(s.SubjectId, s.Name)).ToArray(),
                manifest.Groups.Select(g => new StudentPaperGroup(g.GroupId, g.SubjectId, g.Name, g.Order)).ToArray(),
                manifest.Questions.Select((q, i) => new StudentPaperQuestion(structure.Questions[i],
                    StudentPackageLayout.QuestionPdfPath(q.Source),
                    q.DisplayMetadata?.OptionCaptions)).ToArray(),
                request.DisplayResourcePaths.Select(PackagePathRules.Normalize).ToArray());
            var metadata = JsonSerializer.SerializeToUtf8Bytes(studentManifest, ImportJson.Create());
            if (metadata.Length > MaxMetadataBytes)
            {
                issues.Add(new(StudentPackageIssueCode.MetadataInvalid, "重新生成的学生清单超过容量限制。"));
                return Fail();
            }
            files.Add(StudentPackagePlan.MetadataFileName, metadata);
            long totalBytes = metadata.Length;
            void AddFile(string relative, string source)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (files.Count >= StudentPackageLayout.MaxEntryCount) throw new IOException("学生题包文件数量超限。");
                if (!StudentPackageLayout.IsDisplayPath(relative))
                {
                    issues.Add(new(StudentPackageIssueCode.RequiredResourceRejected, "显示文件路径不符合读写约定。", relative));
                    return;
                }
                if (files.Keys.Any(existing => string.Equals(existing, relative, StringComparison.OrdinalIgnoreCase)))
                {
                    issues.Add(new(StudentPackageIssueCode.RequiredResourceRejected, "包内路径重复。", relative));
                    return;
                }
                var bytes = ReadBounded(source, checked((int)(MaxPackageBytes - totalBytes)));
                // 完整内容的补充字段检查，不按后缀或前 64 KiB 取样放行。
                if (!StudentPackageAllowlist.IsAllowed(relative, Encoding.UTF8.GetString(bytes), out var reason))
                {
                    issues.Add(new(StudentPackageIssueCode.RequiredResourceRejected, reason!, relative));
                    return;
                }
                if (!StudentPackageLayout.HasDisplaySignature(relative, bytes))
                {
                    issues.Add(new(StudentPackageIssueCode.RequiredResourceRejected, "显示文件类型或文件头不符合约定。", relative));
                    return;
                }
                totalBytes += bytes.Length;
                files.Add(relative, bytes);
            }
            void AddOptionRegions(OptionRegionArtifact artifact)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string json;
                try
                {
                    // The canonical writer validates original geometry, unique identifiers and null collections.
                    // Validate before enumerating or quantizing; invalid coordinates are not an encryption error.
                    json = OptionRegionArtifactJson.Serialize(artifact);
                }
                catch (ArgumentException)
                {
                    issues.Add(new(StudentPackageIssueCode.OptionRegionInvalid,
                        "选项坐标原始产物无效，未发布预览或加密产物。", OptionRegionArtifact.FileName));
                    return;
                }
                if (!string.Equals(artifact.ExamId, examId, StringComparison.Ordinal) ||
                    artifact.PaperVersion != request.PaperVersion)
                {
                    issues.Add(new(StudentPackageIssueCode.OptionRegionInvalid,
                        "选项坐标的考试或试卷版本与本次组包不一致。", OptionRegionArtifact.FileName));
                    return;
                }
                if (!OptionRegionArtifact.IsSupportedVersion(artifact.ArtifactVersion) ||
                    !string.Equals(artifact.AnchorScheme, OptionRegionArtifact.AnchorSchemeName, StringComparison.Ordinal))
                {
                    issues.Add(new(StudentPackageIssueCode.OptionRegionInvalid,
                        "选项坐标的版本或坐标规范不受支持。", OptionRegionArtifact.FileName));
                    return;
                }

                // 必须与清单的题目及选项逐一对应：多一个、少一个都不发布。
                var expected = structure.Questions.ToDictionary(
                    question => question.QuestionId,
                    question => question.OptionIds.ToHashSet(StringComparer.Ordinal),
                    StringComparer.Ordinal);
                foreach (var question in artifact.Questions)
                {
                    if (!expected.TryGetValue(question.QuestionId, out var optionIds) ||
                        question.Options.Count != optionIds.Count ||
                        question.Options.Any(option => !optionIds.Contains(option.OptionId)))
                    {
                        issues.Add(new(StudentPackageIssueCode.OptionRegionInvalid,
                            $"选项坐标与清单题目 {question.QuestionId} 的选项不一致。", OptionRegionArtifact.FileName));
                        return;
                    }

                    expected.Remove(question.QuestionId);
                }

                if (expected.Count != 0)
                {
                    issues.Add(new(StudentPackageIssueCode.OptionRegionInvalid,
                        "选项坐标缺少清单中的题目。", OptionRegionArtifact.FileName));
                    return;
                }

                var bytes = Encoding.UTF8.GetBytes(json);
                if (bytes.Length > MaxMetadataBytes || totalBytes + bytes.Length > MaxPackageBytes ||
                    files.Count >= StudentPackageLayout.MaxEntryCount)
                {
                    issues.Add(new(StudentPackageIssueCode.OptionRegionInvalid,
                        "选项坐标文件超出容量限制。", OptionRegionArtifact.FileName));
                    return;
                }

                totalBytes += bytes.Length;
                files.Add(OptionRegionArtifact.FileName, bytes);
            }

            if (request.CompiledQuestions is { } compiled &&
                (compiled.Count != manifest.Questions.Count || compiled.Keys.Any(id => !manifest.Questions.Any(q => q.QuestionId == id))))
                issues.Add(new(StudentPackageIssueCode.QuestionPdfMissing, "编译结果必须与清单中的题目逐一对应。"));
            foreach (var question in manifest.Questions)
            {
                var destination = StudentPackageLayout.QuestionPdfPath(question.Source);
                var pdfName = destination["questions/".Length..];
                var source = Resolve(compilationRoot, pdfName);
                if (request.CompiledQuestions is { } results)
                {
                    if (!results.TryGetValue(question.QuestionId, out var result) || result is null ||
                        !result.Succeeded || result.OutputPdfPath is null || result.Issues.Count != 0 ||
                        !string.Equals(Path.GetFileName(result.OutputPdfPath), pdfName, StringComparison.Ordinal))
                    {
                        issues.Add(new(StudentPackageIssueCode.QuestionPdfMissing, "题目缺少本次成功编译的 PDF。", destination));
                        continue;
                    }
                    source = PathBoundary.ResolveWithinRoot(compilationRoot, result.OutputPdfPath, "编译 PDF 路径越界。");
                }
                if (!File.Exists(source))
                {
                    issues.Add(new(StudentPackageIssueCode.QuestionPdfMissing, "找不到题目编译好的 PDF。", destination));
                    continue;
                }
                AddFile(destination, source);
            }
            if (request.OptionRegions is { } optionRegions)
            {
                AddOptionRegions(optionRegions);
            }

            var resourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in manifest.ResourceRoots)
            {
                var directory = Resolve(packageRoot, PackagePathRules.Normalize(root));
                if (!Directory.Exists(directory))
                {
                    issues.Add(new(StudentPackageIssueCode.ResourceRootMissing, "清单声明的资源目录不存在。", root));
                    continue;
                }
                CollectResources(packageRoot, directory, resourcePaths, cancellationToken);
            }
            var requestedResources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in request.DisplayResourcePaths)
            {
                if (!PackagePathRules.IsRelativeAndInside(path))
                {
                    issues.Add(new(StudentPackageIssueCode.PathEscaped, "显示资源必须使用包内相对路径。", path));
                    continue;
                }
                var relative = PackagePathRules.Normalize(path);
                if (!requestedResources.Add(relative))
                {
                    issues.Add(new(StudentPackageIssueCode.RequiredResourceRejected, "显示资源清单重复。", relative));
                    continue;
                }
                if (!resourcePaths.TryGetValue(relative, out var actual))
                {
                    issues.Add(new(StudentPackageIssueCode.RequiredImageMissing, "显示资源不存在或未处于声明的资源目录中。", relative));
                    continue;
                }
                if (actual.StartsWith("questions/", StringComparison.OrdinalIgnoreCase) ||
                    !StudentPackageAllowlist.IsAllowed(actual, null, out _))
                {
                    issues.Add(new(StudentPackageIssueCode.RequiredResourceRejected, "必需资源被路径规则拒绝。", actual));
                    continue;
                }
                AddFile(actual, Resolve(packageRoot, actual));
            }
            foreach (var path in resourcePaths.Order(StringComparer.Ordinal))
            {
                if (!requestedResources.Contains(path))
                {
                    StudentPackageAllowlist.IsAllowed(path, null, out var reason);
                    excluded.Add(new(path, reason ?? "未列入逐文件显示资源允许清单；编译依赖不随学生包分发。"));
                }
            }
            if (issues.Count != 0) return Fail();
            var finalMetadata = JsonSerializer.SerializeToUtf8Bytes(studentManifest with
            {
                // 坐标文件是专用元数据，不是显示资源：混进 displayResources 会被当成普通显示文件校验，
                // 学生端也会把它当资源遍历。契约 4.4 明确不允许这样加进旧包。
                DisplayResources = files.Keys.Where(path => path != StudentPackagePlan.MetadataFileName &&
                    path != OptionRegionArtifact.FileName &&
                    !path.StartsWith("questions/", StringComparison.Ordinal)).ToArray()
            }, ImportJson.Create());
            if (files.Count > StudentPackageLayout.MaxEntryCount || finalMetadata.Length > MaxMetadataBytes || totalBytes - metadata.Length + finalMetadata.Length > MaxPackageBytes)
                throw new IOException("学生显示清单超出大小限制。");
            files[StudentPackagePlan.MetadataFileName] = finalMetadata;
            var plan = new StudentPackagePlan(examId, request.PaperVersion, files.Keys.ToArray(), excluded);
            if (target is not null)
            {
                var envelope = await EncryptAsync(request, files, target, cancellationToken);
                return new(true, plan, target, envelope, issues);
            }
            WritePreview(files, studentDirectory, cancellationToken);
            return new(true, plan, null, null, issues);
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException)
        {
            issues.Add(new(StudentPackageIssueCode.PathEscaped, "文件路径越界、经过重解析点或没有访问权限。"));
        }
        catch (DecoderFallbackException)
        {
            issues.Add(new(StudentPackageIssueCode.MetadataInvalid, "清单必须使用有效的 UTF-8 编码。"));
        }
        catch (Exception exception) when (exception is IOException or CryptographicException or ArgumentException)
        {
            issues.Add(new(StudentPackageIssueCode.EncryptionFailed, "组包或加密失败，未发布产物。请检查文件、容量及节点证书配置。"));
        }
        return Fail();
    }

    private async Task<EncryptedPackageEnvelope> EncryptAsync(StudentPackageRequest request,
        SortedDictionary<string, byte[]> files, string target, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(target)!;
        PathBoundary.RejectExistingReparsePoints(directory, directory);
        Directory.CreateDirectory(directory);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var plaintext = new MemoryStream();
            using (var zip = new ZipArchive(plaintext, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, bytes) in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using var stream = entry.Open();
                    stream.Write(bytes);
                }
            }
            if (plaintext.Length > MaxPackageBytes) throw new IOException("ZIP 总大小超出学生读取限制。");
            plaintext.Position = 0;
            EncryptedPackageEnvelope envelope;
            await using (var ciphertext = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                envelope = await _encryption.EncryptAsync(plaintext, ciphertext, request.PackageId,
                    request.PaperVersion, request.Recipients ?? [], cancellationToken);
                ciphertext.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            PathBoundary.RejectExistingReparsePoints(target, target);
            File.Move(temporary, target, overwrite: false);
            return envelope;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WritePreview(SortedDictionary<string, byte[]> files, string target, CancellationToken token)
    {
        var parent = Path.GetDirectoryName(target)!;
        PathBoundary.RejectExistingReparsePoints(parent, parent);
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".student-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (relative, bytes) in files)
            {
                token.ThrowIfCancellationRequested();
                var path = Resolve(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }
            token.ThrowIfCancellationRequested();
            PathBoundary.RejectExistingReparsePoints(target, target);
            RequireEmpty(target);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: false);
            Directory.Move(staging, target);
        }
        finally
        {
            // 只清理本次创建的目录，不递归删除请求指定的目标。
            PathBoundary.ResolveWithinRoot(parent, staging, "临时目录越界。");
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static void CollectResources(string root, string directory, HashSet<string> paths, CancellationToken token)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        var count = 0;
        while (pending.TryPop(out var current))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                token.ThrowIfCancellationRequested();
                if (++count > 10000) throw new IOException("资源数量超限。");
                PathBoundary.ResolveWithinRoot(root, entry, "资源路径越界。");
                if (Directory.Exists(entry)) pending.Push(entry);
                else paths.Add(Path.GetRelativePath(root, entry).Replace('\\', '/'));
            }
        }
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new IOException("文件超过组包容量限制。");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("读取期间文件变化。");
        return bytes;
    }

    private static string ReadUtf8(byte[] bytes) => new UTF8Encoding(false, true).GetString(
        bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes.AsSpan(3) : bytes);

    private static string Resolve(string root, string relative) => PathBoundary.ResolveWithinRoot(root,
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), "包内文件路径越界。");

    private static string RequireAbsolute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("必须是绝对路径。", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string RequireDirectory(string path)
    {
        var full = RequireAbsolute(path);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("输入目录不存在。");
        return full;
    }

    private static void RequireEmpty(string path)
    {
        if (File.Exists(path) || Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new InvalidOperationException("学生端包预览目录必须为空。");
    }

    private static void RequireOutside(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == "." || !Path.IsPathFullyQualified(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("产物必须在材料、编译输入及其他输出目录之外。");
    }
}
