using Npgsql;

namespace Exam.Infrastructure.Postgres;

public sealed record PostgresConnectionOptions(
    string ConnectionString,
    int CommandTimeoutSeconds = 30,
    string ApplicationName = "Huaizhou.ExamSystem",
    int? MaximumPoolSize = null,
    int? MinimumPoolSize = null,
    int ConnectionTimeoutSeconds = 15)
{
    // Development/test fallback only. Production must explicitly budget its pool in Database configuration.
    public const int DevelopmentMaximumPoolSize = 32;
    public const string MetricsPoolName = "Exam.BusinessDatabase";

    public NpgsqlConnectionStringBuilder BuildConnectionSettings()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString))
        {
            throw new ArgumentException("PostgreSQL 连接字符串不能为空。", nameof(ConnectionString));
        }

        if (CommandTimeoutSeconds is < 1 or > 600)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CommandTimeoutSeconds),
                "数据库命令超时必须在 1 至 600 秒之间。");
        }

        if (ConnectionTimeoutSeconds is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(ConnectionTimeoutSeconds), "连接及连接池等待超时必须在 1 至 120 秒之间。");

        var connection = new NpgsqlConnectionStringBuilder(ConnectionString);
        if (!connection.Pooling)
            throw new ArgumentException("业务数据库必须启用有界连接池。", nameof(ConnectionString));
        var maximum = MaximumPoolSize ?? (connection.ShouldSerialize("Maximum Pool Size")
            ? connection.MaxPoolSize : DevelopmentMaximumPoolSize);
        var minimum = MinimumPoolSize ?? connection.MinPoolSize;
        if (maximum is < 1 or > 1024 || minimum < 0 || minimum > maximum)
            throw new ArgumentOutOfRangeException(nameof(MaximumPoolSize), "连接池上限须为 1 至 1024，最小值须在 0 与上限之间。");
        if (MaximumPoolSize.HasValue && connection.ShouldSerialize("Maximum Pool Size") && connection.MaxPoolSize != maximum)
            throw new ArgumentException("连接字符串中的连接池上限与 Database:MaximumPoolSize 不一致。", nameof(ConnectionString));
        if (MinimumPoolSize.HasValue && connection.ShouldSerialize("Minimum Pool Size") && connection.MinPoolSize != minimum)
            throw new ArgumentException("连接字符串中的连接池下限与 Database:MinimumPoolSize 不一致。", nameof(ConnectionString));

        connection.ApplicationName = ApplicationName;
        connection.CommandTimeout = CommandTimeoutSeconds;
        connection.Timeout = ConnectionTimeoutSeconds;
        connection.MaxPoolSize = maximum;
        connection.MinPoolSize = minimum;
        connection.IncludeErrorDetail = false;
        connection.LogParameters = false;
        return connection;
    }

    public NpgsqlDataSource CreateDataSource()
    {
        var connection = BuildConnectionSettings();
        var builder = new NpgsqlDataSourceBuilder(connection.ConnectionString)
        {
            // Npgsql otherwise uses the connection string as a metric label. Never export credentials there.
            Name = MetricsPoolName
        };
        return builder.Build();
    }
}
