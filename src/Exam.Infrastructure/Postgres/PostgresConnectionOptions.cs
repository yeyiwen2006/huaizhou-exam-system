using Npgsql;

namespace Exam.Infrastructure.Postgres;

public sealed record PostgresConnectionOptions(
    string ConnectionString,
    int CommandTimeoutSeconds = 30,
    string ApplicationName = "Huaizhou.ExamSystem")
{
    public NpgsqlDataSource CreateDataSource()
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

        var connection = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            ApplicationName = ApplicationName,
            CommandTimeout = CommandTimeoutSeconds,
            IncludeErrorDetail = false,
            LogParameters = false
        };

        var builder = new NpgsqlDataSourceBuilder(connection.ConnectionString);
        return builder.Build();
    }
}
