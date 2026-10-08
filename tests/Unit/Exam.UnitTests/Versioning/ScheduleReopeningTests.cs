using Exam.Contracts.Common;
using Exam.Contracts.Versioning;

namespace Exam.UnitTests.Versioning;

// Synthetic schedule and participants; no real exam data.
public sealed partial class ExamVersioningServiceTests
{
    [Fact]
    public void Explicit_extension_reopens_and_preserves_submitted_participants_and_original_start()
    {
        var service = CreateService(now: BaseTime.AddHours(1));
        var originalStart = service.GetExam("exam-1").Value!.ActualStartedAt;
        Assert.True(service.RegisterParticipant(new("exam-1", "synthetic-active", ParticipationBusinessState.Answering)).Succeeded);
        Assert.True(service.RegisterParticipant(new("exam-1", "synthetic-submitted", ParticipationBusinessState.Submitted)).Succeeded);
        Assert.True(service.ExecuteScheduleTrigger(new("exam-1", 1, ScheduleTriggerKind.End, BaseTime.AddHours(2))).Value!.Applied);
        var reopened = service.UpdateSchedule(new("exam-1", 1,
            new(BaseTime.AddHours(1), BaseTime.AddHours(4)), BaseTime.AddHours(3)) { ReopenEndedExam = true });
        Assert.True(reopened.Succeeded);
        Assert.Equal(ExamLifecycle.Running, reopened.Value!.LifecycleState);
        Assert.Equal(originalStart, reopened.Value.ActualStartedAt);
        Assert.Null(reopened.Value.ActualEndedAt);
        Assert.Equal(2, reopened.Value.ReopenedAtScheduleVersion);
        Assert.Equal(ParticipationBusinessState.Submitted, reopened.Value.Participants["synthetic-submitted"].ExamState);
        Assert.False(service.ChangeParticipantState(new("exam-1", "synthetic-submitted", ParticipationBusinessState.Answering)).Succeeded);
        Assert.True(service.RegisterParticipant(new("exam-1", "synthetic-new", ParticipationBusinessState.Answering)).Succeeded);
        Assert.False(service.ExecuteScheduleTrigger(new("exam-1", 1, ScheduleTriggerKind.End, BaseTime.AddHours(3))).Value!.Applied);
        Assert.Equal(ExamLifecycle.Running, service.GetExam("exam-1").Value!.LifecycleState);
        Assert.True(service.ExecuteScheduleTrigger(new("exam-1", 2, ScheduleTriggerKind.End, BaseTime.AddHours(4))).Value!.Applied);
        Assert.Equal(ExamLifecycle.Ended, service.GetExam("exam-1").Value!.LifecycleState);
    }

    [Theory]
    [InlineData(false, ExamLifecycle.Ended)]
    [InlineData(true, ExamLifecycle.Scheduled)]
    public void Historical_corrections_keep_their_policy_while_new_future_schedules_can_reopen(bool reopen, ExamLifecycle expected)
    {
        var service = CreateService(now: BaseTime.AddHours(3));
        var result = service.UpdateSchedule(new("exam-1", 1,
            new(BaseTime.AddHours(4), BaseTime.AddHours(5)), BaseTime.AddHours(3)) { ReopenEndedExam = reopen });
        Assert.Equal(expected, result.Value!.LifecycleState);
        Assert.Equal(reopen ? 2 : 0, result.Value.ReopenedAtScheduleVersion);
    }

    [Fact]
    public void An_extension_that_is_still_in_the_past_does_not_reopen()
    {
        var service = CreateService(now: BaseTime.AddHours(3));
        var result = service.UpdateSchedule(new("exam-1", 1,
            new(BaseTime.AddHours(1), BaseTime.AddMinutes(150)), BaseTime.AddHours(3)) { ReopenEndedExam = true });
        Assert.Equal(ExamLifecycle.Ended, result.Value!.LifecycleState);
        Assert.Equal(0, result.Value.ReopenedAtScheduleVersion);
    }
}
