using System.Text;
using Exam.Contracts.Exams;
using Exam.Domain.Importing;
using Exam.Domain.Scoring;

namespace Exam.Domain.Exams;

/// <summary>
/// 考试设置的校验与考试说明生成。服务器保存前以此为准，考务端用同一份规则做即时提示，
/// 说明由结构化规则生成（requirements.md“考试说明由结构化规则生成”）。
/// </summary>
public static class ExamSettingsRules
{
    public const int MaxNameLength = 128;
    private static readonly string[] Types = ["single", "multiple", "indeterminate"];

    /// <summary>考试名称比较规则：去除首尾空白后不区分大小写，避免仅大小写不同的同名考试。</summary>
    public static string NameKey(string name) => name.Trim().ToUpperInvariant();

    public static IReadOnlyList<string> Validate(string? name, ExamSettings? settings)
    {
        var errors = new List<string>();
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > MaxNameLength || !WindowsFileNameRules.IsValidFileName(trimmed))
            errors.Add($"考试名称不能为空、不超过 {MaxNameLength} 个字符，且不能包含 \\ / : * ? \" < > | 等字符。");
        if (settings is null || settings.Subjects is not { Count: > 0 })
        {
            errors.Add("至少设置一个科目。");
            return errors;
        }
        if (settings.Subjects.Count > 100) errors.Add("科目数量不能超过 100 个。");

        var subjectIds = new HashSet<string>(StringComparer.Ordinal);
        var groupIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subject in settings.Subjects)
        {
            if (subject is null) { errors.Add("科目设置无效。"); continue; }
            var label = $"科目“{subject.Name}”";
            if (string.IsNullOrWhiteSpace(subject.SubjectId) || subject.SubjectId.Trim().Length > 128 ||
                !subjectIds.Add(subject.SubjectId.Trim()))
                errors.Add($"{label}的编号为空或与其他科目重复。");
            if (string.IsNullOrWhiteSpace(subject.Name) || subject.Name.Trim().Length > 64)
                errors.Add("有科目没有填写名称，或名称超过 64 个字符。");
            if (!ScoreConstantRules.IsValid(subject.MaxScore?.Trim()))
                errors.Add($"{label}的满分必须是数字，例如 100 或 7.5。");
            if (subject.Groups is not { Count: > 0 })
            {
                errors.Add($"{label}至少需要一个题组。");
                continue;
            }
            var orders = new HashSet<int>();
            foreach (var group in subject.Groups)
                ValidateGroup(subject, group, groupIds, orders, errors);
        }
        return errors;
    }

    private static void ValidateGroup(ExamSubjectSettings subject, ExamGroupSettings? group,
        ISet<string> groupIds, ISet<int> orders, ICollection<string> errors)
    {
        if (group is null) { errors.Add($"科目“{subject.Name}”的题组设置无效。"); return; }
        var label = $"科目“{subject.Name}”题组“{group.Name}”";
        if (string.IsNullOrWhiteSpace(group.GroupId) || group.GroupId.Trim().Length > 128 || !groupIds.Add(group.GroupId.Trim()))
            errors.Add($"{label}的编号为空或与其他题组重复。");
        if (string.IsNullOrWhiteSpace(group.Name) || group.Name.Trim().Length > 64)
            errors.Add($"{label}缺少名称，或名称超过 64 个字符。");
        if (!Types.Contains(group.Type, StringComparer.Ordinal))
            errors.Add($"{label}的题型只能是单项选择题、多项选择题或不定项选择题。");
        if (group.Order is < 1 or > 9999 || !orders.Add(group.Order))
            errors.Add($"{label}的顺序必须是 1–9999 的整数，且同一科目内不能重复。");
        if (group.QuestionCount is < 1 or > 9999)
            errors.Add($"{label}的题量必须是 1–9999 的整数。");
        if (!ScoreConstantRules.IsValid(group.MaxScore?.Trim()))
            errors.Add($"{label}的满分必须是数字，例如 30 或 7.5。");

        var needsOne = group.Type is "single" or "indeterminate";
        var needsMultiple = group.Type is "multiple" or "indeterminate";
        if (needsOne != group.OneCorrect is not null || needsMultiple != group.MultipleCorrect is not null)
        {
            errors.Add($"{label}的计分规则与题型不符：单项选择题只填一个正确答案的规则，多项选择题只填多个正确答案的规则，不定项选择题两者都填。");
            return;
        }
        var expressions = new List<string?>();
        if (group.OneCorrect is { } one) expressions.AddRange([one.Correct, one.Blank, one.Wrong]);
        if (group.MultipleCorrect is { } many) expressions.AddRange([many.AllCorrect, many.PartialCorrect, many.Blank, many.Wrong]);
        if (expressions.Any(expression => !ScoreExpressionParser.Check(expression).IsValid))
            errors.Add($"{label}的计分规则只能使用数字、加减乘除、括号和 {{正确选项总数}}、{{选择的正确选项数}} 两个变量，且不能除以零。");
    }

    /// <summary>按科目、题组顺序生成考试说明，每科一行标题，每个题组一行（与需求示例格式一致）。</summary>
    public static IReadOnlyList<string> BuildInstructions(ExamSettings settings)
    {
        var lines = new List<string>();
        foreach (var subject in settings.Subjects)
        {
            lines.Add($"{subject.Name.Trim()}（满分{subject.MaxScore.Trim()}分）");
            foreach (var group in subject.Groups.OrderBy(group => group.Order))
            {
                var one = group.OneCorrect;
                var many = group.MultipleCorrect;
                var perQuestion = group.Type switch
                {
                    "single" => one?.Correct,
                    "multiple" => many?.AllCorrect,
                    "indeterminate" when one?.Correct == many?.AllCorrect => one?.Correct,
                    "indeterminate" => $"{one?.Correct}分或{many?.AllCorrect}",
                    _ => null,
                };
                var scoring = group.Type switch
                {
                    "single" => $"选对得{one?.Correct}分，不选得{one?.Blank}分，选错得{one?.Wrong}分。",
                    "multiple" => $"全部选对得{many?.AllCorrect}分，选对但不全得{many?.PartialCorrect}分，不选得{many?.Blank}分，选错得{many?.Wrong}分。",
                    "indeterminate" => $"若有一个正确答案，选对得{one?.Correct}分，不选得{one?.Blank}分，选错得{one?.Wrong}分；若有多个正确答案，全部选对得{many?.AllCorrect}分，选对但不全得{many?.PartialCorrect}分，不选得{many?.Blank}分，选错得{many?.Wrong}分。",
                    _ => string.Empty,
                };
                var abstain = group.AllowAbstain ? "本组允许选择“不作答”。" : string.Empty;
                var builder = new StringBuilder();
                builder.Append($"{group.Name.Trim()}：本题共{group.QuestionCount}小题，");
                builder.Append($"每小题{perQuestion}分，共计{group.MaxScore.Trim()}分。{scoring}{abstain}");
                lines.Add(builder.ToString());
            }
        }
        return lines;
    }
}
