using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;

namespace Exam.Student.Core;

/// <summary>
/// B03 选项点击语义（需求·答题）：
/// 单选——选新项替换旧项，再点已选项取消；
/// 多项及不定项——逐项选中、取消；
/// "不作答"——位于原选项之后，与普通选项互斥，选择、取消与普通选项一致。
/// 计算纯函数，不落盘；持久化由 AnswerSession 负责。
/// </summary>
public static class OptionSelection
{
    /// <summary>点击普通选项，返回该题的下一答案状态。</summary>
    public static AnswerValue Toggle(PaperQuestionStructure question, AnswerValue current, string optionId)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentException.ThrowIfNullOrWhiteSpace(optionId);
        if (!question.OptionIds.Contains(optionId, StringComparer.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(optionId), $"未知选项 {optionId}。");

        if (question.QuestionType == "single")
        {
            // 单选：再点已选项取消；选新项替换旧项（含从"不作答"切回普通选项）。
            return current.Kind == AnswerKind.Selected &&
                current.OptionIds.Count == 1 &&
                string.Equals(current.OptionIds[0], optionId, StringComparison.Ordinal)
                ? AnswerValue.Empty()
                : AnswerValue.Selected(optionId);
        }
        if (question.QuestionType is not ("multiple" or "indeterminate"))
            throw new InvalidOperationException($"未知题型 {question.QuestionType}。");

        // 多项/不定项：逐项选中、取消；快照按清单选项顺序稳定输出。
        var selected = current.Kind == AnswerKind.Selected
            ? question.OptionIds.Where(id => current.OptionIds.Contains(id, StringComparer.Ordinal)).ToList()
            : new List<string>();
        if (!selected.Remove(optionId)) { selected.Add(optionId); }
        return selected.Count == 0
            ? AnswerValue.Empty()
            : AnswerValue.Selected(question.OptionIds
                .Where(id => selected.Contains(id, StringComparer.Ordinal)).ToArray());
    }

    /// <summary>点击"不作答"。未启用时抛出异常，由调用方保证按钮可见性。</summary>
    public static AnswerValue ToggleAbstain(PaperQuestionStructure question, AnswerValue current)
    {
        ArgumentNullException.ThrowIfNull(question);
        if (!question.AllowAbstain) { throw new InvalidOperationException("此题不允许不作答标记。"); }
        return current.Kind == AnswerKind.Abstain ? AnswerValue.Empty() : AnswerValue.Abstain();
    }

    /// <summary>该题是否处于已作答展示状态（用于题号变绿判定，与导航一致）。</summary>
    public static bool IsAnswered(AnswerValue answer) => answer.Kind != AnswerKind.Empty;
}
