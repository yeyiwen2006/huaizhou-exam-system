using Exam.Application.Commands;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Versioning;
using Exam.Infrastructure.Postgres.Commands;
using Exam.Infrastructure.Postgres.Participation;
using Exam.Infrastructure.Postgres.Versioning;

namespace Exam.IntegrationTests.Versioning;

public sealed partial class PostgresExamVersioningServiceTests
{
    [Fact]
    public async Task Postponed_running_exam_persists_scheduled_state_and_restarts_at_the_new_start()
    {
        var exam = "synthetic-postpone-" + Guid.NewGuid().ToString("N");
        var now = InitialNow.AddYears(100);
        var service = new PostgresExamVersioningService(fixture.DataSource);
        Assert.True(service.CreateExam(new(exam, new(now.AddHours(1), now.AddHours(2)), now, exam + "-create")).Succeeded);
        await StorePaperPackageAsync(exam, exam + "-paper", 1);
        Assert.True(service.PublishPaper(new(exam, 0, exam + "-paper", Structure(1), [],
            new(true, true, true, true), "synthetic-operator", "synthetic postponement", now, exam + "-publish")).Succeeded);
        await MarkInstructionsReadyAsync(exam);
        var active = await SeedReopeningParticipantAsync(exam, "waiting");
        var submitted = await SeedReopeningParticipantAsync(exam, "submitted");
        Assert.True(service.ExecuteScheduleTrigger(new(exam, 1, ScheduleTriggerKind.Start, now.AddHours(1), exam + "-start")).Succeeded);
        var request = new UpdateScheduleRequest(exam, 1, new(now.AddHours(3), now.AddHours(4)), now.AddMinutes(90), exam + "-postpone")
        { ReopenEndedExam = true, ReevaluateLifecycle = true };
        Assert.Equal(ExamLifecycle.Scheduled, service.UpdateSchedule(request).Value!.LifecycleState);

        var restarted = new PostgresExamVersioningService(fixture.DataSource);
        Assert.Equal(ExamLifecycle.Scheduled, restarted.GetExam(exam).Value!.LifecycleState);
        var entry = await new PostgresExamEntryDirectory(fixture.DataSource).FindAsync(exam);
        Assert.Equal(ExamLifecycle.Scheduled, entry!.Lifecycle);
        Assert.Equal(2, entry.ReopenedAtScheduleVersion);
        Assert.Equal(request.Schedule.StartsAt, entry.ScheduledStart);
        var commands = new ReliableCommandService(new("synthetic-server", "synthetic-node", 1),
            new PostgresCommandEventStore(fixture.DataSource), new PostgresParticipationAccessReader(fixture.DataSource));
        var due = await commands.GetDueForDeviceAsync(active.Participation, 1, active.Device, 1, "synthetic-server", 1);
        Assert.True(due.IsSuccess, due.Message);
        var schedule = Assert.Single(due.Value!);
        Assert.Equal(CommandEventType.ScheduleChanged, schedule.EventType);
        Assert.Equal("scheduled", schedule.Metadata["lifecycleState"]);
        Assert.Equal(2, schedule.StateVersion);
        Assert.Equal(ParticipationBusinessState.Submitted,
            (await new PostgresParticipationAccessReader(fixture.DataSource).FindAsync(submitted.Participation))!.BusinessState);
        Assert.False(restarted.ExecuteScheduleTrigger(new(exam, 1, ScheduleTriggerKind.End, now.AddHours(2), exam + "-old-end")).Value!.Applied);
        Assert.True(restarted.ExecuteScheduleTrigger(new(exam, 2, ScheduleTriggerKind.Start, now.AddHours(3), exam + "-new-start")).Value!.Applied);
        Assert.Equal(ExamLifecycle.Running, restarted.GetExam(exam).Value!.LifecycleState);
        var resumed = await commands.GetDueForDeviceAsync(active.Participation, 1, active.Device, 1, "synthetic-server", 1);
        var start = Assert.Single(resumed.Value!, item => item.EventType == CommandEventType.Start);
        Assert.Equal(2, start.StateVersion);
        Assert.Equal("running", start.Metadata["lifecycleState"]);
    }
}
