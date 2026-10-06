using Exam.Admin.ViewModels;
using Exam.Contracts.Submissions;

namespace Exam.AdminUnitTests;

public sealed class MonitoringOptionOrderTests
{
    [Fact]
    public void SavedSelectionIsDisplayedInPublishedOrderRatherThanClickOrder()
    {
        var answers = new Dictionary<string, AnswerValue>
        {
            ["question"] = AnswerValue.Selected("opaque-third", "opaque-first")
        };
        Assert.Equal("第 7 题：第 1 个选项、第 3 个选项",
            MonitoringDisplay.CurrentAnswer("question", answers, 7,
                ["opaque-first", "opaque-second", "opaque-third"]));
    }

    [Fact]
    public void ALetterAbsentFromPublishedOptionsIsNotPresentedAsAValidAnswer()
    {
        var answers = new Dictionary<string, AnswerValue> { ["question"] = AnswerValue.Selected("Z") };
        Assert.Equal("第 7 题：选项待核对",
            MonitoringDisplay.CurrentAnswer("question", answers, 7, ["A", "B"]));
    }

    [Fact]
    public void LegacyLetterSelectionsStayReadableWithoutExposingOpaqueIdentifiers()
    {
        var answers = new Dictionary<string, AnswerValue> { ["question"] = AnswerValue.Selected("B", "A") };
        Assert.Equal("第 7 题：AB", MonitoringDisplay.CurrentAnswer("question", answers, 7));
        answers["question"] = AnswerValue.Selected("opaque-option");
        Assert.Equal("第 7 题：选项待核对", MonitoringDisplay.CurrentAnswer("question", answers, 7));
    }
}
