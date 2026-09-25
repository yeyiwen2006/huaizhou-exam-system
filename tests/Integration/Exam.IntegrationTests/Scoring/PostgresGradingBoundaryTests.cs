using Exam.Domain.Scoring;
using Exam.Infrastructure.Postgres;
using Exam.Infrastructure.Postgres.Scoring;
using NpgsqlTypes;

namespace Exam.IntegrationTests.Scoring;

public sealed partial class PostgresGradingPersistenceTests
{
    private static readonly DateTimeOffset BoundaryNow = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan BoundaryLease = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task ResultStore_RejectsStaleAndConflictingSameVersionWrites()
    {
        await SeedAsync();
        var store = new PostgresGradingResultStore(fixture.DataSource);
        var current = Result(2, "3", BoundaryNow);
        await store.SaveAsync(current);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Result(1, "0", BoundaryNow)).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Result(2, "0", BoundaryNow)).AsTask());
        await store.SaveAsync(current with { GradedAt = BoundaryNow.AddHours(1) });
        var read = (await store.FindAsync(SubmissionId))!;
        Assert.Equal(2, read.GradingVersion);
        Assert.Equal("3", read.Grade.ExactTotalScore);
        Assert.Equal(BoundaryNow, read.GradedAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResultStore_RejectsWrongSubmissionOwnerOnInsertAndUpdate(bool existing)
    {
        await SeedAsync();
        var store = new PostgresGradingResultStore(fixture.DataSource);
        var result = Result(1, "3", BoundaryNow);
        if (existing) await store.SaveAsync(result);
        var invalid = result with { Grade = result.Grade with { CandidateId = "other", GradingVersion = 2 } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(invalid).AsTask());
        invalid = result with { Grade = result.Grade with { ExamId = "other", GradingVersion = 2 } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(invalid).AsTask());
        Assert.Equal(existing ? CandidateId : null, (await store.FindAsync(SubmissionId))?.Grade.CandidateId);
    }

    [Fact]
    public async Task TaskSource_StaleConsumerCannotCompleteOrReleaseNewLease()
    {
        await SeedAsync();
        var first = new PostgresGradingTaskSource(fixture.DataSource);
        var second = new PostgresGradingTaskSource(fixture.DataSource);
        var old = Assert.Single(await first.ClaimAsync(1, BoundaryNow, BoundaryLease));
        var current = Assert.Single(await second.ClaimAsync(1, BoundaryNow + BoundaryLease, BoundaryLease));
        Assert.False(await first.CompleteAsync(old));
        Assert.False(await first.FailAsync(old, "old consumer"));
        Assert.Empty(await first.ClaimAsync(1, BoundaryNow + BoundaryLease + TimeSpan.FromSeconds(1), BoundaryLease));
        Assert.True(await second.CompleteAsync(current));
        var completed = await ReadCompletedAtAsync();
        Assert.False(await first.FailAsync(current, "late failure"));
        Assert.False(await first.CompleteAsync(current));
        Assert.Equal(completed, await ReadCompletedAtAsync());
        Assert.Null(await ReadLastErrorAsync());
    }

    [Fact]
    public async Task TaskSource_ConcurrentClaimsHaveExactlyOneOwner()
    {
        await SeedAsync();
        var batches = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
            await new PostgresGradingTaskSource(fixture.DataSource).ClaimAsync(1, BoundaryNow, BoundaryLease)));
        var task = Assert.Single(batches.SelectMany(batch => batch));
        Assert.Equal(1, task.AttemptCount);
        Assert.True(await new PostgresGradingTaskSource(fixture.DataSource).CompleteAsync(task));
    }

    [Fact]
    public async Task TaskSource_FailedHeadDoesNotStarveLaterVersionAndUnicodeErrorsRemainValid()
    {
        await SeedAsync();
        await ExecuteAsync("INSERT INTO exam.grading_outbox (submission_id, grading_version) VALUES ('pg-grading-submission', 2);");
        var source = new PostgresGradingTaskSource(fixture.DataSource);
        var first = Assert.Single(await source.ClaimAsync(1, BoundaryNow, BoundaryLease));
        Assert.Equal(1, first.GradingVersion);
        Assert.True(await source.FailAsync(first, new string('x', 1999) + "\U0001F642suffix"));
        await using (var error = fixture.DataSource.CreateCommand("SELECT last_error FROM exam.grading_outbox WHERE grading_version = 1;"))
            Assert.Equal(new string('x', 1999), await error.ExecuteScalarAsync());
        var next = Assert.Single(await source.ClaimAsync(1, BoundaryNow, BoundaryLease));
        Assert.Equal(2, next.GradingVersion);
    }

    [Theory]
    [InlineData("sources")]
    [InlineData("version")]
    [InlineData("exam")]
    public async Task Inputs_DoNotRelabelOldRulesOrIgnoreInvalidLatestPublication(string defect)
    {
        await SeedAsync();
        var mutation = defect switch
        {
            "sources" => "payload - 'manifestJson' - 'answerKeyJson'",
            "version" => "jsonb_set(payload, '{expectedVersion}', '0'::jsonb)",
            _ => "jsonb_set(payload, '{examId}', '\"other-exam\"'::jsonb)"
        };
        await ExecuteAsync($"UPDATE exam.version_events SET payload = {mutation} WHERE stream_revision = (SELECT max(stream_revision) FROM exam.version_events);");
        await Assert.ThrowsAsync<InvalidDataException>(() => new PostgresGradingInputs(fixture.DataSource).LoadRulesAsync(ExamId).AsTask());
    }

    [Fact]
    public async Task Inputs_DoNotFallBackWhenNewerLegacyEventHasNoSource()
    {
        await SeedAsync();
        await ExecuteAsync("""
            UPDATE exam.version_state SET grading_version = 4, stream_revision = 4;
            UPDATE exam.version_streams SET stream_revision = 4;
            INSERT INTO exam.version_events (exam_id, stream_revision, operation_type, payload, recorded_at)
            VALUES ('pg-grading-exam', 4, 'grading.publish',
                '{"examId":"pg-grading-exam","expectedVersion":3,"ruleSetId":"legacy-new"}'::jsonb, clock_timestamp());
            """);
        await Assert.ThrowsAsync<InvalidDataException>(() => new PostgresGradingInputs(fixture.DataSource).LoadRulesAsync(ExamId).AsTask());
    }

    [Fact]
    public async Task Inputs_NullFrozenAnswersAreCorruptionNotEmptyAnswerSheet()
    {
        await SeedAsync();
        await ExecuteAsync("UPDATE exam.final_submissions SET frozen_answers = 'null'::jsonb;");
        await Assert.ThrowsAsync<InvalidDataException>(() => new PostgresGradingInputs(fixture.DataSource).LoadSubmissionAsync(SubmissionId).AsTask());
    }

    [Fact]
    public async Task MigrationRestartAndReapplicationPreserveFrozenAnswerAndReceipt()
    {
        await SeedAsync();
        var inputs = new PostgresGradingInputs(fixture.DataSource);
        var before = (await inputs.LoadSubmissionAsync(SubmissionId))!;
        var store = new PostgresGradingResultStore(fixture.DataSource);
        await store.SaveAsync(Result(3, "3", BoundaryNow.ToOffset(TimeSpan.FromHours(8))));
        Assert.Empty(await new PostgresMigrationRunner(fixture.DataSource).MigrateAsync());
        Assert.Equal(BoundaryNow, (await store.FindAsync(SubmissionId))!.GradedAt);
        // 只在夹具的随机合成库中演练回退；计分结果是派生数据，原始答卷不变。
        await ExecuteAsync("""
            DROP TABLE exam.grading_results;
            DELETE FROM exam.schema_migrations WHERE migration_name = '202609252100_D04_grading_results';
            """);
        var applied = await new PostgresMigrationRunner(fixture.DataSource).MigrateAsync();
        Assert.Equal("202609252100_D04_grading_results", Assert.Single(applied));
        var after = (await inputs.LoadSubmissionAsync(SubmissionId))!;
        Assert.Equal(before.Receipt, after.Receipt);
        Assert.Equal(before.FrozenAnswers["q-001"].OptionIds, after.FrozenAnswers["q-001"].OptionIds);
        Assert.Single(await new PostgresGradingTaskSource(fixture.DataSource).ClaimAsync(1, BoundaryNow, BoundaryLease));
        Assert.Null(await store.FindAsync(SubmissionId));
        await store.SaveAsync(Result(3, "3", BoundaryNow));
        Assert.Single(await store.ListAsync(ExamId, 3));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = fixture.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
