using System.IO.Compression;
using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;

namespace Exam.Contracts.Resources;

public sealed record StudentPaperQuestion(PaperQuestionStructure Structure, string PdfPath,
    IReadOnlyList<string>? OptionCaptions);
public sealed record StudentPaperSubject(string SubjectId, string Name);
public sealed record StudentPaperGroup(string GroupId, string SubjectId, string Name, int Order);

/// <summary>学生显示清单。没有源文件路径、计分规则或参考答案；坐标未就绪时不提供点击区域。</summary>
public sealed record StudentPaperManifest(int SchemaVersion, string ExamId, string ExamName, long PaperVersion,
    IReadOnlyList<StudentPaperSubject> Subjects, IReadOnlyList<StudentPaperGroup> Groups,
    IReadOnlyList<StudentPaperQuestion> Questions, IReadOnlyList<string> DisplayResources)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public PaperStructure Structure => new(Array.AsReadOnly(Questions.Select(q => q.Structure).ToArray()));
}

/// <summary>在内存中读取 D03 的 ZIP；绝不将包内路径解压到文件系统。</summary>
public static class StudentPaperPackage
{
    public const int MaxPackageBytes = StudentPackageLayout.MaxContentBytes;
    public const string ManifestPath = StudentPackagePlan.MetadataFileName;

    public static StudentPaperManifest ReadManifest(byte[] payload, string examId, long paperVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();
        try { return ReadManifestCore(payload, examId, paperVersion, cancellationToken); }
        catch (JsonException error) { throw new InvalidDataException("学生清单 JSON 或 UTF-8 编码无效。", error); }
    }

    private static StudentPaperManifest ReadManifestCore(byte[] payload, string examId, long paperVersion, CancellationToken cancellationToken)
    {
        if (payload.Length > MaxPackageBytes) throw new InvalidDataException("试题包超出大小限制。");
        using var stream = new MemoryStream(payload, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count is 0 or > StudentPackageLayout.MaxEntryCount) throw new InvalidDataException("试题包文件数量无效。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!StudentPackageLayout.IsSafePath(entry.FullName) || !paths.Add(entry.FullName) || entry.Length < 0 ||
                entry.Length > MaxPackageBytes || (total += entry.Length) > MaxPackageBytes)
                throw new InvalidDataException("试题包含重复、越界路径或超出大小限制。");
        }
        var metadata = ReadEntry(zip, ManifestPath, StudentPackageLayout.MaxMetadataBytes, cancellationToken);
        StudentPaperManifest manifest;
        try
        {
            using var document = JsonDocument.Parse(metadata);
            RejectDuplicateProperties(document.RootElement);
            manifest = JsonSerializer.Deserialize<StudentPaperManifest>(metadata, ImportJson.Create())
                ?? throw new InvalidDataException("缺少学生显示清单。");
        }
        finally { CryptographicOperations.ZeroMemory(metadata); }
        ValidateManifest(manifest, examId, paperVersion);
        var expected = new HashSet<string>(StringComparer.Ordinal) { ManifestPath };
        foreach (var question in manifest.Questions)
        {
            if (!expected.Add(question.PdfPath) || !question.PdfPath.StartsWith("questions/", StringComparison.Ordinal) ||
                !question.PdfPath.EndsWith(".pdf", StringComparison.Ordinal))
                throw new InvalidDataException("题目 PDF 映射无效。");
            ValidateDisplayEntry(zip, question.PdfPath, cancellationToken);
        }
        foreach (var resource in manifest.DisplayResources)
        {
            if (!StudentPackageLayout.IsSafePath(resource) || resource.StartsWith("questions/", StringComparison.OrdinalIgnoreCase) || !expected.Add(resource))
                throw new InvalidDataException("显示资源清单无效。");
            ValidateDisplayEntry(zip, resource, cancellationToken);
        }
        if (!expected.SetEquals(zip.Entries.Select(e => e.FullName)))
            throw new InvalidDataException("显示清单与包内文件不一致。");
        cancellationToken.ThrowIfCancellationRequested();
        return manifest with
        {
            Subjects = Array.AsReadOnly(manifest.Subjects.ToArray()),
            Groups = Array.AsReadOnly(manifest.Groups.ToArray()),
            Questions = Array.AsReadOnly(manifest.Questions.Select(q => q with
            {
                Structure = q.Structure with { OptionIds = Array.AsReadOnly(q.Structure.OptionIds.ToArray()) },
                OptionCaptions = q.OptionCaptions is null ? null : Array.AsReadOnly(q.OptionCaptions.ToArray())
            }).ToArray()),
            DisplayResources = Array.AsReadOnly(manifest.DisplayResources.ToArray())
        };
    }

    public static byte[] ReadQuestionPdf(byte[] payload, StudentPaperManifest manifest, string questionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var question = manifest.Questions.SingleOrDefault(q => q.Structure.QuestionId == questionId)
            ?? throw new ArgumentException("未知题目。", nameof(questionId));
        using var stream = new MemoryStream(payload, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return ReadEntry(zip, question.PdfPath, MaxPackageBytes, cancellationToken);
    }

    private static byte[] ReadEntry(ZipArchive zip, string path, int limit, CancellationToken token = default)
    {
        var entry = zip.GetEntry(path) ?? throw new InvalidDataException("包内文件缺失。");
        if (entry.Length < 0 || entry.Length > limit) throw new InvalidDataException("文件大小超出限制。");
        var bytes = new byte[checked((int)entry.Length)];
        try
        {
            using var input = entry.Open();
            var offset = 0;
            while (offset < bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                var count = input.Read(bytes, offset, Math.Min(64 * 1024, bytes.Length - offset));
                if (count == 0) throw new InvalidDataException("文件内容不完整。");
                offset += count;
            }
            token.ThrowIfCancellationRequested();
            if (input.ReadByte() != -1) throw new InvalidDataException("文件长度不一致。");
            return bytes;
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    private static void ValidateDisplayEntry(ZipArchive zip, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!StudentPackageLayout.IsDisplayPath(path)) throw new InvalidDataException("学生包包含禁止分发的显示路径。");
        var entry = zip.GetEntry(path) ?? throw new InvalidDataException("显示文件缺失。");
        using var input = entry.Open();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var headerLength = checked((int)Math.Min(entry.Length, 8));
            input.ReadExactly(buffer.AsSpan(0, headerLength));
            if (!StudentPackageLayout.HasDisplaySignature(path, buffer.AsSpan(0, headerLength)))
                throw new InvalidDataException("显示文件头无效。");
            var remaining = entry.Length - headerLength;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                var count = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                if (count == 0) throw new InvalidDataException("显示文件内容不完整。");
                remaining -= count;
            }
            token.ThrowIfCancellationRequested();
            if (input.ReadByte() != -1) throw new InvalidDataException("显示文件超出声明长度。");
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static void ValidateManifest(StudentPaperManifest manifest, string examId, long version)
    {
        if (manifest.SchemaVersion != 1 || manifest.ExamId != examId || manifest.PaperVersion != version || version < 1 ||
            string.IsNullOrWhiteSpace(manifest.ExamId) || string.IsNullOrWhiteSpace(manifest.ExamName) ||
            manifest.Subjects is null || manifest.Groups is null || manifest.Questions is null ||
            manifest.DisplayResources is null || manifest.Questions.Count == 0)
            throw new InvalidDataException("学生清单格式、考试或试卷版本不匹配。");
        var subjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subject in manifest.Subjects)
            if (subject is null || string.IsNullOrWhiteSpace(subject.SubjectId) || string.IsNullOrWhiteSpace(subject.Name) || !subjects.Add(subject.SubjectId))
                throw new InvalidDataException("科目清单无效。");
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        var groupOrders = new HashSet<(string SubjectId, int Order)>();
        foreach (var group in manifest.Groups)
            if (group is null || string.IsNullOrWhiteSpace(group.GroupId) || string.IsNullOrWhiteSpace(group.Name) ||
                !subjects.Contains(group.SubjectId) || !groups.TryAdd(group.GroupId, group.SubjectId) ||
                group.Order < 1 || !groupOrders.Add((group.SubjectId, group.Order)))
                throw new InvalidDataException("题组清单无效。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var numbers = new HashSet<int>();
        foreach (var item in manifest.Questions)
        {
            var q = item?.Structure;
            if (q is null || string.IsNullOrWhiteSpace(q.QuestionId) || !ids.Add(q.QuestionId) || q.QuestionNo < 1 ||
                !numbers.Add(q.QuestionNo) || q.GroupId is null || !groups.TryGetValue(q.GroupId, out var subject) || subject != q.SubjectId ||
                q.QuestionType is not ("single" or "multiple" or "indeterminate") ||
                q.OptionIds is null || q.OptionIds.Count is < 2 or > 26 || q.OptionIds.Any(string.IsNullOrWhiteSpace) ||
                q.OptionIds.Distinct(StringComparer.Ordinal).Count() != q.OptionIds.Count || !StudentPackageLayout.IsSafePath(item!.PdfPath) ||
                (item.OptionCaptions is { } captions && (captions.Count != q.OptionIds.Count || captions.Any(string.IsNullOrWhiteSpace))))
                throw new InvalidDataException("题目清单无效。");
        }
    }

    private static void RejectDuplicateProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("显示清单包含重复字段。");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) RejectDuplicateProperties(item);
    }
}
