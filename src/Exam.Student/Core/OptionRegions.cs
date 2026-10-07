using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;

namespace Exam.Student.Core;

/// <summary>
/// 单个选项的定位区域。坐标为左上角原点、[0,1] 归一化值，
/// 契约 anchorScheme = pageTopLeftNormalizedV1（选项区域与题包 选项定位契约 4.3）。
/// </summary>
public sealed record OptionRegion(int PageIndex, double X, double Y, double Width, double Height,
    OptionLabelOverlay? Overlay = null)
{
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;
}

/// <summary>
/// 选项区域与题包 契约 4.4 的 option-regions.json 学生侧读取与严格校验。
/// 只有在题目、选项、页码与 PDF 映射全部核对一致后才允许启用坐标作答；
/// 任何缺失、多余、越界或版本不符均使整份坐标无效（整包失败），不降级、不猜测。
/// </summary>
public sealed class OptionRegionMap
{
    public const string EntryPath = "option-regions.json";
    public const int RequiredArtifactVersion = OptionRegionArtifact.CurrentArtifactVersion;
    public const string RequiredAnchorScheme = "pageTopLeftNormalizedV1";

    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, OptionRegion>> _regions;

    public string ExamId { get; }
    public long PaperVersion { get; }

    private OptionRegionMap(string examId, long paperVersion,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, OptionRegion>> regions)
    {
        ExamId = examId;
        PaperVersion = paperVersion;
        _regions = regions;
    }

    public IReadOnlyDictionary<string, OptionRegion>? ForQuestion(string questionId) =>
        _regions.TryGetValue(questionId, out var options) ? options : null;

    /// <summary>
    /// 从解锁后的包内容读取根目录 option-regions.json。
    /// 文件不存在返回 null（v1 学生清单没有坐标，不猜测点击位置）；
    /// 存在但内容无效时抛出 <see cref="InvalidDataException"/>（整包失败）。
    /// </summary>
    public static OptionRegionMap? TryReadFromPackage(byte[] payload, StudentPaperManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(manifest);
        byte[]? json = null;
        try
        {
            using var stream = new MemoryStream(payload, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entries = zip.Entries.Where(item => item.FullName == EntryPath).ToArray();
            if (entries.Length == 0) { return null; }
            if (entries.Length != 1) throw new InvalidDataException("选项坐标文件在包中重复出现。");
            var entry = entries[0];
            if (entry.Length <= 0 || entry.Length > StudentPackageLayout.MaxMetadataBytes)
                throw new InvalidDataException("选项坐标文件缺失或超出大小限制。");
            json = new byte[checked((int)entry.Length)];
            using var input = entry.Open();
            var offset = 0;
            while (offset < json.Length)
            {
                var count = input.Read(json, offset, json.Length - offset);
                if (count == 0) { throw new InvalidDataException("选项坐标文件内容不完整。"); }
                offset += count;
            }
            if (input.ReadByte() != -1) { throw new InvalidDataException("选项坐标文件长度不一致。"); }
            return Parse(json, manifest);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("选项坐标 JSON 或 UTF-8 编码无效。", error);
        }
        finally
        {
            if (json is not null) { Array.Clear(json); }
        }
    }

    /// <summary>解析并按学生清单逐题逐选项完整核对；失败即整包失败，不产出部分结果。</summary>
    public static OptionRegionMap Parse(byte[] json, StudentPaperManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(manifest);
        if (json.Length == 0 || json.Length > StudentPackageLayout.MaxMetadataBytes)
            throw new InvalidDataException("选项坐标文件大小无效。");

        RegionFileDocument file;
        try
        {
            using var document = JsonDocument.Parse(json);
            RejectDuplicateProperties(document.RootElement);
            var options = ImportJson.Create();
            options.RespectRequiredConstructorParameters = true;
            file = JsonSerializer.Deserialize<RegionFileDocument>(json, options)
                ?? throw new InvalidDataException("缺少选项坐标内容。");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("选项坐标 JSON 或 UTF-8 编码无效。", error);
        }

        if (!OptionRegionArtifact.IsSupportedVersion(file.ArtifactVersion) || file.AnchorScheme != RequiredAnchorScheme)
            throw new InvalidDataException("选项坐标版本或锚点方案不受支持。");
        if (!string.Equals(file.ExamId, manifest.ExamId, StringComparison.Ordinal) ||
            file.PaperVersion != manifest.PaperVersion)
            throw new InvalidDataException("选项坐标的考试或试卷版本与学生清单不一致。");
        if (file.Questions is null || file.Questions.Count == 0)
            throw new InvalidDataException("选项坐标缺少题目集合。");

        // 每个清单题目及选项恰好出现一次：缺失、重复、清单外标记均失败。
        var byQuestion = new Dictionary<string, Dictionary<string, OptionRegion>>(StringComparer.Ordinal);
        foreach (var question in file.Questions)
        {
            if (question is null || string.IsNullOrWhiteSpace(question.QuestionId) || question.Options is null ||
                question.Options.Count == 0)
                throw new InvalidDataException("选项坐标题目记录无效。");
            if (byQuestion.ContainsKey(question.QuestionId))
                throw new InvalidDataException($"题目 {question.QuestionId} 的选项坐标重复出现。");
            var options = new Dictionary<string, OptionRegion>(StringComparer.Ordinal);
            foreach (var option in question.Options)
            {
                if (option is null || string.IsNullOrWhiteSpace(option.OptionId))
                    throw new InvalidDataException($"题目 {question.QuestionId} 存在无效选项坐标记录。");
                if (option.Overlay is not null && file.ArtifactVersion < 2)
                    throw new InvalidDataException("旧版坐标不能包含新版选项显示信息。");
                if (!options.TryAdd(option.OptionId, ValidateRegion(question.QuestionId, option)))
                    throw new InvalidDataException($"题目 {question.QuestionId} 选项 {option.OptionId} 的坐标重复出现。");
            }
            byQuestion[question.QuestionId] = options;
        }

        foreach (var item in manifest.Questions)
        {
            var structure = item.Structure;
            if (!byQuestion.TryGetValue(structure.QuestionId, out var options))
                throw new InvalidDataException($"题目 {structure.QuestionId} 缺少选项坐标。");
            foreach (var optionId in structure.OptionIds)
            {
                if (!options.ContainsKey(optionId))
                    throw new InvalidDataException($"题目 {structure.QuestionId} 选项 {optionId} 缺少坐标。");
            }
            if (options.Count != structure.OptionIds.Count)
                throw new InvalidDataException($"题目 {structure.QuestionId} 的坐标包含清单外选项。");
        }
        if (byQuestion.Count != manifest.Questions.Count)
            throw new InvalidDataException("选项坐标包含清单外题目。");

        var frozen = byQuestion.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, OptionRegion>)new ReadOnlyDictionary<string, OptionRegion>(pair.Value),
            StringComparer.Ordinal);
        return new OptionRegionMap(file.ExamId!, file.PaperVersion, frozen);
    }

    /// <summary>显示前按该题 PDF 实际页数核对 pageIndex；越界即整包失败。</summary>
    public static string? ValidatePageCount(
        IReadOnlyDictionary<string, OptionRegion>? questionRegions, int pageCount, string questionId)
    {
        if (questionRegions is null) { return null; }
        foreach (var (optionId, region) in questionRegions)
        {
            if (region is null || pageCount <= 0 || region.PageIndex < 0 || region.PageIndex >= pageCount ||
                !double.IsFinite(region.X) || !double.IsFinite(region.Y) ||
                !double.IsFinite(region.Width) || !double.IsFinite(region.Height) ||
                region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
                region.X + region.Width > 1 || region.Y + region.Height > 1 ||
                region.Overlay is { } overlay && !overlay.IsValid())
                return $"题目 {questionId} 选项 {optionId} 的页码或区域无效（该题 PDF 实际页数 {pageCount}）。";
        }
        return null;
    }

    private static OptionRegion ValidateRegion(string questionId, RegionOptionDocument option)
    {
        if (option.PageIndex < 0)
            throw new InvalidDataException($"题目 {questionId} 选项 {option.OptionId} 的页码为负。");
        var values = new[] { option.X, option.Y, option.Width, option.Height };
        if (values.Any(double.IsNaN) || values.Any(double.IsInfinity))
            throw new InvalidDataException($"题目 {questionId} 选项 {option.OptionId} 的坐标包含非有限数值。");
        if (option.X < 0 || option.Y < 0 || option.Width <= 0 || option.Height <= 0 ||
            option.X + option.Width > 1 || option.Y + option.Height > 1)
            throw new InvalidDataException($"题目 {questionId} 选项 {option.OptionId} 的坐标越出页面范围。");
        if (option.Overlay is { } overlay && !overlay.IsValid())
            throw new InvalidDataException($"题目 {questionId} 选项 {option.OptionId} 的显示字母或遮盖范围无效。");
        return new OptionRegion(option.PageIndex, option.X, option.Y, option.Width, option.Height, option.Overlay);
    }

    private static void RejectDuplicateProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) { throw new InvalidDataException("选项坐标包含重复字段。"); }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray()) { RejectDuplicateProperties(item); }
        }
    }

    private sealed record RegionFileDocument(
        int ArtifactVersion, string? AnchorScheme, string? ExamId, long PaperVersion,
        IReadOnlyList<RegionQuestionDocument>? Questions);

    private sealed record RegionQuestionDocument(string? QuestionId, IReadOnlyList<RegionOptionDocument>? Options);

    private sealed record RegionOptionDocument(
        string? OptionId, int PageIndex, double X, double Y, double Width, double Height,
        OptionLabelOverlay? Overlay = null);
}
