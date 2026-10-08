using System.Reflection;
using Npgsql;

namespace Exam.Infrastructure.Postgres;

public sealed class PostgresMigrationRunner(
    NpgsqlDataSource dataSource,
    Assembly? migrationAssembly = null)
{
    private const string ResourceMarker = ".Migrations.";
    private const long MigrationLockId = 7_243_123_901;
    private readonly Assembly _migrationAssembly = migrationAssembly ?? typeof(PostgresMigrationRunner).Assembly;

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureMigrationTableAsync(connection, cancellationToken);
        await AcquireLockAsync(connection, cancellationToken);
        try
        {
            var applied = new List<string>();
            foreach (var resourceName in FindMigrationResources())
            {
                var migrationName = GetMigrationName(resourceName);
                if (await IsAppliedAsync(connection, migrationName, cancellationToken))
                {
                    continue;
                }

                var sql = await ReadResourceAsync(resourceName, cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                try
                {
                    await EnsureMaintenanceWindowAsync(connection, transaction, cancellationToken);
                    await using (var migrationCommand = new NpgsqlCommand(sql, connection, transaction))
                    {
                        await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
                    }

                    const string recordSql =
                        "INSERT INTO exam.schema_migrations (migration_name, applied_at) VALUES ($1, clock_timestamp());";
                    await using (var recordCommand = new NpgsqlCommand(recordSql, connection, transaction))
                    {
                        recordCommand.Parameters.AddWithValue(migrationName);
                        await recordCommand.ExecuteNonQueryAsync(cancellationToken);
                    }

                    await transaction.CommitAsync(cancellationToken);
                    applied.Add(migrationName);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
            }

            return applied;
        }
        finally
        {
            await ReleaseLockAsync(connection);
        }
    }

    public IReadOnlyList<string> GetRequiredMigrationNames() =>
        FindMigrationResources().Select(GetMigrationName).ToArray();

    private IEnumerable<string> FindMigrationResources() =>
        _migrationAssembly.GetManifestResourceNames()
            .Where(name => name.Contains(ResourceMarker, StringComparison.Ordinal) &&
                           name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);

    private static string GetMigrationName(string resourceName)
    {
        var marker = resourceName.LastIndexOf(ResourceMarker, StringComparison.Ordinal);
        return resourceName[(marker + ResourceMarker.Length)..^4];
    }

    private async Task<string> ReadResourceAsync(string resourceName, CancellationToken cancellationToken)
    {
        await using var stream = _migrationAssembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"找不到数据库迁移资源：{resourceName}");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task EnsureMaintenanceWindowAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        // Fixed identifiers only. Locks last through this migration, so the state check cannot
        // race a new start, entry confirmation or answer-state write. A no-op restart does not lock.
        var checks = new (string Table, string Predicate)[]
        {
            ("exam.version_state", "lifecycle = 'Running' OR (lifecycle = 'Scheduled' AND starts_at <= clock_timestamp() AND ends_at > clock_timestamp())"),
            ("exam.exam_entry_access", "lifecycle = 'running' OR (lifecycle = 'scheduled' AND scheduled_start <= clock_timestamp() AND scheduled_end > clock_timestamp())"),
            ("exam.participations", "business_state IN ('waiting', 'answering')")
        };
        foreach (var (table, predicate) in checks)
        {
            await using var exists = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL;", connection, transaction);
            exists.Parameters.AddWithValue(table);
            if (await exists.ExecuteScalarAsync(cancellationToken) is not true) continue;
            await using var tableLock = new NpgsqlCommand($"LOCK TABLE {table} IN SHARE ROW EXCLUSIVE MODE;", connection, transaction);
            await tableLock.ExecuteNonQueryAsync(cancellationToken);
            await using var active = new NpgsqlCommand($"SELECT EXISTS (SELECT 1 FROM {table} WHERE {predicate});", connection, transaction);
            if (await active.ExecuteScalarAsync(cancellationToken) is true)
                throw new PostgresMigrationBlockedException();
        }
    }

    private static async Task EnsureMigrationTableAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE SCHEMA IF NOT EXISTS exam;
            CREATE TABLE IF NOT EXISTS exam.schema_migrations
            (
                migration_name text PRIMARY KEY,
                applied_at timestamptz NOT NULL
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> IsAppliedAsync(
        NpgsqlConnection connection,
        string migrationName,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT EXISTS (SELECT 1 FROM exam.schema_migrations WHERE migration_name = $1);";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(migrationName);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async Task AcquireLockAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_lock($1);", connection);
        command.Parameters.AddWithValue(MigrationLockId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReleaseLockAsync(NpgsqlConnection connection)
    {
        try
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock($1);", connection);
            command.Parameters.AddWithValue(MigrationLockId);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception) when (connection.FullState == System.Data.ConnectionState.Broken)
        {
            // PostgreSQL automatically releases advisory locks when the broken session ends.
        }
    }
}
