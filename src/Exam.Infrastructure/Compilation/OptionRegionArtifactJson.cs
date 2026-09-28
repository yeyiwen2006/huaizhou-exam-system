using System.Text.Json;
using Exam.Contracts.Importing;

namespace Exam.Infrastructure.Compilation;

/// <summary>
/// <c>option-regions.json</c> 的规范读写。字段名、出现顺序与坐标精度在此固定，B03 按此解析。
/// 契约见 docs/d03-option-region-contract.md 第 4.4 节。
///
/// 写出一律按 <see cref="CoordinateDecimals"/> 位小数**向零**舍入：向零舍入不会让任何值变小或变大到
/// 越出 <c>[0,1]</c>，也不会让 <c>x + width</c> 超过 1，因此舍入后的结果仍满足契约的取值约束。
/// </summary>
public static class OptionRegionArtifactJson
{
    /// <summary>归一化坐标写出的小数位。1e-6 页宽在显示上远小于一个像素，足够表达点击区域。</summary>
    public const int CoordinateDecimals = 6;

    /// <summary>
    /// 缩进固定为两个空格、换行固定为 <c>\n</c>、属性顺序即写出顺序：产物必须与平台无关，
    /// 否则同一份试卷在不同操作系统上会产出字节不同的文件，无法按版本比对。
    /// </summary>
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, NewLine = "\n" };

    public static string Serialize(OptionRegionArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

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
                artifactVersion != OptionRegionArtifact.CurrentArtifactVersion)
                return Fail(OptionRegionIssueCode.RequestInvalid,
                    $"artifactVersion 必须为 {OptionRegionArtifact.CurrentArtifactVersion}");

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
                var parsed = ParseQuestion(question, questionIds);
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
        HashSet<string> questionIds)
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
            if (UnexpectedProperty(option, ["optionId", "pageIndex", "x", "y", "width", "height"]) is { } optionUnknown)
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

            if (!(width > 0) || !(height > 0) || x < 0 || y < 0 || x + width > 1 || y + height > 1)
                return (null, new OptionRegionIssue(
                    OptionRegionIssueCode.RegionOutOfBounds, "坐标越出 [0,1] 或宽高非正",
                    questionId, optionId, pageIndex));

            parsedOptions.Add(new OptionRegion(optionId, pageIndex, x, y, width, height));
        }

        return (new OptionRegionQuestion(questionId, parsedOptions), null);
    }

    private static OptionRegionIssue Invalid(string message, string? questionId = null, string? optionId = null) =>
        new(OptionRegionIssueCode.RequestInvalid, message, questionId, optionId);

    private static OptionRegionExtractionResult Fail(OptionRegionIssueCode code, string message) =>
        OptionRegionExtractionResult.Failure([new OptionRegionIssue(code, message)]);

    /// <summary>向零舍入；极小值（小于精度）保留原值，避免被舍成 0 而破坏「宽高为正」。</summary>
    private static double Round(double value)
    {
        var rounded = Math.Round(value, CoordinateDecimals, MidpointRounding.ToZero);
        return rounded > 0 ? rounded : value;
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
            var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
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
