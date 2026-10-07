using System.Globalization;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Importing;

namespace Exam.Infrastructure.Compilation;

/// <summary>
/// <c>option-regions.json</c> 的规范读写。字段名、出现顺序与坐标精度在此固定，按此解析。
/// 契约见 docs/option-region-contract.md 第 4.4 节。
///
/// 写出前校验原始区域，不能通过舍入隐藏越界。合法坐标按最短往返十进制表示向零舍入到
/// <see cref="CoordinateDecimals"/> 位；小于精度的正数保留原值，避免宽高变为零。
/// 已规范化的坐标再次写出保持不变。
/// </summary>
public static class OptionRegionArtifactJson
{
    /// <summary>归一化坐标写出的小数位。1e-6 页宽在显示上远小于一个像素，足够表达点击区域。</summary>
    public const int CoordinateDecimals = 6;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// 缩进固定为两个空格、换行固定为 <c>\n</c>、属性顺序即写出顺序：产物必须与平台无关，
    /// 否则同一份试卷在不同操作系统上会产出字节不同的文件，无法按版本比对。
    /// </summary>
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, NewLine = "\n" };

    public static string Serialize(OptionRegionArtifact artifact)
    {
        ValidateArtifact(artifact);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("artifactVersion", artifact.ArtifactVersion);
            writer.WriteString("anchorScheme", artifact.AnchorScheme);
            writer.WriteString("examId", artifact.ExamId);
            writer.WriteNumber("paperVersion", artifact.PaperVersion);

            writer.WriteStartArray("questions");
            foreach (var question in artifact.Questions)
            {
                writer.WriteStartObject();
                writer.WriteString("questionId", question.QuestionId);

                writer.WriteStartArray("options");
                foreach (var option in question.Options)
                {
                    writer.WriteStartObject();
                    writer.WriteString("optionId", option.OptionId);
                    writer.WriteNumber("pageIndex", option.PageIndex);
                    writer.WriteNumber("x", Round(option.X));
                    writer.WriteNumber("y", Round(option.Y));
                    writer.WriteNumber("width", Round(option.Width));
                    writer.WriteNumber("height", Round(option.Height));
                    if (option.Overlay is { } overlay)
                    {
                        writer.WriteStartObject("overlay");
                        writer.WriteString("text", overlay.Text);
                        writer.WriteNumber("x", Round(overlay.X));
                        writer.WriteNumber("y", Round(overlay.Y));
                        writer.WriteNumber("width", Round(overlay.Width));
                        writer.WriteNumber("height", Round(overlay.Height));
                        writer.WriteEndObject();
                    }
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        // 文本文件以换行结尾，便于按行读取与版本比对。
        return System.Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>
    /// 解析并校验坐标产物。任何不合法都不产出部分结果：字段缺失、类型不符、重复 JSON 字段、
    /// 未知字段、版本或坐标规范不符、重复标识、非有限数、越界区域一律失败。
    /// </summary>
    public static OptionRegionExtractionResult Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Fail(OptionRegionIssueCode.RequestInvalid, "坐标文件为空");

        try
        {
            // Reject unpaired UTF-16 surrogates before the JSON APIs can replace them.
            StrictUtf8.GetByteCount(json);
            return DeserializeCore(json);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or EncoderFallbackException)
        {
            // GetString also throws InvalidOperationException for escaped invalid surrogates.
            return Fail(OptionRegionIssueCode.RequestInvalid, "坐标文件不是合法 JSON 或包含无效 Unicode");
        }
    }

    private static OptionRegionExtractionResult DeserializeCore(string json)
    {
        if (HasDuplicateProperties(json))
            return Fail(OptionRegionIssueCode.RequestInvalid, "坐标文件存在重复的 JSON 字段");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Fail(OptionRegionIssueCode.RequestInvalid, "坐标文件不是合法 JSON");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fail(OptionRegionIssueCode.RequestInvalid, "坐标文件根必须是对象");

            if (UnexpectedProperty(root, ["artifactVersion", "anchorScheme", "examId", "paperVersion", "questions"])
                is { } unknown)
                return Fail(OptionRegionIssueCode.RequestInvalid, $"坐标文件含未知字段 {unknown}");

            if (!TryInt(root, "artifactVersion", out var artifactVersion) ||
                !OptionRegionArtifact.IsSupportedVersion(artifactVersion))
                return Fail(OptionRegionIssueCode.RequestInvalid,
                    "artifactVersion 必须为 1 或 2");

            if (!TryText(root, "anchorScheme", out var anchorScheme) ||
                !string.Equals(anchorScheme, OptionRegionArtifact.AnchorSchemeName, StringComparison.Ordinal))
                return Fail(OptionRegionIssueCode.RequestInvalid,
                    $"anchorScheme 必须为 {OptionRegionArtifact.AnchorSchemeName}");

            if (!TryText(root, "examId", out var examId) || string.IsNullOrWhiteSpace(examId))
                return Fail(OptionRegionIssueCode.RequestInvalid, "examId 不能为空");

            if (!TryLong(root, "paperVersion", out var paperVersion) || paperVersion < 1)
                return Fail(OptionRegionIssueCode.RequestInvalid, "paperVersion 必须为正整数");

            if (!root.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array ||
                questions.GetArrayLength() == 0)
                return Fail(OptionRegionIssueCode.RequestInvalid, "questions 必须是非空数组");

            var result = new List<OptionRegionQuestion>();
            var questionIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var question in questions.EnumerateArray())
            {
                var parsed = ParseQuestion(question, questionIds, artifactVersion);
                if (parsed.Issue is { } issue) return OptionRegionExtractionResult.Failure([issue]);
                result.Add(parsed.Question!);
            }

            return new OptionRegionExtractionResult(
                new OptionRegionArtifact(
                    artifactVersion, anchorScheme, examId, paperVersion, result),
                []);
        }
    }

    private static (OptionRegionQuestion? Question, OptionRegionIssue? Issue) ParseQuestion(
        JsonElement question,
        HashSet<string> questionIds, int artifactVersion)
    {
        if (question.ValueKind != JsonValueKind.Object)
            return (null, Invalid("题目项必须是对象"));
        if (UnexpectedProperty(question, ["questionId", "options"]) is { } unknown)
            return (null, Invalid($"题目项含未知字段 {unknown}"));
        if (!TryText(question, "questionId", out var questionId) || string.IsNullOrWhiteSpace(questionId))
            return (null, Invalid("questionId 不能为空"));
        if (!questionIds.Add(questionId))
            return (null, Invalid($"题目标识重复：{questionId}", questionId));
        if (!question.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array ||
            options.GetArrayLength() == 0)
            return (null, Invalid("options 必须是非空数组", questionId));

        var parsedOptions = new List<OptionRegion>();
        var optionIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object)
                return (null, Invalid("选项项必须是对象", questionId));
            if (UnexpectedProperty(option, ["optionId", "pageIndex", "x", "y", "width", "height", "overlay"]) is { } optionUnknown)
                return (null, Invalid($"选项项含未知字段 {optionUnknown}", questionId));
            if (!TryText(option, "optionId", out var optionId) || string.IsNullOrWhiteSpace(optionId))
                return (null, Invalid("optionId 不能为空", questionId));
            if (!optionIds.Add(optionId))
                return (null, Invalid($"选项标识重复：{optionId}", questionId, optionId));
            if (!TryInt(option, "pageIndex", out var pageIndex) || pageIndex < 0)
                return (null, Invalid("pageIndex 必须是从 0 开始的整数", questionId, optionId));
            if (!TryNumber(option, "x", out var x) || !TryNumber(option, "y", out var y) ||
                !TryNumber(option, "width", out var width) || !TryNumber(option, "height", out var height))
                return (null, Invalid("坐标必须是有限数", questionId, optionId));

            if (!IsValidRegion(x, y, width, height))
                return (null, new OptionRegionIssue(
                    OptionRegionIssueCode.RegionOutOfBounds, "坐标越出 [0,1] 或宽高非正",
                    questionId, optionId, pageIndex));

            OptionLabelOverlay? overlay = null;
            if (option.TryGetProperty("overlay", out var display) && display.ValueKind != JsonValueKind.Null)
            {
                if (artifactVersion < 2 || display.ValueKind != JsonValueKind.Object ||
                    UnexpectedProperty(display, ["text", "x", "y", "width", "height"]) is not null ||
                    !TryText(display, "text", out var text) || string.IsNullOrWhiteSpace(text) ||
                    !TryNumber(display, "x", out var coverX) || !TryNumber(display, "y", out var coverY) ||
                    !TryNumber(display, "width", out var coverWidth) || !TryNumber(display, "height", out var coverHeight) ||
                    !IsValidRegion(coverX, coverY, coverWidth, coverHeight))
                    return (null, Invalid("选项显示字母或遮盖范围无效", questionId, optionId));
                overlay = new OptionLabelOverlay(text, coverX, coverY, coverWidth, coverHeight);
            }
            parsedOptions.Add(new OptionRegion(optionId, pageIndex, x, y, width, height, overlay));
        }

        return (new OptionRegionQuestion(questionId, parsedOptions), null);
    }

    private static OptionRegionIssue Invalid(string message, string? questionId = null, string? optionId = null) =>
        new(OptionRegionIssueCode.RequestInvalid, message, questionId, optionId);

    private static OptionRegionExtractionResult Fail(OptionRegionIssueCode code, string message) =>
        OptionRegionExtractionResult.Failure([new OptionRegionIssue(code, message)]);

    private static void ValidateArtifact(OptionRegionArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (!OptionRegionArtifact.IsSupportedVersion(artifact.ArtifactVersion) ||
            artifact.AnchorScheme != OptionRegionArtifact.AnchorSchemeName || artifact.PaperVersion < 1)
            throw new ArgumentException("坐标产物版本、坐标规范或试卷版本无效。", nameof(artifact));
        RequireText(artifact.ExamId, nameof(artifact.ExamId));
        if (artifact.Questions is null || artifact.Questions.Count == 0)
            throw new ArgumentException("坐标产物必须包含题目。", nameof(artifact));

        var questionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in artifact.Questions)
        {
            ArgumentNullException.ThrowIfNull(question);
            RequireText(question.QuestionId, nameof(question.QuestionId));
            if (!questionIds.Add(question.QuestionId) || question.Options is null || question.Options.Count == 0)
                throw new ArgumentException("题目标识重复或缺少选项。", nameof(artifact));
            var optionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in question.Options)
            {
                ArgumentNullException.ThrowIfNull(option);
                RequireText(option.OptionId, nameof(option.OptionId));
                if (!optionIds.Add(option.OptionId) || option.PageIndex < 0 ||
                    !IsValidRegion(option.X, option.Y, option.Width, option.Height))
                    throw new ArgumentException("选项标识重复、页索引无效或原始区域越界。", nameof(artifact));
                if (option.Overlay is { } overlay)
                {
                    if (artifact.ArtifactVersion < 2 || !overlay.IsValid())
                        throw new ArgumentException("选项显示字母或遮盖范围无效。", nameof(artifact));
                    RequireText(overlay.Text, nameof(overlay.Text));
                }
            }
        }
    }

    private static void RequireText(string? value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        StrictUtf8.GetByteCount(value);
    }

    private static bool IsValidRegion(double x, double y, double width, double height) =>
        double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(width) && double.IsFinite(height) &&
        x >= 0 && y >= 0 && width > 0 && height > 0 && x + width <= 1 && y + height <= 1;

    /// <summary>按往返十进制向零舍入；小于精度的正数保留，不以二进制乘法再次截掉末位。</summary>
    private static double Round(double value)
    {
        if (value == 0) return 0; // Canonicalize negative zero.
        if (value < 0.000001) return value;
        var decimalValue = decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture),
            NumberStyles.Float, CultureInfo.InvariantCulture);
        return (double)decimal.Round(decimalValue, CoordinateDecimals, MidpointRounding.ToZero);
    }

    private static string? UnexpectedProperty(JsonElement element, string[] allowed)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) return property.Name;
        }

        return null;
    }

    private static bool TryText(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString()!;
        return true;
    }

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }

    private static bool TryLong(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt64(out value);
    }

    private static bool TryNumber(JsonElement element, string name, out double value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number &&
               property.TryGetDouble(out value) && double.IsFinite(value);
    }

    /// <summary>逐层检测同一对象内的重复属性名。JsonDocument 会静默保留后者，必须在解析前拦下。</summary>
    private static bool HasDuplicateProperties(string json)
    {
        try
        {
            var reader = new Utf8JsonReader(StrictUtf8.GetBytes(json));
            var stack = new Stack<HashSet<string>>();

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        stack.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.EndObject:
                        stack.Pop();
                        break;
                    case JsonTokenType.PropertyName when stack.Count > 0:
                        if (!stack.Peek().Add(reader.GetString()!)) return true;
                        break;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            // 结构异常由后续 JsonDocument.Parse 统一报告。
            return false;
        }
    }
}
