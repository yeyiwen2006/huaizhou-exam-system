using Exam.Admin.ViewModels;
using Exam.Contracts.Submissions;

namespace Exam.AdminUnitTests;

public sealed class MonitoringReadableDetailsTests
{
    [Fact]
    public void OpaqueOptionsUsePublishedOrderWithoutExposingInternalIds()
    {
        var answers = new Dictionary<string, AnswerValue> { ["opaque-question"] = AnswerValue.Selected("opaque-second") };
        Assert.Equal("第 7 题：第 2 个选项", MonitoringDisplay.CurrentAnswer("opaque-question", answers, 7,
            ["opaque-first", "opaque-second"]));
        Assert.Equal("第 7 题：选项待核对", MonitoringDisplay.CurrentAnswer("opaque-question", answers, 7));
        answers["opaque-question"] = AnswerValue.Selected("A", "B");
        Assert.Equal("第 7 题：AB", MonitoringDisplay.CurrentAnswer("opaque-question", answers, 7));
    }

    [Fact]
    public void CommunicationAndAnswerVersionCannotInventSavedAnswers()
    {
        var row = new ConflictDeviceRow(MonitoringFixture.Conflict(answerVersion: 9), _ => { });
        Assert.Equal("无保存记录", row.LastSyncedText);
        Assert.Equal("无已同步答案｜正在答题", row.ProgressText);
        var empty = new ConflictDeviceRow(MonitoringFixture.Conflict() with
            { LastSyncedAt = MonitoringFixture.At, SavedAnswerCount = 0 }, _ => { });
        Assert.Equal("已保存 0 题｜正在答题", empty.ProgressText);
        Assert.Equal("2026-10-05 09:30:00", empty.LastSyncedText);
    }
}
