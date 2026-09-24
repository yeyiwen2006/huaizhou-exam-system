using System.IO.Compression;
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
    public PaperStructure Structure => new(Questions.Select(q => q.Structure).ToArray());
}

/// <summary>在内存中读取 D03 的 ZIP；绝不将包内路径解压到文件系统。</summary>
public static class StudentPaperPackage
{
    public const int MaxPackageBytes = 256 * 1024 * 1024;
    public const string ManifestPath = "manifest.json";

    public static StudentPaperManifest ReadManifest(byte[] payload, string examId, long paperVersion)
    {
        if (payload.Length > MaxPackageBytes) throw new InvalidDataException("试题包超出大小限制。");
        using var stream = new MemoryStream(payload, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count is 0 or > 10000) throw new InvalidDataException("试题包文件数量无效。");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (!SafePath(entry.FullName) || !paths.Add(entry.FullName) || entry.Length < 0 ||
                entry.Length > MaxPackageBytes || (total += entry.Length) > MaxPackageBytes)
                throw new InvalidDataException("试题包含重复、越界路径或超出大小限制。");
        }
        var metadata = ReadEntry(zip, ManifestPath, 8 * 1024 * 1024);
        using var document = JsonDocument.Parse(metadata);
        RejectDuplicateProperties(document.RootElement);
        var manifest = JsonSerializer.Deserialize<StudentPaperManifest>(metadata, ImportJson.Create())
            ?? throw new InvalidDataException("缺少学生显示清单。");
        ValidateManifest(manifest, examId, paperVersion);
        var expected = new HashSet<string>(StringComparer.Ordinal) { ManifestPath };
        Span<byte> header = stackalloc byte[5];
        foreach (var question in manifest.Questions)
        {
            if (!expected.Add(question.PdfPath) || !question.PdfPath.StartsWith("questions/", StringComparison.Ordinal) ||
                !question.PdfPath.EndsWith(".pdf", StringComparison.Ordinal))
                throw new InvalidDataException("题目 PDF 映射无效。");
            using var pdf = (zip.GetEntry(question.PdfPath) ?? throw new InvalidDataException("题目 PDF 缺失。")).Open();
            pdf.ReadExactly(header);
            if (!header.SequenceEqual("%PDF-"u8)) throw new InvalidDataException("题目 PDF 文件头无效。");
        }
        foreach (var resource in manifest.DisplayResources)
            if (!SafePath(resource) || resource.StartsWith("questions/", StringComparison.OrdinalIgnoreCase) || !expected.Add(resource))
                throw new InvalidDataException("显示资源清单无效。");
        if (!expected.SetEquals(zip.Entries.Select(e => e.FullName)))
            throw new InvalidDataException("显示清单与包内文件不一致。");
        return manifest;
    }

    public static byte[] ReadQuestionPdf(byte[] payload, StudentPaperManifest manifest, string questionId)
    {
        var question = manifest.Questions.SingleOrDefault(q => q.Structure.QuestionId == questionId)
            ?? throw new ArgumentException("未知题目。", nameof(questionId));
        using var stream = new MemoryStream(payload, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        return ReadEntry(zip, question.PdfPath, MaxPackageBytes);
    }

    private static byte[] ReadEntry(ZipArchive zip, string path, int limit)
    {
        var entry = zip.GetEntry(path) ?? throw new InvalidDataException("包内文件缺失。");
        if (entry.Length < 0 || entry.Length > limit) throw new InvalidDataException("文件大小超出限制。");
        var bytes = new byte[checked((int)entry.Length)];
        using var input = entry.Open();
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new InvalidDataException("文件长度不一致。");
        return bytes;
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
                q.OptionIds.Distinct(StringComparer.Ordinal).Count() != q.OptionIds.Count || !SafePath(item!.PdfPath) ||
                (item.OptionCaptions is { } captions && (captions.Count != q.OptionIds.Count || captions.Any(string.IsNullOrWhiteSpace))))
                throw new InvalidDataException("题目清单无效。");
        }
    }

    private static bool SafePath(string? path) => !string.IsNullOrWhiteSpace(path) &&
        !path.Contains('\\') && !path.Any(c => char.IsControl(c) || ":*?\"<>|".Contains(c)) &&
        path.Split('/').All(part => part.Length > 0 && part is not ("." or "..") && !part.EndsWith(' ') && !part.EndsWith('.'));

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
