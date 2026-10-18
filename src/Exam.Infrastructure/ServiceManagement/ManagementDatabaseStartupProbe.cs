using Exam.Application.ServiceManagement;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.Postgres;
using Npgsql;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>Read-only preflight; missing configuration/database never prevents the management host loading.</summary>
public sealed class ManagementDatabaseStartupProbe(string? connectionString, long expectedEpoch,
    bool inspectStandby = false) : IStartupReadinessProbe
{
    public async Task<IReadOnlyList<StartupCheckResult>> RunAsync(ServiceConfiguration configuration, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString) || expectedEpoch < 1)
            return [new("database", false, "业务数据库连接或预期服务代次未配置。", true)];
        try
        {
            await using var source = ManagementProbeConnection.Create(connectionString, 5);
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            var results = new List<StartupCheckResult> { new("database", true, "业务数据库连接可用。", true) };
            var applied = new HashSet<string>(StringComparer.Ordinal);
            await using (var command = new NpgsqlCommand("SELECT migration_name FROM exam.schema_migrations", connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken)) applied.Add(reader.GetString(0));
            var complete = new PostgresMigrationRunner(source).GetRequiredMigrationNames().All(applied.Contains);
            results.Add(new("database-migrations", complete, complete ? "所需迁移均已应用。" : "业务数据库缺少迁移，管理入口不会自动迁移。", true));
            bool inRecovery, readOnly;
            await using (var command = new NpgsqlCommand("SELECT pg_is_in_recovery(), current_setting('transaction_read_only') = 'on'", connection))
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken)) throw new IOException("数据库角色未返回。");
                inRecovery = reader.GetBoolean(0); readOnly = reader.GetBoolean(1);
            }
            await using (var command = new NpgsqlCommand("""
                SELECT s.active_node_id, s.server_epoch, n.node_role
                FROM exam.server_state s LEFT JOIN exam.server_nodes n
                  ON n.server_id=s.server_id AND n.node_id=$2
                WHERE s.server_id=$1
                """, connection))
            {
                command.Parameters.AddWithValue(configuration.ServerId);
                command.Parameters.AddWithValue(configuration.NodeId);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                var found = await reader.ReadAsync(cancellationToken);
                var expectedStandby = inspectStandby && inRecovery && readOnly && found &&
                    reader.GetString(0) != configuration.NodeId && reader.GetInt64(1) == expectedEpoch &&
                    !reader.IsDBNull(2) && reader.GetString(2) == "Standby";
                var writable = !inRecovery && !readOnly;
                results.Add(new("database-role", writable || expectedStandby,
                    expectedStandby ? "备用节点只读（预期），业务写入保持关闭。" :
                    writable ? "数据库可写角色已核对。" : "数据库为备库或只读状态，禁止启动业务写入。", !expectedStandby));
                var matched = found && reader.GetInt64(1) == expectedEpoch &&
                    (reader.GetString(0) == configuration.NodeId || expectedStandby);
                results.Add(new("database-node-epoch", matched,
                    expectedStandby ? "备用节点身份与预期代次一致。" :
                    matched ? "活动节点与预期代次一致。" : "权威节点或代次缺失或不匹配。", !expectedStandby));
            }
            return results;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // Neither connection material nor provider exception text belongs in a management response.
            return [new("database", false, "无法完成数据库、迁移及角色代次核对，请检查受保护部署配置。", true)];
        }
    }
}
