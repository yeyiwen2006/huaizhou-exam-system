using System.Text.Json;

using Exam.Contracts.Importing;

namespace Exam.Domain.Importing;

/// <summary>
/// 清单与独立答案的 JSON 形状扫描：只负责未定义字段检查，与 schema 的 additionalProperties:false 对齐。
/// 先于反序列化执行，使未定义字段能定位到具体 JSON Pointer，而不是在反序列化时丢失位置。
/// </summary>
internal static class ManifestShapeScanner
{
    private static readonly HashSet<string> RootKeys = new(StringComparer.Ordinal)
    {
        "schemaVersion", "examId", "examName", "subjects", "groups", "questions", "resourceRoots"
    };

    private static readonly HashSet<string> SubjectKeys = new(StringComparer.Ordinal)
    {
        "subjectId", "name", "maxScore"
    };

    private static readonly HashSet<string> GroupKeys = new(StringComparer.Ordinal)
    {
        "groupId", "subjectId", "name", "type", "order", "questionCount", "maxScore", "allowAbstain", "scoring"
    };

    private static readonly HashSet<string> ScoringKeys = new(StringComparer.Ordinal)
    {
        "oneCorrect", "multipleCorrect"
    };

    private static readonly HashSet<string> OneCorrectKeys = new(StringComparer.Ordinal)
    {
        "correct", "blank", "wrong"
    };

    private static readonly HashSet<string> MultipleCorrectKeys = new(StringComparer.Ordinal)
    {
        "allCorrect", "partialCorrect", "blank", "wrong"
    };

    private static readonly HashSet<string> QuestionKeys = new(StringComparer.Ordinal)
    {
        "questionId", "questionNo", "subjectId", "groupId", "source", "options", "displayMetadata"
    };

    private static readonly HashSet<string> DisplayMetadataKeys = new(StringComparer.Ordinal)
    {
        "anchorScheme", "optionCaptions", "pageCount"
    };

    private static readonly HashSet<string> AnswerKeyRootKeys = new(StringComparer.Ordinal)
    {
        "schemaVersion", "examId", "answers"
    };

    private static readonly HashSet<string> AnswerKeys = new(StringComparer.Ordinal)
    {
        "questionId", "questionNo", "correctOptionIds"
    };

    public static bool TryScan(string json, bool isManifest, List<ImportIssue> issues, out JsonDocument document)
    {
        document = null!;
        try
        {
            var parsed = JsonDocument.Parse(json);
            document = parsed;
        }
        catch (JsonException exception)
        {
            issues.Add(ImportIssue.Error(
                ManifestRuleIds.InvalidJson,
                "/",
                $"{(isManifest ? "清单" : "独立答案")}不是合法 JSON：{exception.Message}"));
            return false;
        }

        if (isManifest)
        {
            ScanManifestShape(document.RootElement, issues);
        }
        else
        {
            ScanAnswerKeyShape(document.RootElement, issues);
        }

        return true;
    }

    private static void ScanManifestShape(JsonElement root, List<ImportIssue> issues)
    {
        CheckAllowedProperties(root, string.Empty, RootKeys, issues);

        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (root.TryGetProperty("subjects", out var subjects) && subjects.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var subject in subjects.EnumerateArray())
            {
                CheckAllowedProperties(subject, $"/subjects/{index++}", SubjectKeys, issues);
            }
        }

        if (!root.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var groupIndex = 0;
        foreach (var group in groups.EnumerateArray())
        {
            var pointer = $"/groups/{groupIndex++}";
            CheckAllowedProperties(group, pointer, GroupKeys, issues);

            if (group.ValueKind != JsonValueKind.Object
                || !group.TryGetProperty("scoring", out var scoring)
                || scoring.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            CheckAllowedProperties(scoring, $"{pointer}/scoring", ScoringKeys, issues);

            if (scoring.TryGetProperty("oneCorrect", out var oneCorrect) && oneCorrect.ValueKind == JsonValueKind.Object)
            {
                CheckAllowedProperties(oneCorrect, $"{pointer}/scoring/oneCorrect", OneCorrectKeys, issues);
            }

            if (scoring.TryGetProperty("multipleCorrect", out var multipleCorrect)
                && multipleCorrect.ValueKind == JsonValueKind.Object)
            {
                CheckAllowedProperties(
                    multipleCorrect, $"{pointer}/scoring/multipleCorrect", MultipleCorrectKeys, issues);
            }
        }

        if (!root.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var questionIndex = 0;
        foreach (var question in questions.EnumerateArray())
        {
            var pointer = $"/questions/{questionIndex++}";
            CheckAllowedProperties(question, pointer, QuestionKeys, issues);

            if (question.ValueKind == JsonValueKind.Object
                && question.TryGetProperty("displayMetadata", out var displayMetadata)
                && displayMetadata.ValueKind == JsonValueKind.Object)
            {
                CheckAllowedProperties(displayMetadata, $"{pointer}/displayMetadata", DisplayMetadataKeys, issues);
            }
        }
    }

    private static void ScanAnswerKeyShape(JsonElement root, List<ImportIssue> issues)
    {
        CheckAllowedProperties(root, string.Empty, AnswerKeyRootKeys, issues);

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("answers", out var answers)
            || answers.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var index = 0;
        foreach (var answer in answers.EnumerateArray())
        {
            CheckAllowedProperties(answer, $"/answers/{index++}", AnswerKeys, issues);
        }
    }

    private static void CheckAllowedProperties(
        JsonElement element,
        string pointer,
        IReadOnlySet<string> allowed,
        List<ImportIssue> issues)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                issues.Add(ImportIssue.Error(
                    ManifestRuleIds.UnknownField,
                    $"{pointer}/{EscapePointerToken(property.Name)}",
                    $"出现未定义字段“{property.Name}”。"));
            }
        }
    }

    private static string EscapePointerToken(string token) =>
        token.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
}
