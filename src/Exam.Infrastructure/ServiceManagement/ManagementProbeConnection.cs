using Exam.Infrastructure.Postgres;
using Npgsql;

namespace Exam.Infrastructure.ServiceManagement;

internal static class ManagementProbeConnection
{
    public static NpgsqlDataSource Create(string connectionString, int commandTimeoutSeconds)
    {
        // A diagnostic pool is independent of the business pool budget in the protected connection settings.
        var connection = new NpgsqlConnectionStringBuilder(connectionString)
        { Pooling = true, MinPoolSize = 0, MaxPoolSize = 2 };
        // TLS establishment over a deployed remote link can exceed three seconds. Honor the
        // configured connection budget while keeping an unavailable endpoint bounded.
        var connectionTimeout = Math.Clamp(connection.Timeout, 3, 15);
        return new PostgresConnectionOptions(connection.ConnectionString, commandTimeoutSeconds,
            ApplicationName: "Exam.Management.Preflight", MaximumPoolSize: 2, MinimumPoolSize: 0, ConnectionTimeoutSeconds: connectionTimeout)
            .CreateDataSource();
    }
}
