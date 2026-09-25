using Exam.Infrastructure.Postgres.Scoring;

namespace Exam.IntegrationTests.Scoring;

public sealed partial class PostgresGradingScopeTests
{
    [Fact]
    public async Task RecalculationQueueCannotEnqueueSubmissionFromAnotherExam()
    {
        await SeedAsync();
        var queue = new PostgresRecalculationQueue(fixture.DataSource);
        Assert.False(await queue.EnsureQueuedAsync(ExamId, "pg-scope-other", 3));
        await using var count = fixture.DataSource.CreateCommand(
            "SELECT count(*) FROM exam.grading_outbox WHERE submission_id = 'pg-scope-other';");
        Assert.Equal(0L, await count.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RecalculationQueueDoesNotResetActiveConsumerClaim()
    {
        await SeedAsync();
        await using (var missing = fixture.DataSource.CreateCommand("DELETE FROM exam.grading_results WHERE submission_id = 'pg-scope-current';"))
            await missing.ExecuteNonQueryAsync();
        var queue = new PostgresRecalculationQueue(fixture.DataSource);
        Assert.True(await queue.EnsureQueuedAsync(ExamId, "pg-scope-current", 2));
        var tasks = new PostgresGradingTaskSource(fixture.DataSource);
        var now = DateTimeOffset.UtcNow;
        var current = (await tasks.ClaimAsync(100, now, TimeSpan.FromMinutes(5)))
            .Single(task => task.SubmissionId == "pg-scope-current");
        Assert.False(await queue.EnsureQueuedAsync(ExamId, "pg-scope-current", 2));
        Assert.True(await tasks.CompleteAsync(current));
    }

    [Fact]
    public async Task RecalculationQueueSkipsSameOrNewerResultAndDeduplicatesNewVersion()
    {
        await SeedAsync();
        var queue = new PostgresRecalculationQueue(fixture.DataSource);
        Assert.False(await queue.EnsureQueuedAsync(ExamId, "pg-scope-current", 1));
        Assert.False(await queue.EnsureQueuedAsync(ExamId, "pg-scope-current", 2));
        Assert.True(await queue.EnsureQueuedAsync(ExamId, "pg-scope-current", 3));
        Assert.False(await queue.EnsureQueuedAsync(ExamId, "pg-scope-current", 3));
        await using var count = fixture.DataSource.CreateCommand(
            "SELECT count(*) FROM exam.grading_outbox WHERE submission_id = 'pg-scope-current' AND completed_at IS NULL;");
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public async Task FailureIsEffectiveOnlyBeyondActualPersistedResult(long failedVersion, bool expectedFailure)
    {
        await SeedAsync();
        await using (var fail = fixture.DataSource.CreateCommand("""
            INSERT INTO exam.grading_outbox (submission_id, grading_version, last_error)
            VALUES ('pg-scope-current', $1, 'synthetic acknowledgement failure')
            ON CONFLICT (submission_id, grading_version) DO UPDATE SET completed_at = NULL, last_error = EXCLUDED.last_error;
            """))
        {
            fail.Parameters.AddWithValue(failedVersion);
            await fail.ExecuteNonQueryAsync();
        }
        var state = (await new PostgresGradingScope(fixture.DataSource).ListStatesAsync(ExamId))
            .Single(item => item.SubmissionId == "pg-scope-current");
        Assert.Equal(2, state.GradedVersion);
        Assert.Equal(expectedFailure, state.GradingFailed);
    }
}
