using Exam.Infrastructure.Postgres;
using Npgsql;

namespace Exam.Infrastructure.ServiceManagement;

internal static class ManagementProbeConnection
{
    internal static int ConnectionTimeout(NpgsqlConnectionStringBuilder connection) =>
        connection.Host is "127.0.0.1" or "::1" or "localhost"
            ? Math.Clamp(connection.Timeout, 3, 15) : Math.Clamp(connection.Timeout, 30, 45);
    public static NpgsqlDataSource Create(string connectionString, int commandTimeoutSeconds)
    {
        // A diagnostic pool is independent of the business pool budget in the protected connection settings.
        var connection = new NpgsqlConnectionStringBuilder(connectionString)
        { Pooling = true, MinPoolSize = 0, MaxPoolSize = 2 };
        // TLS establishment over a deployed remote link can exceed three seconds. Honor the
        // configured connection budget while keeping an unavailable endpoint bounded.
        var connectionTimeout = ConnectionTimeout(connection);
        return new PostgresConnectionOptions(connection.ConnectionString, commandTimeoutSeconds,
            ApplicationName: "Exam.Management.Preflight", MaximumPoolSize: 2, MinimumPoolSize: 0, ConnectionTimeoutSeconds: connectionTimeout)
            .CreateDataSource();
    }
}
