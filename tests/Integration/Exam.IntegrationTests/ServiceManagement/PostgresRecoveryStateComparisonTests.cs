using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;
using Exam.IntegrationTests.Postgres;
using Npgsql;

namespace Exam.IntegrationTests.ServiceManagement;

[Trait("Category", "Postgres")]
public sealed class PostgresRecoveryStateComparisonTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _original = new();
    private readonly PostgresDatabaseFixture _recovery = new();
    private readonly string _role = "f01_reader_" + Guid.NewGuid().ToString("N");
    private bool _roleCreated;
    private static readonly ServiceConfiguration Configuration = new("server", "node", "127.0.0.1", 8443, "/synthetic", false, 1);

    public async Task InitializeAsync()
    {
        await _original.InitializeAsync();
        await _recovery.InitializeAsync();
        await ExecuteAsync(_original, $"CREATE ROLE {_role} NOLOGIN NOSUPERUSER NOBYPASSRLS");
        _roleCreated = true;
        foreach (var database in new[] { _original, _recovery })
            await ExecuteAsync(database, $"""
                BEGIN;
                UPDATE exam.schema_migrations SET applied_at='2026-01-01T00:00:00Z';
                INSERT INTO exam.server_state VALUES ('server', 'node', 1, 'EmergencySingleNode', 1, '2026-01-01T00:00:00Z');
                INSERT INTO exam.server_nodes VALUES ('server', 'node', 'Primary', true, false, 0, true, true, '2026-01-01T00:00:00Z');
                CREATE TABLE exam."synthetic""visibility" (id integer PRIMARY KEY, value text);
                INSERT INTO exam."synthetic""visibility" VALUES (1, 'visible'), (2, 'hidden');
                GRANT USAGE ON SCHEMA exam TO {_role};
                GRANT SELECT ON ALL TABLES IN SCHEMA exam TO {_role};
                GRANT SELECT ON ALL SEQUENCES IN SCHEMA exam TO {_role};
                COMMIT;
                """);
    }

    public async Task DisposeAsync()
    {
        // Dropping both isolated databases removes their grants/ownership before the cluster role is dropped.
        var adminConnection = Environment.GetEnvironmentVariable("EXAM_TEST_POSTGRES");
        await _recovery.DisposeAsync();
        await _original.DisposeAsync();
        if (_roleCreated && adminConnection is not null)
        {
            await using var admin = NpgsqlDataSource.Create(adminConnection);
            await using var drop = admin.CreateCommand($"DROP ROLE {_role}");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task CompleteReaderWithoutRowPoliciesCanVerifyEqualState()
        => Assert.True((await CompareAsync()).Passed);

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RowFilteringOnEitherConnectionBlocksVerification(bool originalPolicy, bool recoveryPolicy)
    {
        if (originalPolicy) await EnablePolicyAsync(_original);
        if (recoveryPolicy) await EnablePolicyAsync(_recovery);
        Assert.False((await CompareAsync()).Passed);
    }

    [Fact]
    public async Task IdenticalVisibleRowsCannotHideDifferentPersistedRows()
    {
        await EnablePolicyAsync(_original);
        await EnablePolicyAsync(_recovery);
        await ExecuteAsync(_recovery, "UPDATE exam.\"synthetic\"\"visibility\" SET value='newer-confirmed-value' WHERE id=2");
        // Demonstrate the exact false-positive precondition: ordinary SELECT sees equal rows.
        foreach (var database in new[] { _original, _recovery })
        {
            await using var reader = NpgsqlDataSource.Create(AsReader(database));
            await using var visible = reader.CreateCommand("SELECT value FROM exam.\"synthetic\"\"visibility\"");
            Assert.Equal("visible", await visible.ExecuteScalarAsync());
            await using var count = reader.CreateCommand("SELECT count(*) FROM exam.\"synthetic\"\"visibility\"");
            Assert.Equal(1L, await count.ExecuteScalarAsync());
        }
        Assert.False((await CompareAsync()).Passed);
        Assert.False((await CompareAsync(asReader: false)).Passed);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task TableOwnerMustActuallyHaveUnfilteredAccess(bool force, bool expected)
    {
        foreach (var database in new[] { _original, _recovery })
        {
            await EnablePolicyAsync(database);
            await ExecuteAsync(database, $"ALTER TABLE exam.\"synthetic\"\"visibility\" OWNER TO {_role}");
            if (force) await ExecuteAsync(database, "ALTER TABLE exam.\"synthetic\"\"visibility\" FORCE ROW LEVEL SECURITY");
        }
        Assert.Equal(expected, (await CompareAsync()).Passed);
    }

    [Fact]
    public async Task MissingTableAccessBlocksVerification()
    {
        await ExecuteAsync(_recovery, $"REVOKE SELECT ON exam.\"synthetic\"\"visibility\" FROM {_role}");
        Assert.False((await CompareAsync()).Passed);
    }

    private string AsReader(PostgresDatabaseFixture database)
        => new NpgsqlConnectionStringBuilder(database.ConnectionString) { Options = "-c role=" + _role }.ConnectionString;

    private Task<StartupCheckResult> CompareAsync(bool asReader = true)
        => new PostgresRecoveryStateComparison(asReader ? AsReader(_original) : _original.ConnectionString,
            asReader ? AsReader(_recovery) : _recovery.ConnectionString, 1).RunAsync(Configuration, default);

    private Task EnablePolicyAsync(PostgresDatabaseFixture database)
        => ExecuteAsync(database, """
            ALTER TABLE exam."synthetic""visibility" ENABLE ROW LEVEL SECURITY;
            CREATE POLICY visible_only ON exam."synthetic""visibility" FOR SELECT USING (id=1);
            """);

    private static async Task ExecuteAsync(PostgresDatabaseFixture database, string sql)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
}
