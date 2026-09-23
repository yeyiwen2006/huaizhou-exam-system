using System.Text;
using System.Text.Json;

using Exam.Contracts.Importing;

namespace Exam.Domain.Importing;

/// <summary>
/// 反序列化前检查结构、必填字段、null、类型、边界与重复字段；不依赖外部 schema 文件或网络。
/// 此处只描述结构，引用、路径、题型及计分语义仍由 ExamManifestValidator 判定。
/// 与公开 schema 的约束对应关系由基于 schema 自动生成的变异测试守护。
/// </summary>
internal static class ManifestShapeScanner
{
    private static readonly JsonSerializerOptions JsonOptions = ImportJson.Create();
    private static readonly Shape Identifier = Text(128);
    private static readonly Shape Expression = Text(200, ManifestRuleIds.ScoreExpressionInvalid);
    private static readonly Shape OneCorrect = Object(
        ("correct", Expression, true), ("blank", Expression, true), ("wrong", Expression, true));
    private static readonly Shape MultipleCorrect = Object(
        ("allCorrect", Expression, true), ("partialCorrect", Expression, true),
        ("blank", Expression, true), ("wrong", Expression, true));
    private static readonly Shape Scoring = Object(
        ("oneCorrect", OneCorrect, false), ("multipleCorrect", MultipleCorrect, false)) with
    {
        MinProperties = 1,
        ConstraintRule = ManifestRuleIds.GroupScoringBranchMissing
    };
    private static readonly Shape Display = Object(
        ("anchorScheme", Text(int.MaxValue, ManifestRuleIds.QuestionDisplayAnchorScheme), false),
        ("optionCaptions", Array(Text(32, ManifestRuleIds.QuestionDisplayCaptions), 2,
            rule: ManifestRuleIds.QuestionDisplayCaptions, duplicateRule: ManifestRuleIds.QuestionDisplayCaptions), false),
        ("pageCount", Integer(1, 64), false)) with { MinProperties = 1 };
    private static readonly Shape Subject = Object(
        ("subjectId", Identifier, true), ("name", Text(64), true),
        ("maxScore", Text(32, ManifestRuleIds.SubjectMaxScoreConstantRequired), true));
    private static readonly Shape Group = Object(
        ("groupId", Identifier, true), ("subjectId", Identifier, true), ("name", Text(64), true),
        ("type", Text(int.MaxValue, ManifestRuleIds.GroupTypeInvalid), true),
        ("order", Integer(1, 9999), true), ("questionCount", Integer(1, 9999), true),
        ("allowAbstain", new Shape(JsonValueKind.True), false),
        ("maxScore", Text(32, ManifestRuleIds.GroupMaxScoreConstantRequired), false),
        ("scoring", Scoring, true));
    private static readonly Shape Question = Object(
        ("questionId", Identifier, true), ("questionNo", Integer(1, 99999), true),
        ("subjectId", Identifier, true), ("groupId", Identifier, true),
        ("source", Text(512, ManifestRuleIds.QuestionSourcePathEscapes), true),
        ("options", Array(Text(32, ManifestRuleIds.QuestionOptionsInvalid), 2, 26,
            ManifestRuleIds.QuestionOptionsTooFew, ManifestRuleIds.QuestionOptionsDuplicate) with
        {
            MaximumRule = ManifestRuleIds.QuestionOptionsInvalid
        }, true),
        ("displayMetadata", Display, false));
    private static readonly Shape Manifest = Object(
        ("schemaVersion", Integer(int.MinValue, int.MaxValue), true),
        ("examId", Identifier, true), ("examName", Text(128), true),
        ("subjects", Array(Subject, 1), true), ("groups", Array(Group, 1), true),
        ("questions", Array(Question, 1), true),
        // 资源目录还需按 Windows 规范化后的路径判重，统一在语义校验中定位到重复条目。
        ("resourceRoots", Array(Text(512, ManifestRuleIds.ResourceRootInvalid), 0), true));
    private static readonly Shape Answer = Object(
        ("questionId", Identifier, true), ("questionNo", Integer(1, 99999), true),
        ("correctOptionIds", Array(Text(32), 1, 26, ManifestRuleIds.AnswerKeyCorrectCount,
            ManifestRuleIds.AnswerKeyOptionsDuplicate), true));
    private static readonly Shape AnswerKey = Object(
        ("schemaVersion", Integer(int.MinValue, int.MaxValue), true),
        ("examId", Identifier, true), ("answers", Array(Answer, 1), true));

    public static bool TryScan(string json, bool isManifest, List<ImportIssue> issues, out JsonDocument document)
    {
        document = null!;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            issues.Add(ImportIssue.Error(ManifestRuleIds.InvalidJson, string.Empty,
                $"{(isManifest ? "清单" : "独立答案")}不是合法 JSON：{exception.Message}"));
            return false;
        }

        Scan(document.RootElement, isManifest, issues);
        return true;
    }

    public static void Scan(JsonElement root, bool isManifest, List<ImportIssue> issues)
    {
        try
        {
            Check(root, isManifest ? Manifest : AnswerKey, string.Empty, issues);
        }
        catch (InvalidOperationException exception)
        {
            // JsonDocument.Parse 可接受不成对的 Unicode 转义；读取属性名或字符串时才会抛出此异常。
            // 此时甚至可能无法解码字段名，使用文档根指针，而不是让坏输入逸出校验器。
            issues.Add(ImportIssue.Error(ManifestRuleIds.InvalidJson, string.Empty,
                $"JSON 包含不能解码的 Unicode 文本：{exception.Message}"));
        }
    }

    private static void Check(JsonElement value, Shape shape, string pointer, List<ImportIssue> issues)
    {
        // optional 表示可以省略，绝不表示允许显式 null；数组中的 null 也不能漏到领域层。
        if (value.ValueKind != shape.Kind
            && !(shape.Kind == JsonValueKind.True && value.ValueKind == JsonValueKind.False))
        {
            issues.Add(ImportIssue.Error(ManifestRuleIds.InvalidJson, pointer,
                $"字段类型不符：应为 {shape.Kind}，实际为 {value.ValueKind}；不接受 null 或隐式类型转换。"));
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                CheckObject(value, shape, pointer, issues);
                break;
            case JsonValueKind.Array:
                CheckArray(value, shape, pointer, issues);
                break;
            case JsonValueKind.String:
                var length = value.GetString()!.EnumerateRunes().Count();
                CheckRange(length, shape, pointer, "字符串字符数", issues);
                break;
            case JsonValueKind.Number:
                try
                {
                    var number = value.Deserialize<int>(JsonOptions);
                    CheckRange(number, shape, pointer, "整数值", issues);
                }
                catch (JsonException)
                {
                    issues.Add(ImportIssue.Error(ManifestRuleIds.InvalidJson, pointer,
                        "字段必须是精确的 32 位整数值，不能是非整数或带引号的数字。"));
                }
                break;
        }
    }

    private static void CheckObject(JsonElement value, Shape shape, string pointer, List<ImportIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            var location = $"{pointer}/{EscapePointerToken(property.Name)}";
            if (!seen.Add(property.Name))
            {
                issues.Add(ImportIssue.Error(ManifestRuleIds.DuplicateField, location,
                    $"字段“{property.Name}”重复，不能以最后一个值覆盖前面的值。"));
                continue;
            }
            if (!shape.Properties!.TryGetValue(property.Name, out var field))
            {
                issues.Add(ImportIssue.Error(ManifestRuleIds.UnknownField, location, $"出现未定义字段“{property.Name}”。"));
                continue;
            }
            Check(property.Value, field.Shape, location, issues);
        }

        foreach (var (name, field) in shape.Properties!)
        {
            if (field.Required && !seen.Contains(name))
            {
                issues.Add(ImportIssue.Error(ManifestRuleIds.RequiredMissing,
                    $"{pointer}/{EscapePointerToken(name)}", $"缺少必填字段：{name}。"));
            }
        }
        if (seen.Count < shape.MinProperties)
        {
            issues.Add(ImportIssue.Error(shape.ConstraintRule, pointer, "对象至少需要一个已定义字段。"));
        }
    }

    private static void CheckArray(JsonElement value, Shape shape, string pointer, List<ImportIssue> issues)
    {
        CheckRange(value.GetArrayLength(), shape, pointer, "数组元素数", issues);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicate = false;
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            Check(item, shape.Item!, $"{pointer}/{index++}", issues);
            // 目前声明 uniqueItems 的都是字符串数组。按解码后的值判等，不按 JSON 转义写法判等。
            if (shape.DuplicateRule is not null && item.ValueKind == JsonValueKind.String
                && !seen.Add(item.GetString()!))
            {
                duplicate = true;
            }
        }
        if (duplicate)
        {
            issues.Add(ImportIssue.Error(shape.DuplicateRule!, pointer, "数组元素不能重复。"));
        }
    }

    private static void CheckRange(int value, Shape shape, string pointer, string label, List<ImportIssue> issues)
    {
        if (value < shape.Minimum || value > shape.Maximum)
        {
            var rule = value > shape.Maximum ? shape.MaximumRule ?? shape.ConstraintRule : shape.ConstraintRule;
            issues.Add(ImportIssue.Error(rule, pointer,
                $"{label}必须在 {shape.Minimum} 至 {shape.Maximum} 之间，当前为 {value}。"));
        }
    }

    private static Shape Text(int maximum, string rule = ManifestRuleIds.SchemaConstraint) =>
        new(JsonValueKind.String) { Minimum = 1, Maximum = maximum, ConstraintRule = rule };

    private static Shape Integer(int minimum, int maximum) =>
        new(JsonValueKind.Number) { Minimum = minimum, Maximum = maximum };

    private static Shape Array(Shape item, int minimum, int maximum = int.MaxValue,
        string rule = ManifestRuleIds.SchemaConstraint, string? duplicateRule = null) =>
        new(JsonValueKind.Array)
        {
            Item = item, Minimum = minimum, Maximum = maximum,
            ConstraintRule = rule, DuplicateRule = duplicateRule
        };

    private static Shape Object(params (string Name, Shape Shape, bool Required)[] fields) =>
        new(JsonValueKind.Object)
        {
            Properties = fields.ToDictionary(f => f.Name, f => new Field(f.Shape, f.Required), StringComparer.Ordinal)
        };

    private static string EscapePointerToken(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private sealed record Field(Shape Shape, bool Required);

    private sealed record Shape(JsonValueKind Kind)
    {
        public IReadOnlyDictionary<string, Field>? Properties { get; init; }
        public Shape? Item { get; init; }
        public int Minimum { get; init; }
        public int Maximum { get; init; } = int.MaxValue;
        public int MinProperties { get; init; }
        public string ConstraintRule { get; init; } = ManifestRuleIds.SchemaConstraint;
        public string? MaximumRule { get; init; }
        public string? DuplicateRule { get; init; }
    }
}
