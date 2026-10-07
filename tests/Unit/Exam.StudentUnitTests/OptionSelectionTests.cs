using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

/// <summary>
/// 学生导航 选项点击语义：单选替换并可再点取消；多选/不定项逐项切换；
/// “不作答”与普通选项互斥。所有产物必须通过答卷契约校验。
/// </summary>
public sealed class OptionSelectionTests
{
    private static PaperQuestionStructure Single(bool allowAbstain = false) =>
        new("q1", 1, "s", "g", "single", ["A", "B", "C"], allowAbstain);

    private static PaperQuestionStructure Multiple(bool allowAbstain = false) =>
        new("q2", 2, "s", "g", "multiple", ["A", "B", "C"], allowAbstain);

    private static PaperQuestionStructure Indeterminate(bool allowAbstain = false) =>
        new("q3", 3, "s", "g", "indeterminate", ["A", "B", "C"], allowAbstain);

    [Fact]
    public void SingleChoiceSelectsNewOptionAndReplacesTheOldOne()
    {
        var question = Single();
        var state = OptionSelection.Toggle(question, AnswerValue.Empty(), "A");
        Assert.Equal(AnswerKind.Selected, state.Kind);
        Assert.Equal(new[] { "A" }, state.OptionIds);

        state = OptionSelection.Toggle(question, state, "C");
        Assert.Equal(AnswerKind.Selected, state.Kind);
        Assert.Equal(new[] { "C" }, state.OptionIds);
    }

    [Fact]
    public void SingleChoiceClickingTheSelectedOptionCancelsIt()
    {
        var question = Single();
        var state = OptionSelection.Toggle(question, AnswerValue.Selected("B"), "B");
        Assert.Equal(AnswerKind.Empty, state.Kind);
    }

    [Theory]
    [InlineData("multiple")]
    [InlineData("indeterminate")]
    public void MultiChoiceTogglesEachOptionIndependently(string questionType)
    {
        var question = questionType == "multiple" ? Multiple() : Indeterminate();

        var state = OptionSelection.Toggle(question, AnswerValue.Empty(), "C");
        state = OptionSelection.Toggle(question, state, "A");
        Assert.Equal(AnswerKind.Selected, state.Kind);
        Assert.Equal(new[] { "A", "C" }, state.OptionIds);

        // 快照按清单顺序稳定输出，与点击顺序无关。
        state = OptionSelection.Toggle(question, state, "B");
        Assert.Equal(new[] { "A", "B", "C" }, state.OptionIds);

        state = OptionSelection.Toggle(question, state, "A");
        Assert.Equal(new[] { "B", "C" }, state.OptionIds);

        // 取消全部选择恢复未作答。
        state = OptionSelection.Toggle(question, state, "B");
        state = OptionSelection.Toggle(question, state, "C");
        Assert.Equal(AnswerKind.Empty, state.Kind);
    }

    [Fact]
    public void AbstainAndNormalOptionsAreMutuallyExclusive()
    {
        var question = Indeterminate(allowAbstain: true);

        // 普通选项 → 不作答：替换。
        var state = OptionSelection.ToggleAbstain(question, AnswerValue.Selected("A", "B"));
        Assert.Equal(AnswerKind.Abstain, state.Kind);

        // 不作答 → 再点不作答：取消，恢复未作答。
        state = OptionSelection.ToggleAbstain(question, state);
        Assert.Equal(AnswerKind.Empty, state.Kind);

        // 不作答 → 普通选项：替换。
        state = OptionSelection.ToggleAbstain(question, AnswerValue.Empty());
        state = OptionSelection.Toggle(question, state, "C");
        Assert.Equal(AnswerKind.Selected, state.Kind);
        Assert.Equal(new[] { "C" }, state.OptionIds);

        // 单选从“不作答”直接切换到普通选项。
        var single = Single(allowAbstain: true);
        state = OptionSelection.Toggle(single, AnswerValue.Abstain(), "A");
        Assert.Equal(AnswerKind.Selected, state.Kind);
        Assert.Equal(new[] { "A" }, state.OptionIds);
    }

    [Fact]
    public void UnknownOptionOrDisallowedAbstainIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OptionSelection.Toggle(Single(), AnswerValue.Empty(), "Z"));
        Assert.Throws<InvalidOperationException>(() =>
            OptionSelection.ToggleAbstain(Single(allowAbstain: false), AnswerValue.Empty()));
    }

    [Theory]
    [InlineData("single")]
    [InlineData("multiple")]
    [InlineData("indeterminate")]
    public void EveryReachableStatePassesTheAnswerSheetContract(string questionType)
    {
        // 逐一点击可达的所有答案状态，全部必须满足答卷契约（题型约束 + 选项合法性）。
        PaperQuestionStructure question = questionType switch
        {
            "single" => Single(),
            "multiple" => Multiple(),
            _ => Indeterminate(),
        };
        var reachable = new List<AnswerValue> { AnswerValue.Empty() };
        foreach (var optionId in question.OptionIds)
        {
            reachable.Add(OptionSelection.Toggle(question, AnswerValue.Empty(), optionId));
        }
        var combined = AnswerValue.Selected("A", "B");
        reachable.Add(OptionSelection.Toggle(question, AnswerValue.Selected("A"), "B"));
        reachable.Add(OptionSelection.Toggle(question, combined, "A"));

        foreach (var answer in reachable)
        {
            Assert.Null(AnswerSheetContract.ValidateAnswer(question, answer));
        }

        // 多选全套仅对非单选合法；契约层对单选多选必须报错（反向验证测试本身有效）。
        if (questionType == "single")
        {
            Assert.NotNull(AnswerSheetContract.ValidateAnswer(question, combined));
        }
    }
}
