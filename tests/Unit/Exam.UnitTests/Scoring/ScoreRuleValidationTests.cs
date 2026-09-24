using Exam.Contracts.Grading;
using Exam.Contracts.Importing;
using Exam.Domain.Scoring;

namespace Exam.UnitTests.Scoring;

/// <summary>
/// 发布前校验的用例。requirements.md 要求「发布前校验所有合法作答情况，最终逐题分必须为有限小数」，
/// 因此 1÷3 这类规则必须在配置期被挡下，而不是等计分时才暴露。
/// </summary>
public sealed class ScoreRuleValidationTests
{
    [Fact]
    public void ValidatePublishable_SingleAndMultipleBranches_ProduceNoProblems()
    {
        // 交大式：一个正确选项，错扣 0.75；清华式：按正确选项数量计分。
        var single = Rule(
            correctOptionIds: ["A"],
            scoring: new ManifestGroupScoring(
                new ManifestOneCorrect("3", "0", "-0.75"),
                new ManifestMultipleCorrect("4", "2", "0", "-1")));

        var variable = Rule(
            correctOptionIds: ["A", "B", "C"],
            scoring: new ManifestGroupScoring(
                new ManifestOneCorrect("{正确选项总数}", "0", "0"),
                new ManifestMultipleCorrect("{正确选项总数}", "{选择的正确选项数}", "0", "0")));

        Assert.Empty(ScoreCalculator.ValidatePublishable([single]));
        Assert.Empty(ScoreCalculator.ValidatePublishable([variable]));
    }

    [Fact]
    public void ValidatePublishable_NonFiniteConstantScore_IsRejected()
    {
        // 需求明示：1÷3 这样的最终分值必须拒绝发布。
        var rule = Rule(
            correctOptionIds: ["A"],
            scoring: new ManifestGroupScoring(
                new ManifestOneCorrect("1/3", "0", "0"),
                new ManifestMultipleCorrect("1/3", "0", "0", "0")));

        var problem = Assert.Single(
            ScoreCalculator.ValidatePublishable([rule]),
            item => item.Code == ScoreRuleCodes.NotFiniteDecimal);

        Assert.Equal("q-001", problem.QuestionId);
    }

    [Fact]
    public void ValidatePublishable_VariableDivisorThatCanBeZero_IsRejected()
    {
        // 分母含变量时静态判不出除零：不选（k=0）这一情形下必然除零，必须由枚举发现。
        // 这条正是审查里指出的「除零只标待核对」缺陷的正面覆盖。
        var rule = Rule(
            correctOptionIds: ["A"],
            scoring: new ManifestGroupScoring(
                new ManifestOneCorrect("3", "1/{选择的正确选项数}", "0"),
                new ManifestMultipleCorrect("3", "1", "1/{选择的正确选项数}", "0")));

        var problem = Assert.Single(
            ScoreCalculator.ValidatePublishable([rule]),
            item => item.Code == ScoreRuleCodes.DivisionByZero);

        Assert.Contains("分母为 0", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatePublishable_ChecksEverySelectableCount_NotJustOneRepresentative()
    {
        // partialCorrect = 2/{选择的正确选项数}，正确选项 4 个：
        // k=1、2 时是有限小数，只有 k=3 时会得到 2/3。只抽查一个 k 就会漏掉。
        var rule = Rule(
            correctOptionIds: ["A", "B", "C", "D"],
            scoring: new ManifestGroupScoring(
                new ManifestOneCorrect("1", "0", "0"),
                new ManifestMultipleCorrect("2", "2/{选择的正确选项数}", "0", "0")));

        var problem = Assert.Single(
            ScoreCalculator.ValidatePublishable([rule]),
            item => item.Code == ScoreRuleCodes.NotFiniteDecimal);

        Assert.Contains("选对 3 个", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatePublishable_UnreachableWrongBranch_IsNotReported()
    {
        // 所有选项都是正确选项时，「选错」不可达，因此它的表达式不会影响任何真实计分。
        var rule = new QuestionRule(
            "q-001",
            1,
            "subject-1",
            ["A", "B"],
            ["A", "B"],
            new ManifestGroupScoring(
                new ManifestOneCorrect("1", "0", "1/0"),
                new ManifestMultipleCorrect("2", "1", "0", "1/0")));

        Assert.Empty(ScoreCalculator.ValidatePublishable([rule]));
    }

    [Fact]
    public void ValidatePublishable_CorrectOptionNotDeclared_IsRejected()
    {
        var rule = Rule(
            correctOptionIds: ["A", "E"],
            scoring: new ManifestGroupScoring(
                new ManifestOneCorrect("3", "0", "0"),
                new ManifestMultipleCorrect("3", "1", "0", "0")));

        var problem = Assert.Single(
            ScoreCalculator.ValidatePublishable([rule]),
            item => item.Code == ScoreRuleCodes.CorrectOptionNotDeclared);

        Assert.Contains("E", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatePublishable_MissingAnswerKey_IsRejected()
    {
        var rule = Rule(
            correctOptionIds: [],
            scoring: new ManifestGroupScoring(
                new ManifestOneCorrect("3", "0", "0"),
                new ManifestMultipleCorrect("3", "1", "0", "0")));

        Assert.Single(
            ScoreCalculator.ValidatePublishable([rule]),
            item => item.Code == ScoreRuleCodes.CorrectOptionMissing);
    }

    [Fact]
    public void ValidatePublishable_MultipleCorrectOptionsWithoutMultipleBranch_IsRejected()
    {
        // 该题有两个正确选项，必须走 multipleCorrect；只配 oneCorrect 时无法计分。
        var rule = Rule(
            correctOptionIds: ["A", "C"],
            scoring: new ManifestGroupScoring(
                new ManifestOneCorrect("3", "0", "0"),
                null));

        Assert.Single(
            ScoreCalculator.ValidatePublishable([rule]),
            item => item.Code == ScoreRuleCodes.BranchMissing);
    }

    [Fact]
    public void ValidatePublishable_UnknownVariable_IsRejected()
    {
        var rule = Rule(
            correctOptionIds: ["A"],
            scoring: new ManifestGroupScoring(
                new ManifestOneCorrect("{总题数}", "0", "0"),
                new ManifestMultipleCorrect("1", "0", "0", "0")));

        Assert.Single(
            ScoreCalculator.ValidatePublishable([rule]),
            item => item.Code == ScoreRuleCodes.ExpressionUnknownVariable);
    }

    [Fact]
    public void ValidatePublishable_NegativeFullScore_IsRejected()
    {
        var rule = Rule(
            correctOptionIds: ["A"],
            scoring: new ManifestGroupScoring(
                new ManifestOneCorrect("-1", "0", "0"),
                new ManifestMultipleCorrect("-1", "0", "0", "0")));

        Assert.Single(
            ScoreCalculator.ValidatePublishable([rule]),
            item => item.Code == ScoreRuleCodes.NegativeFullScore);
    }

    private static QuestionRule Rule(
        IReadOnlyList<string> correctOptionIds,
        ManifestGroupScoring scoring) =>
        new("q-001", 1, "subject-1", ["A", "B", "C", "D"], correctOptionIds, scoring);

    [Fact]
    public void ValidatePublishable_AllCorrectOptionsPlusWrongOptionIsReachable()
    {
        var rule = Rule(["A", "B"], new ManifestGroupScoring(null,
            new ManifestMultipleCorrect("2", "1", "0", "1/({正确选项总数}-{选择的正确选项数})")));

        Assert.Contains(ScoreCalculator.ValidatePublishable([rule]), problem => problem.Code == ScoreRuleCodes.DivisionByZero);
    }

    [Fact]
    public void ValidatePublishable_DuplicateQuestionCannotBeCountedTwice()
    {
        var rule = Rule(["A"], new ManifestGroupScoring(new ManifestOneCorrect("1", "0", "0"), null));
        Assert.NotEmpty(ScoreCalculator.ValidatePublishable([rule, rule]));
    }

    [Fact]
    public void ValidatePublishable_DuplicateCorrectOptionsAreInvalid()
    {
        var rule = Rule(["A", "A"], new ManifestGroupScoring(null, new ManifestMultipleCorrect("2", "1", "0", "0")));
        Assert.NotEmpty(ScoreCalculator.ValidatePublishable([rule]));
    }

    [Fact]
    public void ValidatePublishable_SingleChoiceDoesNotEnumerateImpossibleExtraSelection()
    {
        var rule = Rule(["A"], new ManifestGroupScoring(
            new ManifestOneCorrect("1", "0", "1/(1-{选择的正确选项数})"), null))
            with { Type = QuestionTypeKind.Single };
        Assert.Empty(ScoreCalculator.ValidatePublishable([rule]));
        Assert.Contains(ScoreCalculator.ValidatePublishable([rule with { Type = QuestionTypeKind.Indeterminate }]),
            problem => problem.Code == ScoreRuleCodes.DivisionByZero);
    }

    [Theory]
    [InlineData(QuestionTypeKind.Single, 2)]
    [InlineData(QuestionTypeKind.Multiple, 1)]
    public void ValidatePublishable_QuestionTypeMustMatchAnswerKey(QuestionTypeKind type, int count)
    {
        var rule = Rule(new[] { "A", "B" }.Take(count).ToArray(), new ManifestGroupScoring(
            new ManifestOneCorrect("1", "0", "0"), new ManifestMultipleCorrect("2", "1", "0", "0")))
            with { Type = type };
        Assert.Contains(ScoreCalculator.ValidatePublishable([rule]), problem => problem.Code == ScoreRuleCodes.RuleInvalid);
    }

    [Fact]
    public void ValidatePublishable_EmptyRuleSetIsInvalid()
    {
        Assert.Contains(ScoreCalculator.ValidatePublishable([]), problem => problem.Code == ScoreRuleCodes.RuleInvalid);
    }
}
