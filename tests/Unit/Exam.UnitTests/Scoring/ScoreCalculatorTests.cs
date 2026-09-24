using Exam.Contracts.Grading;
using Exam.Contracts.Importing;
using Exam.Contracts.Submissions;
using Exam.Domain.Scoring;

namespace Exam.UnitTests.Scoring;

/// <summary>
/// 精确计分的用例。规则形状取自 requirements.md 的两组示例（交大式错扣分、清华式按正确选项数量计分），
/// 试题内容全是合成的。
/// </summary>
public sealed class ScoreCalculatorTests
{
    [Fact]
    public void DetermineOutcome_SingleCorrectBranch_ScoresCorrectBlankAndWrong()
    {
        // 交大式：每小题 3 分，选对得 3、不选得 0、选错得 -0.75。
        var rule = SingleChoiceRule(correct: "3", blank: "0", wrong: "-0.75");

        Assert.Equal(AnswerOutcome.Correct, ScoreCalculator.DetermineOutcome(rule, AnswerValue.Selected("A")));
        Assert.Equal(AnswerOutcome.Blank, ScoreCalculator.DetermineOutcome(rule, AnswerValue.Abstain()));
        Assert.Equal(AnswerOutcome.Wrong, ScoreCalculator.DetermineOutcome(rule, AnswerValue.Selected("D")));

        Assert.Equal("3", Score(rule, AnswerValue.Selected("A")));
        Assert.Equal("0", Score(rule, AnswerValue.Abstain()));
        Assert.Equal("-0.75", Score(rule, AnswerValue.Selected("D")));
    }

    [Fact]
    public void DetermineOutcome_EmptyAndAbstain_ShareTheBlankBranch()
    {
        var rule = SingleChoiceRule(correct: "3", blank: "0", wrong: "-0.75");

        Assert.Equal(AnswerOutcome.Blank, ScoreCalculator.DetermineOutcome(rule, AnswerValue.Empty()));
        Assert.Equal(AnswerOutcome.Blank, ScoreCalculator.DetermineOutcome(rule, AnswerValue.Abstain()));
        Assert.Equal(Score(rule, AnswerValue.Empty()), Score(rule, AnswerValue.Abstain()));
    }

    [Fact]
    public void DetermineOutcome_MultipleCorrectBranch_UsesAllCorrectAndPartialCorrect()
    {
        // 清华化学式：全部选对得 {正确选项总数} 分，选对但不全得 {选择的正确选项数} 分。
        var rule = MultipleChoiceRule(correctOptionIds: ["A", "B", "C"]);

        var full = Grade(rule, AnswerValue.Selected("A", "B", "C")).Grade!.Questions[0];
        var partial = Grade(rule, AnswerValue.Selected("A", "C")).Grade!.Questions[0];

        Assert.Equal(AnswerOutcome.Correct, full.Outcome);
        Assert.Equal("3", full.ExactScore);

        Assert.Equal(AnswerOutcome.PartialCorrect, partial.Outcome);
        Assert.Equal("2", partial.ExactScore);
    }

    [Fact]
    public void DetermineOutcome_AnyWrongOption_IsWrongEvenWhenCorrectOptionsAreAlsoSelected()
    {
        var rule = MultipleChoiceRule(correctOptionIds: ["A", "C"]);

        // 正确选项是 A、C；选 A、B、C 里含错误选项 B，判定为选错而不是部分选对。
        Assert.Equal(AnswerOutcome.Wrong, ScoreCalculator.DetermineOutcome(rule, AnswerValue.Selected("A", "B", "C")));
    }

    [Fact]
    public void Grade_NegativeSubjectScore_IsClampedToZeroWithoutOffsettingOtherSubjects()
    {
        var rules = new[]
        {
            SingleChoiceRule(correct: "3", blank: "0", wrong: "-0.75") with
            {
                QuestionId = "q-001", QuestionNo = 1, SubjectId = "subject-a"
            },
            SingleChoiceRule(correct: "3", blank: "0", wrong: "-0.75") with
            {
                QuestionId = "q-002", QuestionNo = 2, SubjectId = "subject-b"
            }
        };

        var outcome = ScoreCalculator.Grade(
            rules,
            new Dictionary<string, AnswerValue>
            {
                ["q-001"] = AnswerValue.Selected("D"),
                ["q-002"] = AnswerValue.Selected("A")
            },
            Identity());

        Assert.True(outcome.Succeeded, outcome.FailureReason);
        var subjectA = outcome.Grade!.Subjects.Single(item => item.SubjectId == "subject-a");
        Assert.Equal("-0.75", subjectA.ExactRawScore);
        Assert.Equal("0", subjectA.ExactFinalScore);
        Assert.True(subjectA.WasClampedToZero);

        // 归零的负分不抵消其他科目：总分是 3，不是 2.25。
        Assert.Equal("3", outcome.Grade.ExactTotalScore);
    }

    [Fact]
    public void Grade_MultipleNegativeQuestionsInOneSubject_AreSummedBeforeClamping()
    {
        var rules = new[]
        {
            SingleChoiceRule(correct: "3", blank: "0", wrong: "-0.75") with { QuestionId = "q-001", QuestionNo = 1 },
            SingleChoiceRule(correct: "3", blank: "0", wrong: "-0.75") with { QuestionId = "q-002", QuestionNo = 2 },
            SingleChoiceRule(correct: "3", blank: "0", wrong: "-0.75") with { QuestionId = "q-003", QuestionNo = 3 }
        };

        var outcome = ScoreCalculator.Grade(
            rules,
            new Dictionary<string, AnswerValue>
            {
                ["q-001"] = AnswerValue.Selected("A"),
                ["q-002"] = AnswerValue.Selected("C"),
                ["q-003"] = AnswerValue.Selected("D")
            },
            Identity());

        // 3 + (-0.75) + (-0.75) = 1.5：先累计单科原始分再判负，不逐题归零。
        var subject = Assert.Single(outcome.Grade!.Subjects);
        Assert.Equal("1.5", subject.ExactRawScore);
        Assert.False(subject.WasClampedToZero);
    }

    [Fact]
    public void Grade_UsesExactDecimalArithmetic_AndOmitsTrailingPointOnIntegers()
    {
        Assert.Equal("0.3", Score(SingleChoiceRule(correct: "0.1+0.2", blank: "0", wrong: "0"), AnswerValue.Selected("A")));
        Assert.Equal("3", Score(SingleChoiceRule(correct: "3", blank: "0", wrong: "0"), AnswerValue.Selected("A")));
    }

    [Fact]
    public void Grade_MissingAnswerForAQuestion_FailsInsteadOfFabricatingBlank()
    {
        // 漏传记录不能与显式的 Empty 作答混淆。
        var rule = SingleChoiceRule(correct: "3", blank: "0", wrong: "-0.75");

        var outcome = ScoreCalculator.Grade([rule], new Dictionary<string, AnswerValue>(), Identity());

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Grade);
    }

    [Fact]
    public void Grade_InvalidExpression_FailsInsteadOfReturningAFabricatedScore()
    {
        var rule = SingleChoiceRule(correct: "{未知变量}", blank: "0", wrong: "0");

        var outcome = Grade(rule, AnswerValue.Selected("A"));

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Grade);
        Assert.Contains("不合法", outcome.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Grade_DivisionByZeroAtRuntime_FailsInsteadOfScoringZero()
    {
        // 分母含变量时静态判不出除零，只有在具体作答情形下才知道：不选时 k=0。
        var rule = SingleChoiceRule(correct: "3", blank: "1/{选择的正确选项数}", wrong: "0");

        var outcome = Grade(rule, AnswerValue.Abstain());

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.Grade);
        Assert.Contains("分母为 0", outcome.FailureReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Grade_ResultCarriesSubmissionIdentityAndGradingVersion()
    {
        var grade = Grade(SingleChoiceRule(correct: "3", blank: "0", wrong: "0"), AnswerValue.Selected("A")).Grade!;

        Assert.Equal("submission-1", grade.SubmissionId);
        Assert.Equal("exam-1", grade.ExamId);
        Assert.Equal("candidate-1", grade.CandidateId);
        Assert.Equal(7, grade.GradingVersion);
        Assert.Equal("subject-1", Assert.Single(grade.Subjects).SubjectId);
    }

    [Fact]
    public void DetermineOutcome_OneOptionQuestionUnlikeTheKey_IsWrong()
    {
        var rule = SingleChoiceRule(correct: "3", blank: "0", wrong: "0");

        // 只有一个正确选项的题不可能「选对但不全」：选到别的选项就是选错。
        var outcome = ScoreCalculator.DetermineOutcome(rule, AnswerValue.Selected("B"));
        Assert.Equal(AnswerOutcome.Wrong, outcome);
        Assert.NotEqual(AnswerOutcome.PartialCorrect, outcome);
    }

    [Fact]
    public void DetermineOutcome_SingleChoiceWithAnExtraWrongOption_IsWrongNotPartial()
    {
        // 一个正确选项的题：选 A、B 时含错误选项，判定为选错，不是部分选对。
        var rule = SingleChoiceRule(correct: "3", blank: "0", wrong: "-0.75");

        Assert.Equal(AnswerOutcome.Wrong, ScoreCalculator.DetermineOutcome(rule, AnswerValue.Selected("A", "B")));
    }

    private static string Score(QuestionRule rule, AnswerValue answer) =>
        Grade(rule, answer).Grade!.Questions[0].ExactScore;

    [Fact]
    public void Grade_DuplicateSelectedOptionsCannotReceiveFullCredit()
    {
        var result = Grade(MultipleChoiceRule(["A", "B"]), AnswerValue.Selected("A", "A"));
        Assert.False(result.Succeeded);
        Assert.Null(result.Grade);
    }

    [Fact]
    public void Grade_UnknownOptionIsInvalidRatherThanAnOrdinaryWrongAnswer()
    {
        var result = Grade(SingleChoiceRule("3", "0", "-1"), AnswerValue.Selected("unknown"));
        Assert.False(result.Succeeded);
        Assert.Null(result.Grade);
    }

    [Fact]
    public void Grade_AllCorrectPlusWrongUsesActualSelectedCorrectCount()
    {
        var rule = MultipleChoiceRule(["A", "B"]) with
        {
            Scoring = new ManifestGroupScoring(null, new ManifestMultipleCorrect("2", "1", "0", "-{选择的正确选项数}"))
        };
        var result = Grade(rule, AnswerValue.Selected("A", "B", "D"));
        Assert.True(result.Succeeded, result.FailureReason);
        var question = Assert.Single(result.Grade!.Questions);
        Assert.Equal(AnswerOutcome.Wrong, question.Outcome);
        Assert.Equal("-2", question.ExactScore);
        Assert.Equal("0", result.Grade.ExactTotalScore);
    }

    [Fact]
    public void Grade_RejectsSingleChoiceMultipleSelectionAndDisabledAbstain()
    {
        var rule = SingleChoiceRule("3", "0", "-1") with { Type = QuestionTypeKind.Single, AllowAbstain = false };
        Assert.False(Grade(rule, AnswerValue.Selected("A", "B")).Succeeded);
        Assert.False(Grade(rule, AnswerValue.Abstain()).Succeeded);
        Assert.True(Grade(rule, AnswerValue.Empty()).Succeeded);
    }

    [Fact]
    public void Grade_InvalidAnswerShapesFailWithoutScores()
    {
        var rule = SingleChoiceRule("3", "0", "-1");
        var answers = new AnswerValue[]
        {
            new((Exam.Contracts.Common.AnswerKind)99, []),
            AnswerValue.Selected(),
            new(Exam.Contracts.Common.AnswerKind.Empty, ["A"]),
            new(Exam.Contracts.Common.AnswerKind.Selected, null!),
            null!
        };
        foreach (var answer in answers)
        {
            var result = Grade(rule, answer);
            Assert.False(result.Succeeded);
            Assert.Null(result.Grade);
        }
    }

    [Fact]
    public void Grade_DuplicateRulesAndForeignQuestionsCannotBeSilentlyIgnored()
    {
        var rule = SingleChoiceRule("3", "0", "-1");
        var answers = new Dictionary<string, AnswerValue> { [rule.QuestionId] = AnswerValue.Selected("A") };
        Assert.False(ScoreCalculator.Grade([rule, rule], answers, Identity()).Succeeded);
        answers.Add("foreign", AnswerValue.Empty());
        Assert.False(ScoreCalculator.Grade([rule], answers, Identity()).Succeeded);
    }

    private static GradeOutcome Grade(QuestionRule rule, AnswerValue answer) =>
        ScoreCalculator.Grade(
            [rule],
            new Dictionary<string, AnswerValue> { [rule.QuestionId] = answer },
            Identity());

    private static GradeIdentity Identity() => new("submission-1", "exam-1", "candidate-1", 7);

    /// <summary>一个正确选项的题：走 oneCorrect 分支。</summary>
    private static QuestionRule SingleChoiceRule(string correct, string blank, string wrong) =>
        new(
            "q-001",
            1,
            "subject-1",
            ["A", "B", "C", "D"],
            ["A"],
            new ManifestGroupScoring(
                new ManifestOneCorrect(correct, blank, wrong),
                new ManifestMultipleCorrect(correct, "2", blank, wrong))) { AllowAbstain = true };

    /// <summary>多个正确选项的题：走 multipleCorrect 分支。</summary>
    private static QuestionRule MultipleChoiceRule(IReadOnlyList<string> correctOptionIds) =>
        new(
            "q-001",
            1,
            "subject-1",
            ["A", "B", "C", "D"],
            correctOptionIds,
            new ManifestGroupScoring(
                new ManifestOneCorrect("{正确选项总数}", "0", "0"),
                new ManifestMultipleCorrect("{正确选项总数}", "{选择的正确选项数}", "0", "0")));
}
