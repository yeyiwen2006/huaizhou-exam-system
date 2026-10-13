using Exam.Infrastructure.Postgres;

namespace Exam.Server.Capacity;

/// <summary>A deployment budget, not a performance claim. Count every process that can target the same database.</summary>
public sealed record DatabaseCapacityOptions(
    int MaximumPoolSize,
    int PostgresMaxConnections,
    int PostgresReservedConnections,
    int MaximumServerProcesses,
    int OtherConnections,
    int OperationsReserveConnections,
    bool RequireActualDatabaseMatch)
{
    public long RequiredConnections => (long)MaximumPoolSize * MaximumServerProcesses +
        OtherConnections + OperationsReserveConnections + PostgresReservedConnections;

    public static DatabaseCapacityOptions Read(IConfiguration configuration, int effectivePoolMaximum, bool production)
    {
        int Read(string name, int fallback)
        {
            var key = "Database:Capacity:" + name;
            if (production && string.IsNullOrWhiteSpace(configuration[key]))
                throw new InvalidOperationException($"生产连接容量必须显式配置 {key}，不能沿用开发或 CI 的连接池设置。");
            return configuration.GetValue(key, fallback);
        }
        if (production && string.IsNullOrWhiteSpace(configuration["Database:MaximumPoolSize"]))
            throw new InvalidOperationException("生产环境必须显式配置 Database:MaximumPoolSize 并完成同库多进程连接预算。");
        var result = new DatabaseCapacityOptions(effectivePoolMaximum,
            Read(nameof(PostgresMaxConnections), 100), Read(nameof(PostgresReservedConnections), 3),
            Read(nameof(MaximumServerProcesses), 1), Read(nameof(OtherConnections), 0),
            Read(nameof(OperationsReserveConnections), 10), production);
        result.Validate();
        return result;
    }

    public void Validate()
    {
        if (MaximumPoolSize is < 1 or > 1024 || PostgresMaxConnections is < 10 or > 100000 ||
            PostgresReservedConnections < 0 || MaximumServerProcesses is < 1 or > 256 ||
            OtherConnections < 0 || OperationsReserveConnections < 3)
            throw new InvalidOperationException("数据库容量预算无效；必须使用正的进程/连接上限，并至少保留 3 个额外运维连接。");
        if (RequiredConnections > PostgresMaxConnections)
            throw new InvalidOperationException(
                $"数据库连接预算超限：连接池上限×同库进程数＋其他连接＋运维余量＋PostgreSQL保留连接 = {RequiredConnections}，超过配置上限 {PostgresMaxConnections}。");
    }

    public string? ValidateActual(int actualMaximum, int actualReserved)
    {
        if (actualMaximum < 1 || actualReserved < 0 || actualReserved >= actualMaximum)
            return "PostgreSQL 返回的连接容量参数无效。";
        if (RequireActualDatabaseMatch &&
            (actualMaximum != PostgresMaxConnections || actualReserved != PostgresReservedConnections))
            return "PostgreSQL 实际 max_connections 或保留连接数与部署预算不一致，请核对配置后重新状态检测。";
        var actualRequired = (long)MaximumPoolSize * MaximumServerProcesses + OtherConnections +
            OperationsReserveConnections + actualReserved;
        return actualRequired > actualMaximum
            ? "当前 PostgreSQL 连接上限不能容纳全部业务进程、其他连接和运维余量。"
            : null;
    }
}
