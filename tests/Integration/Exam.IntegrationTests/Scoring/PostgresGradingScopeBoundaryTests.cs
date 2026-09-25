using Exam.Contracts.Grading;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Postgres.Scoring;

namespace Exam.IntegrationTests.Scoring;

public sealed partial class PostgresGradingScopeTests
{
    [Fact]
    public async Task ScopeUsesPersistedResultVersionInsteadOfCompletedOutboxTarget()
    {
        await SeedAsync();
        await SaveScopeGradeAsync("pg-scope-current", "pg-candidate-current", 3);
        var state = (await new PostgresGradingScope(fixture.DataSource).ListStatesAsync(ExamId))
            .Single(item => item.SubmissionId == "pg-scope-current");
        Assert.Equal(3, state.GradedVersion);
    }

    [Fact]
    public async Task CompletedTaskWithoutPersistedResultIsNotGraded()
    {
        await SeedAsync();
        await using (var command = fixture.DataSource.CreateCommand("DELETE FROM exam.grading_results WHERE submission_id = 'pg-scope-current';"))
            await command.ExecuteNonQueryAsync();
        var state = (await new PostgresGradingScope(fixture.DataSource).ListStatesAsync(ExamId))
            .Single(item => item.SubmissionId == "pg-scope-current");
        Assert.Null(state.GradedVersion);
    }

    private async Task SaveScopeGradeAsync(string id, string candidate, long version)
    {
        await new PostgresGradingResultStore(fixture.DataSource).SaveAsync(new StoredGradingResult(
            new SubmissionGrade(id, ExamId, candidate, version,
                [new("q", 1, "subject", AnswerOutcome.Correct, "3")],
                [new("subject", "3", "3", false)], "3"), DateTimeOffset.UtcNow));
    }
}
