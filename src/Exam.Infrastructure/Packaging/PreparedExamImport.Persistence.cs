using System.Security.Cryptography;
using System.Text.Json;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Domain.Importing;
using Exam.Domain.Scoring;

namespace Exam.Infrastructure.Packaging;

public sealed partial class PreparedExamImport
{
    private Dictionary<string, string> _fingerprints = new(StringComparer.Ordinal);
    private string? _ciphertextHash;
    internal SemaphoreSlim PublicationGate { get; } = new(1, 1);
    internal long ConnectionGeneration { get; set; }
    internal string? LogicalServerId { get; set; }
    internal long ExpectedSettingsVersion { get; set; }
    public string ExamName => Manifest.ExamName;
    public string PreviewDirectory => Workspace.Root;
    public bool PublicationAttempted => Upload is not null;

    internal async Task SealAsync(CancellationToken ct)
    {
        var paths = SourceFiles(Workspace.PackageRoot)
            .Concat(Compiled.Values.Select(x => x.OutputPdfPath!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var path in paths)
        {
            var relative = Path.GetRelativePath(Workspace.Root, path).Replace('\\', '/');
            _fingerprints.Add(relative, await HashAsync(path, ct));
        }

        await SealMaterialsArchiveAsync(ct);
    }

    /// <summary>预览里的源材料归档文件名（含解密后上传前那份密文）。</summary>
    internal const string MaterialsArchiveFileName = "materials.zip";

    internal const string MaterialsCiphertextFileName = "materials.enc";

    /// <summary>
    /// 封存时生成源材料归档：**只**包含材料目录下的文件（清单、题目 `.tex`、共享样式、图片、独立答案），
    /// 路径相对材料包根。归档随预览一起保存并纳入指纹，因此「丢回执后用同一预览重试」
    /// 与跨进程重放上传的都是**同一份字节**——否则重建 ZIP 的字节差异会被判成幂等冲突。
    /// </summary>
    private async Task SealMaterialsArchiveAsync(CancellationToken ct)
    {
        var files = new List<KeyValuePair<string, byte[]>>();
        foreach (var path in SourceFiles(Workspace.PackageRoot))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(Workspace.PackageRoot, path).Replace('\\', '/');
            files.Add(new KeyValuePair<string, byte[]>(relative, await File.ReadAllBytesAsync(path, ct)));
        }

        var archive = PaperMaterialsArchive.Build(Expected.ExamId, TargetPaperVersion, files);
        await File.WriteAllBytesAsync(MaterialsArchivePath, archive, ct);
        // 与 HashAsync 同一写法（大写十六进制）：指纹表里不能混两种大小写。
        _fingerprints.Add(MaterialsArchiveFileName, Convert.ToHexString(SHA256.HashData(archive)));
    }

    /// <summary>发布前核对归档未变：与密文一样，改了就不能当作同一请求重试。</summary>
    internal async Task VerifyMaterialsAsync(CancellationToken ct)
    {
        if (MaterialsSha256 is not { Length: > 0 } expected ||
            !StringComparer.Ordinal.Equals(expected, await HashAsync(MaterialsArchivePath, ct)))
            throw new InvalidDataException("本次预览的源材料归档已变化或缺失，请重新检查试题包。");
    }

    internal string MaterialsArchivePath => Path.Combine(Workspace.Root, MaterialsArchiveFileName);

    internal string MaterialsCiphertextPath => Path.Combine(Workspace.Root, MaterialsCiphertextFileName);

    /// <summary>
    /// 明文归档的 SHA-256（十六进制），随发布请求提交并由服务器按收到的字节重算比对。
    /// 直接读指纹表——它是随预览导出/载入持久化的，因此跨进程重放也拿得到同一个值。
    /// </summary>
    internal string? MaterialsSha256 => _fingerprints.GetValueOrDefault(MaterialsArchiveFileName);

    internal async Task VerifyUnchangedAsync(CancellationToken ct)
    {
        if (_fingerprints.Count == 0) throw new InvalidDataException("预览缺少本次材料校验记录，请重新生成预览。");
        var actualSource = SourceFiles(Workspace.PackageRoot)
            .Select(p => Path.GetRelativePath(Workspace.Root, p).Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        var expectedSource = _fingerprints.Keys.Where(p => p.StartsWith("source/", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        if (!actualSource.SetEquals(expectedSource)) throw new InvalidDataException("预览材料的文件集合已改变，请重新检查。");
        foreach (var (relative, expected) in _fingerprints)
        {
            var path = ResolveSnapshotPath(Workspace.Root, relative);
            if (!StringComparer.Ordinal.Equals(expected, await HashAsync(path, ct)))
                throw new InvalidDataException("预览后材料或题目 PDF 已被修改，请重新检查，不能发布旧预览。");
        }
    }

    internal async Task SaveUploadAsync(CancellationToken ct)
    {
        if (Upload is null) throw new InvalidOperationException("尚未生成正式发布请求。");
        _ciphertextHash ??= await HashAsync(Workspace.EncryptedPath, ct);
        await VerifyCiphertextAsync(ct);
        if (Workspace.Persistent)
            await WriteAtomicAsync(Path.Combine(Workspace.Root, "upload.json"), new SavedUpload(Upload, _ciphertextHash), ct);
    }

    internal async Task VerifyCiphertextAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_ciphertextHash) || _ciphertextHash != await HashAsync(Workspace.EncryptedPath, ct))
            throw new InvalidDataException("已准备的加密包发生变化，请保留现场并核对原发布结果，不能作为同一请求重试。");
    }

    public bool MatchesPublication(ExamVersionSnapshot actual) =>
        actual.ExamId == Expected.ExamId && actual.Paper?.ArtifactId == ResourceId &&
        actual.Paper.Version == TargetPaperVersion && actual.Versions.PaperVersion == TargetPaperVersion &&
        actual.Versions.GradingVersion == Expected.Versions.GradingVersion + 1 &&
        actual.Grading?.Version == actual.Versions.GradingVersion &&
        actual.Grading.RuleSetId == "bundle-rules-" + ResourceId;

    /// <summary>导出新的私有预览目录。只有本次已校验源材料和 PDF 被复制，不覆盖用户已有目录。</summary>
    public async Task ExportAsync(string destination, CancellationToken ct = default)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(PreparedExamImport));
        if (Upload is not null) throw new InvalidOperationException("已尝试发布的预览不能另存为第二个发布副本。");
        await VerifyUnchangedAsync(ct);
        var target = Path.GetFullPath(destination);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("预览输出目录已存在，请选择新的目录。");
        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("预览输出目录无效。");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("预览输出目录的父目录不存在。");
        ExamImportWorkspace.RejectLinks(parent);
        var staging = Path.Combine(parent, ".c05-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        ExamImportWorkspace.RestrictAccess(staging);
        try
        {
            foreach (var relative in _fingerprints.Keys)
            {
                var input = ResolveSnapshotPath(Workspace.Root, relative);
                var output = ResolveSnapshotPath(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await using var source = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
                await using var sink = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(sink, ct);
            }
            var compiled = Compiled.ToDictionary(x => x.Key, x => x.Value with
            { OutputPdfPath = Path.GetRelativePath(Workspace.Root, x.Value.OutputPdfPath!).Replace('\\', '/') }, StringComparer.Ordinal);
            var questions = Questions.Select(x => x with
            { PdfPath = Path.GetRelativePath(Workspace.Root, x.PdfPath).Replace('\\', '/') }).ToArray();
            await WriteAtomicAsync(Path.Combine(staging, "preview.json"),
                new SavedPreview(2, Source, Expected, ExpectedSettingsVersion, LogicalServerId!, ResourceId,
                    RequestId, compiled, questions, _fingerprints,
                    Reason, DeclaredChangedQuestions, ChangedQuestionsHint), ct);
            // Prove the exported bytes, not only the original preview, match the confirmed snapshot.
            foreach (var (relative, hash) in _fingerprints)
                if (hash != await HashAsync(ResolveSnapshotPath(staging, relative), ct))
                    throw new InvalidDataException("保存预览时材料发生变化，未保留不完整预览。");
            ct.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    /// <summary>重用已检查的源材料、PDF、请求编号及密文；绝不在发布时悄悄重新编译。</summary>
    public static async Task<PreparedExamImport> LoadAsync(string directory, IExamPublicationClient api, CancellationToken ct = default)
    {
        var workspace = ExamImportWorkspace.OpenPersistent(directory);
        PreparedExamImport? prepared = null;
        try
        {
            var json = JsonDefaults.Create();
            var state = JsonSerializer.Deserialize<SavedPreview>(await ExamImportWorkspace.ReadUtf8Async(
                Path.Combine(workspace.Root, "preview.json"), ct), json) ?? throw new InvalidDataException("预览记录无效。");
            if (state.FormatVersion != 2 || state.Expected is null || string.IsNullOrWhiteSpace(state.ServerId) ||
                string.IsNullOrWhiteSpace(state.Reason) || state.DeclaredChangedQuestions is not { Count: > 0 } ||
                state.Compiled is null || state.Questions is null || state.Hashes is not { Count: > 0 and <= 4096 } ||
                state.ResourceId is null || !state.ResourceId.StartsWith("c05-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(state.ResourceId[4..], "N", out _) || !Guid.TryParseExact(state.RequestId, "N", out _))
                throw new InvalidDataException("预览记录格式或发布身份无效。");
            var manifestJson = await ExamImportWorkspace.ReadUtf8Async(Path.Combine(workspace.PackageRoot, "manifest.json"), ct);
            var answerJson = await ExamImportWorkspace.ReadUtf8Async(Path.Combine(workspace.PackageRoot, "server-only", "answer-key.json"), ct);
            ImportedGradingRules.Create(manifestJson, answerJson);
            var manifest = JsonSerializer.Deserialize<ExamManifest>(manifestJson, ImportJson.Create())!;
            if (manifest.ExamId != state.Expected.ExamId || state.Compiled.Count != manifest.Questions.Count ||
                state.Questions.Count != manifest.Questions.Count || manifest.Questions.Any(q => !state.Compiled.ContainsKey(q.QuestionId)))
                throw new InvalidDataException("预览记录的考试或题目与材料不一致。");
            foreach (var item in state.Compiled.Values)
                if (item is null || !item.Succeeded || item.TimedOut || item.ExitCode != 0 ||
                    item.OutputPdfPath is null || !item.OutputPdfPath.StartsWith("compiled/", StringComparison.Ordinal) ||
                    !state.Hashes.ContainsKey(item.OutputPdfPath) || item.Report is null || !item.Report.IsClean ||
                    item.Issues is not { Count: 0 })
                    throw new InvalidDataException("预览缺少完整的成功编译记录或 PDF 校验记录。");
            var compiled = state.Compiled.ToDictionary(x => x.Key, x => x.Value with
            { OutputPdfPath = ResolveSnapshotPath(workspace.Root, x.Value.OutputPdfPath!) }, StringComparer.Ordinal);
            var questions = state.Questions.Select(q => q with { PdfPath = ResolveSnapshotPath(workspace.Root, q.PdfPath) }).ToArray();
            prepared = new PreparedExamImport(workspace, manifest, manifestJson, answerJson, state.Expected,
                compiled, questions, state.Source, state.Reason, state.DeclaredChangedQuestions,
                state.ChangedQuestionsHint ?? [])
            {
                ResourceId = state.ResourceId, RequestId = state.RequestId, ExpectedSettingsVersion = state.SettingsVersion,
                LogicalServerId = state.ServerId, ConnectionGeneration = api.Generation, _fingerprints = state.Hashes
            };
            await prepared.VerifyUnchangedAsync(ct);
            await api.GetExamAsync(state.Expected.ExamId, ct); // Read only: establish the logical server, not success.
            if (api.Generation != prepared.ConnectionGeneration || api.ServerId != state.ServerId)
                throw new InvalidOperationException("预览不属于当前服务器，请连接生成该预览时的服务器。");
            var uploadPath = Path.Combine(workspace.Root, "upload.json");
            if (File.Exists(uploadPath))
            {
                var saved = JsonSerializer.Deserialize<SavedUpload>(await ExamImportWorkspace.ReadUtf8Async(uploadPath, ct), json)
                    ?? throw new InvalidDataException("原发布请求记录无效。");
                var publication = saved.Upload?.Publication;
                if (publication is null || publication.RequestId != prepared.RequestId || publication.ResourceId != prepared.ResourceId ||
                    publication.ExpectedPaperVersion != prepared.Expected.Versions.PaperVersion ||
                    publication.ExpectedGradingVersion != prepared.Expected.Versions.GradingVersion ||
                    publication.ManifestJson != manifestJson || publication.AnswerKeyJson != answerJson)
                    throw new InvalidDataException("原发布请求与预览不一致，拒绝用改变的请求重试。");
                prepared.Upload = saved.Upload;
                prepared._ciphertextHash = saved.CiphertextHash;
                await prepared.VerifyCiphertextAsync(ct);
            }
            return prepared;
        }
        catch { if (prepared is not null) prepared.Dispose(); else workspace.Dispose(); throw; }
    }

    private static IEnumerable<string> SourceFiles(string root)
    {
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        var count = 0;
        while (stack.Count > 0)
        {
            var (folder, depth) = stack.Pop();
            ExamImportWorkspace.RejectLinks(folder);
            if (depth > 32) throw new InvalidDataException("预览目录过深。");
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                ExamImportWorkspace.RejectLinks(path);
                if (++count > 2048) throw new InvalidDataException("预览材料数量超过上限。");
                if (Directory.Exists(path)) stack.Push((path, depth + 1));
                else yield return path;
            }
        }
    }

    private static string ResolveSnapshotPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') ||
            relative.Length > 1024 || relative.Split('/').Any(p => p is "." or ".." || !WindowsFileNameRules.IsValidFileName(p)) ||
            !(relative.StartsWith("source/", StringComparison.Ordinal) ||
                relative.StartsWith("compiled/", StringComparison.Ordinal) ||
                relative == MaterialsArchiveFileName))
            throw new InvalidDataException("预览记录含有非法或越界路径。");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("预览文件路径越界。");
        ExamImportWorkspace.RejectLinks(full);
        return full;
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        ExamImportWorkspace.RejectLinks(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 0 or > 256L * 1024 * 1024) throw new InvalidDataException("预览文件超过允许大小。");
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    private static async Task WriteAtomicAsync<T>(string path, T value, CancellationToken ct)
    {
        ExamImportWorkspace.RejectLinks(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonDefaults.Create(), ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>
    /// 预览记录。**格式版本 2** 起带上发布原因、声明的变更题号与差异提示：
    /// 声明是发布请求的一部分，必须在预览阶段定稿（发布只重放同一份已保存的请求），
    /// 因此旧版预览（v1）不再可用，载入时要求重新检查材料。
    /// </summary>
    private sealed record SavedPreview(int FormatVersion, string Source, ExamVersionSnapshot Expected,
        long SettingsVersion, string ServerId, string ResourceId, string RequestId,
        Dictionary<string, TeXCompileResult> Compiled, IReadOnlyList<ImportPreviewQuestion> Questions,
        Dictionary<string, string> Hashes, string Reason,
        IReadOnlyList<string> DeclaredChangedQuestions, IReadOnlyList<string>? ChangedQuestionsHint);
    private sealed record SavedUpload(UploadExamBundleRequest Upload, string CiphertextHash);
}
