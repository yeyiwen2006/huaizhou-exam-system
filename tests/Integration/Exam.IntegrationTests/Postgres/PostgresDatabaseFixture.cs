using Exam.Infrastructure.Postgres;
using Npgsql;

namespace Exam.IntegrationTests.Postgres;

public static class PostgresCollection
{
    public const string Name = "PostgreSQL integration";
}

[CollectionDefinition(PostgresCollection.Name, DisableParallelization = true)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresDatabaseFixture>;

public sealed class PostgresDatabaseFixture : IAsyncLifetime
{
    private string? _adminConnectionString;
    private string? _databaseName;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _adminConnectionString = Environment.GetEnvironmentVariable("EXAM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(_adminConnectionString))
        {
            throw new InvalidOperationException(
                "PostgreSQL 集成测试需要 EXAM_TEST_POSTGRES。普通测试请使用筛选 Category!=Postgres。");
        }

        var adminBuilder = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = "postgres",
            Pooling = false,
            IncludeErrorDetail = false,
            LogParameters = false
        };
        _adminConnectionString = adminBuilder.ConnectionString;
        _databaseName = $"exam_test_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(_adminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand(
                $"CREATE DATABASE \"{_databaseName}\" TEMPLATE template0 ENCODING 'UTF8';",
                admin);
            await create.ExecuteNonQueryAsync();
        }

        var testBuilder = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = _databaseName,
            Pooling = true
        };
        ConnectionString = testBuilder.ConnectionString;
        DataSource = new PostgresConnectionOptions(ConnectionString, ApplicationName: "Exam.IntegrationTests")
            .CreateDataSource();
        await new PostgresMigrationRunner(DataSource).MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }

        if (string.IsNullOrWhiteSpace(_adminConnectionString) ||
            string.IsNullOrWhiteSpace(_databaseName) ||
            !_databaseName.StartsWith("exam_test_", StringComparison.Ordinal))
        {
            return;
        }

        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using (var terminate = new NpgsqlCommand(
                         "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = $1 AND pid <> pg_backend_pid();",
                         admin))
        {
            terminate.Parameters.AddWithValue(_databaseName);
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_databaseName}\";", admin);
        await drop.ExecuteNonQueryAsync();
    }
}
