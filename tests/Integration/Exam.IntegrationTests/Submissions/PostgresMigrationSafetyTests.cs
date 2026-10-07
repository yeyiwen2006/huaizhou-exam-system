using Exam.Infrastructure.Postgres;
using Exam.IntegrationTests.Postgres;
using Npgsql;

namespace Exam.IntegrationTests.Submissions;

public sealed partial class PostgresSubmissionStoreTests
{
    [Theory]
    [InlineData("Running", true)]
    [InlineData("ScheduledDue", true)]
    [InlineData("waiting", true)]
    [InlineData("answering", true)]
    [InlineData("ScheduledFuture", false)]
    [InlineData("Ended", false)]
    public async Task PendingMigrationRespectsActualExamMaintenanceWindow(string state, bool blocked)
    {
        var isolated = new PostgresDatabaseFixture();
        await isolated.InitializeAsync();
        try
        {
            if (state is "waiting" or "answering")
            {
                var context = await new PostgresSubmissionStoreTests(isolated).SeedParticipationAsync();
                await using var update = isolated.DataSource.CreateCommand(
                    "UPDATE exam.participations SET business_state=$1 WHERE participation_id=$2;");
                update.Parameters.AddWithValue(state);
                update.Parameters.AddWithValue(context.ParticipationId);
                await update.ExecuteNonQueryAsync();
            }
            else await SeedMigrationExamAsync(isolated.DataSource, state);
            var runner = new PostgresMigrationRunner(isolated.DataSource, typeof(PostgresSubmissionStoreTests).Assembly);
            if (blocked)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.MigrateAsync());
                Assert.Contains("禁止迁移数据库", error.Message);
                await using var probe = isolated.DataSource.CreateCommand("SELECT to_regclass('exam.migration_safety_probe') IS NULL;");
                Assert.Equal(true, await probe.ExecuteScalarAsync());
                await using var ledger = isolated.DataSource.CreateCommand(
                    "SELECT count(*) FROM exam.schema_migrations WHERE migration_name LIKE '%safety_probe%';");
                Assert.Equal(0L, await ledger.ExecuteScalarAsync());
            }
            else Assert.Single(await runner.MigrateAsync());
        }
        finally { await isolated.DisposeAsync(); }
    }

    [Fact]
    public async Task AlreadyAppliedMigrationsNeverPreventAnExamRecoveryRestart()
    {
        var isolated = new PostgresDatabaseFixture();
        await isolated.InitializeAsync();
        try
        {
            var runner = new PostgresMigrationRunner(isolated.DataSource, typeof(PostgresSubmissionStoreTests).Assembly);
            Assert.Single(await runner.MigrateAsync());
            await SeedMigrationExamAsync(isolated.DataSource, "Running");
            Assert.Empty(await runner.MigrateAsync());
            Assert.Empty(await new PostgresMigrationRunner(isolated.DataSource).MigrateAsync());
        }
        finally { await isolated.DisposeAsync(); }
    }

    private static async Task SeedMigrationExamAsync(NpgsqlDataSource data, string state)
    {
        var now = DateTimeOffset.UtcNow;
        var future = state == "ScheduledFuture";
        var lifecycle = state.StartsWith("Scheduled", StringComparison.Ordinal) ? "Scheduled" : state;
        await using var command = data.CreateCommand("""
            WITH stream AS (
                INSERT INTO exam.version_streams (exam_id,stream_revision,created_at,updated_at)
                VALUES ('synthetic-migration-window',1,$1,$1) RETURNING exam_id)
            INSERT INTO exam.version_state
                (exam_id,stream_revision,schedule_version,paper_version,grading_version,starts_at,ends_at,lifecycle,snapshot,updated_at)
            SELECT exam_id,1,1,0,0,$2,$3,$4,'{}'::jsonb,$1 FROM stream;
            """);
        command.Parameters.AddWithValue(now);
        command.Parameters.AddWithValue(future ? now.AddHours(1) : now.AddHours(-1));
        command.Parameters.AddWithValue(future ? now.AddHours(2) : now.AddHours(1));
        command.Parameters.AddWithValue(lifecycle);
        await command.ExecuteNonQueryAsync();
    }
}
