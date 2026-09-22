using Npgsql;

namespace Exam.Server.Capacity;

public sealed class DatabaseCapacityProbe(NpgsqlDataSource dataSource, DatabaseCapacityOptions options)
{
    public async Task<string?> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                SELECT current_setting('max_connections')::integer,
                       current_setting('superuser_reserved_connections')::integer
                       + COALESCE(NULLIF(current_setting('reserved_connections', true), ''), '0')::integer;
                """, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return "无法读取 PostgreSQL 连接容量。";
            return options.ValidateActual(reader.GetInt32(0), reader.GetInt32(1));
        }
        catch (Exception error) when (error is NpgsqlException or TimeoutException)
        {
            // Connection exceptions can contain host/user configuration; return only an operational category.
            return "PostgreSQL 连接容量自检未完成，数据库不可用或连接等待超时。";
        }
    }
}

public sealed class DatabaseCapacityStartupCheck(DatabaseCapacityProbe probe, DatabaseCapacityOptions options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.RequireActualDatabaseMatch && await probe.CheckAsync(cancellationToken) is { } reason)
            throw new InvalidOperationException(reason);
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
