using Exam.Application.Scoring;
using Exam.Contracts.Submissions;
using Exam.Contracts.Common;
using Exam.Domain.Submissions;
using Exam.Infrastructure.Postgres.Scoring;
using Exam.Infrastructure.Postgres.Submissions;

namespace Exam.IntegrationTests.Scoring;

public sealed partial class PostgresGradingPersistenceTests
{
    [Fact]
    public async Task SchedulerAndWorkerConvergeOldTaskToActualLatestResultWithoutDuplicateGrade()
    {
        var (scheduler, _, scope) = await AlignRecalculationAsync();
        var state = Assert.Single(await scope.ListStatesAsync(ExamId));
        Assert.Equal(3, state.GradedVersion);
        Assert.False(state.GradingFailed);
        var complete = await scheduler.RunOnceAsync(ExamId, 3);
        Assert.True(complete.Readiness.RecalculationReady);
        Assert.True(complete.Plan.IsComplete);
        Assert.Equal(0, complete.Enqueued);
        Assert.Single(await new PostgresGradingResultStore(fixture.DataSource).ListAsync(ExamId, 3));
    }

    [Fact]
    public async Task MissingDerivedResultReopensCompletedTaskOnceAndPreservesOriginalSubmission()
    {
        var (scheduler, worker, _) = await AlignRecalculationAsync();
        var original = await ReadCanonicalSubmissionRowAsync();
        await ExecuteAsync("DELETE FROM exam.grading_results;");
        var queue = new PostgresRecalculationQueue(fixture.DataSource);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
            await queue.EnsureQueuedAsync(ExamId, SubmissionId, 3)));
        Assert.Single(attempts, reopened => reopened);
        var recovering = await scheduler.RunOnceAsync(ExamId, 3);
        Assert.False(recovering.Readiness.RecalculationReady);
        Assert.Equal(0, recovering.Enqueued);
        Assert.Equal(1, (await worker.RunOnceAsync()).Graded);
        Assert.True((await scheduler.RunOnceAsync(ExamId, 3)).Readiness.RecalculationReady);
        Assert.Equal(original, await ReadCanonicalSubmissionRowAsync());
        Assert.Single(await new PostgresGradingResultStore(fixture.DataSource).ListAsync(ExamId, 3));
    }

    [Fact]
    public async Task LateA05SubmissionInvalidatesPriorReadinessAndUsesCurrentRules()
    {
        var (scheduler, worker, _) = await AlignRecalculationAsync();
        var original = (await new PostgresGradingInputs(fixture.DataSource).LoadSubmissionAsync(SubmissionId))!;
        await using (var roster = fixture.DataSource.CreateCommand("""
            INSERT INTO exam.candidate_roster_entries (exam_id, candidate_id, candidate_name, class_text, admission_no)
            VALUES ('pg-grading-exam', 'pg-late-candidate', '合成补收考生', '001', '0999');
            INSERT INTO exam.activation_devices (device_id, client_number, activation_date, activation_status, protected_credential, credential_version, created_at, version)
            VALUES ('pg-late-device', '20260925999', DATE '2026-09-25', 'active', 'synthetic-protected', 1, clock_timestamp(), 1);
            """)) await roster.ExecuteNonQueryAsync();
        var submissions = new PostgresSubmissionStore(fixture.DataSource);
        await submissions.SetAuthorizationStateAsync(new(ExamId, "pg-late-candidate", "pg-late-participation", 1,
            "pg-late-device", 1, ParticipationBusinessState.Answering, 1, 3));
        var late = await submissions.SaveSubmissionAsync(new SubmitFinalAnswersRequest(
            "pg-late-request", "pg-late-submission", ExamId, "pg-late-candidate", "pg-late-participation", 1,
            "pg-late-device", 1, 1, original.FrozenAnswers), BoundaryNow, "pg-late-receipt");
        Assert.Equal(SubmissionPersistenceStatus.Saved, late.Status);
        Assert.Equal(3, late.Submission!.Receipt.GradingVersion);
        var pending = await scheduler.RunOnceAsync(ExamId, 3);
        Assert.Equal(2, pending.SubmissionCount);
        Assert.Equal("pg-late-submission", Assert.Single(pending.Plan.Pending).SubmissionId);
        Assert.False(pending.Readiness.RecalculationReady);
        // A05 已在收卷事务内创建任务；调度器不能重复建任务或重置它的状态。
        Assert.Equal(0, pending.Enqueued);
        Assert.Equal(1, (await worker.RunOnceAsync()).Graded);
        var complete = await scheduler.RunOnceAsync(ExamId, 3);
        Assert.True(complete.Readiness.RecalculationReady);
        Assert.Equal(2, complete.Plan.UpToDate.Count);
        Assert.Equal(2, (await new PostgresGradingResultStore(fixture.DataSource).ListAsync(ExamId, 3)).Count);
        Assert.Equal(original.Receipt, (await new PostgresGradingInputs(fixture.DataSource).LoadSubmissionAsync(SubmissionId))!.Receipt);
    }

    private async Task<(RecalculationScheduler Scheduler, GradingCoordinator Worker, PostgresGradingScope Scope)> AlignRecalculationAsync()
    {
        await SeedAsync();
        var scope = new PostgresGradingScope(fixture.DataSource);
        var scheduler = new RecalculationScheduler(scope, new PostgresRecalculationQueue(fixture.DataSource));
        var pending = await scheduler.RunOnceAsync(ExamId, 3);
        Assert.Equal(1, pending.Enqueued);
        Assert.False(pending.Readiness.RecalculationReady);
        var worker = new GradingCoordinator(new PostgresGradingTaskSource(fixture.DataSource),
            new PostgresGradingResultStore(fixture.DataSource), new PostgresGradingInputs(fixture.DataSource),
            timeProvider: new FixedTimeProvider(BoundaryNow));
        var report = await worker.RunOnceAsync();
        // 原版本 1 任务和重算版本 3 任务都采用最新规则，但成绩表只存一份结果。
        Assert.Equal(2, report.Claimed);
        Assert.True(report.Graded == 2, string.Join(" | ", report.FailureMessages));
        return (scheduler, worker, scope);
    }

    private async Task<string> ReadCanonicalSubmissionRowAsync()
    {
        await using var command = fixture.DataSource.CreateCommand(
            "SELECT to_jsonb(submission)::text FROM exam.final_submissions AS submission WHERE submission_id = $1;");
        command.Parameters.AddWithValue(SubmissionId);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
