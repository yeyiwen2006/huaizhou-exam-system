using Exam.Contracts.Versioning;
using Exam.Contracts.Common;

namespace Exam.Contracts.Submissions;

/// <summary>学生端、收卷和计分入口共同使用的完整答卷约定。缺项不等于未作答。</summary>
public static class AnswerSheetContract
{
    public static string? Validate(PaperStructure paper, IReadOnlyDictionary<string, AnswerValue>? answers)
    {
        if (paper.Questions.Count == 0 || answers is null || answers.Count != paper.Questions.Count ||
            paper.Questions.Select(q => q.QuestionId).Distinct(StringComparer.Ordinal).Count() != paper.Questions.Count)
            return "答卷必须包含全部题目的记录，未作答须明确保存为 Empty。";
        var answerIds = answers.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var question in paper.Questions)
        {
            if (!answerIds.Contains(question.QuestionId) || !answers.TryGetValue(question.QuestionId, out var answer))
                return "答卷缺少题目记录，不能将丢失记录视为未作答。";
            var error = ValidateAnswer(question, answer);
            if (error is not null) return error;
        }
        return null;
    }

    public static string? ValidateAnswer(PaperQuestionStructure question, AnswerValue? answer)
    {
        if (answer is null || !Enum.IsDefined(answer.Kind) || answer.OptionIds is null)
            return "答案类型或选项集合无效。";
        if (answer.Kind == AnswerKind.Abstain && !question.AllowAbstain)
            return "此题不允许不作答标记。";
        if (answer.Kind != AnswerKind.Selected)
            return answer.OptionIds.Count == 0 ? null : "未选择的答案不能携带选项。";
        if (answer.OptionIds.Count == 0 ||
            (question.QuestionType == "single" && answer.OptionIds.Count != 1) ||
            answer.OptionIds.Distinct(StringComparer.Ordinal).Count() != answer.OptionIds.Count ||
            answer.OptionIds.Any(id => !question.OptionIds.Contains(id, StringComparer.Ordinal)))
            return "答案包含重复、未知选项或不符合题型。";
        return null;
    }
}
