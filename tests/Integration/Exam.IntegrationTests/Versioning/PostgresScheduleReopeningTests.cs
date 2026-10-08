using Exam.Application.Commands;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Versioning;
using Exam.Infrastructure.Postgres.Commands;
using Exam.Infrastructure.Postgres.Participation;
using Exam.Infrastructure.Postgres.Versioning;

namespace Exam.IntegrationTests.Versioning;

// Synthetic database participants; the original submitted records are never reopened.
public sealed partial class PostgresExamVersioningServiceTests
{
    [Fact]
    public async Task Reopening_is_durable_pushes_new_window_and_blocks_old_end_and_submitted_reentry()
    {
        var exam = "synthetic-reopen-" + Guid.NewGuid().ToString("N");
        var now = InitialNow.AddYears(100);
        var service = new PostgresExamVersioningService(fixture.DataSource);
        Assert.True(service.CreateExam(new(exam, new(now.AddHours(1), now.AddHours(2)), now, exam + "-create")).Succeeded);
        await StorePaperPackageAsync(exam, exam + "-paper", 1);
        Assert.True(service.PublishPaper(new(exam, 0, exam + "-paper", Structure(1), [],
            new(true, true, true, true), "synthetic-operator", "synthetic reopening fixture", now, exam + "-publish")).Succeeded);
        await MarkInstructionsReadyAsync(exam);
        var active = await SeedReopeningParticipantAsync(exam, "waiting");
        var submitted = await SeedReopeningParticipantAsync(exam, "submitted");
        Assert.True(service.ExecuteScheduleTrigger(new(exam, 1, ScheduleTriggerKind.Start, now.AddHours(1), exam + "-start")).Succeeded);
        Assert.True(service.ExecuteScheduleTrigger(new(exam, 1, ScheduleTriggerKind.End, now.AddHours(2), exam + "-end")).Succeeded);
        var request = new UpdateScheduleRequest(exam, 1, new(now.AddHours(1), now.AddHours(4)), now.AddHours(3), exam + "-reopen")
        { ReopenEndedExam = true, Operator = "synthetic-operator", Reason = "合成延时" };
        var result = service.UpdateSchedule(request);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(ExamLifecycle.Running, result.Value!.LifecycleState);
        Assert.Null(result.Value.ActualEndedAt);
        var restarted = new PostgresExamVersioningService(fixture.DataSource);
        Assert.Equal(ExamLifecycle.Running, restarted.GetExam(exam).Value!.LifecycleState);
        Assert.Equal(2, restarted.GetExam(exam).Value!.ReopenedAtScheduleVersion);
        Assert.Equal(2, restarted.UpdateSchedule(request with { Now = now.AddMinutes(185) }).Value!.Versions.ScheduleVersion);
        var entry = await new PostgresExamEntryDirectory(fixture.DataSource).FindAsync(exam);
        Assert.Equal(ExamLifecycle.Running, entry!.Lifecycle);
        Assert.Equal(2, entry.ReopenedAtScheduleVersion);
        Assert.Equal(request.Schedule.EndsAt, entry.ScheduledEnd);

        var commands = new ReliableCommandService(new("synthetic-server", "synthetic-node", 1),
            new PostgresCommandEventStore(fixture.DataSource), new PostgresParticipationAccessReader(fixture.DataSource));
        var due = await commands.GetDueForDeviceAsync(active.Participation, 1, active.Device, 1, "synthetic-server", 1);
        Assert.True(due.IsSuccess, due.Message);
        var deliveries = due.Value!;
        Assert.Equal(2, deliveries.Count);
        Assert.Contains(deliveries, item => item.EventType == CommandEventType.ScheduleChanged);
        var start = Assert.Single(deliveries, item => item.EventType == CommandEventType.Start);
        Assert.All(deliveries, item =>
        {
            Assert.Equal(2, item.StateVersion);
            Assert.Equal(request.Schedule.EndsAt, item.NewScheduledEnd);
            Assert.Equal("2", item.Metadata["reopenedAtScheduleVersion"]);
        });
        Assert.NotNull(start.ProtectedUnlockReference);
        var oldEnd = $"version:{exam}-end:End:{active.Participation}";
        Assert.False((await commands.ResendAsync(oldEnd, active.Participation, 1, active.Device, 1)).IsSuccess);
        var submittedDue = await commands.GetDueForDeviceAsync(submitted.Participation, 1, submitted.Device, 1, "synthetic-server", 1);
        Assert.Empty(submittedDue.Value!);
        Assert.Equal(ParticipationBusinessState.Submitted,
            (await new PostgresParticipationAccessReader(fixture.DataSource).FindAsync(submitted.Participation))!.BusinessState);
        Assert.True(restarted.ExecuteScheduleTrigger(new(exam, 2, ScheduleTriggerKind.End, now.AddHours(4), exam + "-end-again")).Succeeded);
        Assert.Equal(ExamLifecycle.Ended, restarted.GetExam(exam).Value!.LifecycleState);
        // Even after the reopened window ends, a previous window's END must not be resent.
        Assert.False((await commands.ResendAsync(oldEnd, active.Participation, 1, active.Device, 1)).IsSuccess);
    }

    [Fact]
    public async Task Entry_confirmation_rejects_an_old_schedule_version_even_when_the_window_is_unchanged()
    {
        var exam = "synthetic-confirm-version-" + Guid.NewGuid().ToString("N");
        var now = InitialNow.AddYears(100);
        var schedule = new ScheduleWindow(now.AddHours(1), now.AddHours(2));
        var service = new PostgresExamVersioningService(fixture.DataSource);
        Assert.True(service.CreateExam(new(exam, schedule, now, exam + "-create")).Succeeded);
        var directory = new PostgresExamEntryDirectory(fixture.DataSource);
        var prior = (await directory.FindAsync(exam))!;
        Assert.True(service.UpdateSchedule(new(exam, 1, schedule, now, exam + "-update")
        { ReopenEndedExam = true }).Succeeded);
        var persistence = new PostgresParticipationPersistence(fixture.DataSource);
        Assert.False(await persistence.ExecuteTransactionAsync(transaction => transaction.IsExamEntryCurrent(prior)));
        var current = (await directory.FindAsync(exam))!;
        Assert.True(await persistence.ExecuteTransactionAsync(transaction => transaction.IsExamEntryCurrent(current)));
    }

    [Fact]
    public void A_pre_upgrade_schedule_retry_returns_the_old_receipt_without_reopening_history()
    {
        var exam = "synthetic-historical-" + Guid.NewGuid().ToString("N");
        var now = InitialNow.AddYears(100);
        var service = new PostgresExamVersioningService(fixture.DataSource);
        Assert.True(service.CreateExam(new(exam, new(now.AddHours(-2), now.AddHours(-1)), now, exam + "-create")).Succeeded);
        var historical = new UpdateScheduleRequest(exam, 1, new(now.AddHours(-2), now.AddHours(1)), now, exam + "-correct");
        Assert.Equal(ExamLifecycle.Ended, service.UpdateSchedule(historical).Value!.LifecycleState);
        var retry = new PostgresExamVersioningService(fixture.DataSource).UpdateSchedule(historical with
        { Now = now.AddMinutes(1), ReopenEndedExam = true });
        Assert.True(retry.Succeeded, retry.Error);
        Assert.Equal(ExamLifecycle.Ended, retry.Value!.LifecycleState);
        Assert.Equal(0, retry.Value.ReopenedAtScheduleVersion);
        Assert.Equal(ExamLifecycle.Ended, service.GetExam(exam).Value!.LifecycleState);
    }

    private async Task<(string Participation, string Device)> SeedReopeningParticipantAsync(string exam, string state)
    {
        var id = "synthetic-" + Guid.NewGuid().ToString("N");
        await using var seed = fixture.DataSource.CreateCommand("""
            WITH device AS (
                INSERT INTO exam.activation_devices
                    (device_id,client_number,activation_date,activation_status,protected_credential,credential_version,created_at,version)
                VALUES ($1,$2,DATE '2026-10-08','active','synthetic-verifier-not-a-secret',1,clock_timestamp(),1)
                RETURNING device_id),
            candidate AS (
                INSERT INTO exam.candidate_roster_entries (exam_id,candidate_id,candidate_name,class_text,admission_no)
                VALUES ($3,$1,'合成考生','001',$5) RETURNING exam_id,candidate_id)
            INSERT INTO exam.participations
                (participation_id,exam_id,candidate_id,entry_version,business_state,authorized_device_id,authorization_version,version)
            SELECT $1,candidate.exam_id,candidate.candidate_id,1,$4,device.device_id,1,1
                FROM device CROSS JOIN candidate;
            """);
        seed.Parameters.AddWithValue(id);
        seed.Parameters.AddWithValue(Random.Shared.NextInt64(10_000_000_000, 99_999_999_999).ToString());
        seed.Parameters.AddWithValue(exam);
        seed.Parameters.AddWithValue(state);
        seed.Parameters.AddWithValue(state == "submitted" ? "000002" : "000001");
        await seed.ExecuteNonQueryAsync();
        return (id, id);
    }
}
