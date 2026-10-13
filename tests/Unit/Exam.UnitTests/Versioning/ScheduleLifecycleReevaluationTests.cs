using Exam.Contracts.Common;
using Exam.Contracts.Versioning;

namespace Exam.UnitTests.Versioning;

public sealed partial class ExamVersioningServiceTests
{
    [Theory]
    [InlineData(false, 1, 2, ExamLifecycle.Scheduled)]
    [InlineData(true, 1, 2, ExamLifecycle.Scheduled)]
    [InlineData(false, 0, 1, ExamLifecycle.Running)]
    [InlineData(true, 0, 1, ExamLifecycle.Running)]
    [InlineData(false, -1, 1, ExamLifecycle.Running)]
    [InlineData(true, -1, 1, ExamLifecycle.Running)]
    [InlineData(false, -1, 0, ExamLifecycle.Ended)]
    [InlineData(true, -1, 0, ExamLifecycle.Ended)]
    public void New_time_saves_recalculate_from_server_time_and_preserve_participants(
        bool wasEnded, int startOffset, int endOffset, ExamLifecycle expected)
    {
        var service = CreateService(now: BaseTime.AddHours(1));
        var actualStart = service.GetExam("exam-1").Value!.ActualStartedAt;
        Assert.True(service.RegisterParticipant(new("exam-1", "active", ParticipationBusinessState.Answering)).Succeeded);
        Assert.True(service.RegisterParticipant(new("exam-1", "submitted", ParticipationBusinessState.Submitted)).Succeeded);
        if (wasEnded)
            Assert.True(service.ExecuteScheduleTrigger(new("exam-1", 1, ScheduleTriggerKind.End, BaseTime.AddHours(2))).Value!.Applied);
        var now = BaseTime.AddHours(3);
        var result = service.UpdateSchedule(new("exam-1", 1,
            new(now.AddHours(startOffset), now.AddHours(endOffset)), now)
        { ReevaluateLifecycle = true });

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(expected, result.Value!.LifecycleState);
        Assert.Equal(actualStart, result.Value.ActualStartedAt);
        Assert.Equal(ParticipationBusinessState.Answering, result.Value.Participants["active"].ExamState);
        Assert.Equal(ParticipationBusinessState.Submitted, result.Value.Participants["submitted"].ExamState);
        Assert.Equal(expected == ExamLifecycle.Ended, result.Value.ActualEndedAt is not null);
        Assert.False(service.ExecuteScheduleTrigger(new("exam-1", 1, ScheduleTriggerKind.End, now.AddHours(3))).Value!.Applied);
        Assert.Equal(expected, service.GetExam("exam-1").Value!.LifecycleState);
    }

    [Fact]
    public void Postponing_a_started_exam_keeps_the_original_paper_structure_locked()
    {
        var service = CreateService();
        Assert.True(service.PublishPaper(PaperRequest(0)).Succeeded);
        Assert.True(service.ExecuteScheduleTrigger(new("exam-1", 1, ScheduleTriggerKind.Start, BaseTime.AddHours(1))).Value!.Applied);
        Assert.Equal(ExamLifecycle.Scheduled, service.UpdateSchedule(new("exam-1", 1,
            new(BaseTime.AddHours(3), BaseTime.AddHours(4)), BaseTime.AddHours(2))
        { ReevaluateLifecycle = true }).Value!.LifecycleState);
        var changed = new PaperStructure([new("q2", 1, "subject", "group", "single", ["A", "B"])]);
        Assert.Equal(VersioningErrorCode.StructuralChangeRejected,
            service.PublishPaper(PaperRequest(1, "paper-v2", changed)).ErrorCode);
    }
}
