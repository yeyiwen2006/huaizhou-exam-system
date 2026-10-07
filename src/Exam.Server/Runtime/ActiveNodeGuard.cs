using Exam.Contracts.Common;
using Npgsql;

namespace Exam.Server.Runtime;

public sealed record ActiveNodeCheck(bool Allowed, string ErrorCode, string? Reason);

public sealed class ActiveNodeGuard(
    NpgsqlDataSource dataSource,
    ServerIdentityOptions identity,
    bool allowUninitializedState)
{
    public async Task<ActiveNodeCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using (var writable = new NpgsqlCommand(
                             "SELECT NOT pg_is_in_recovery() AND current_setting('transaction_read_only') = 'off';",
                             connection))
            {
                if ((bool)(await writable.ExecuteScalarAsync(cancellationToken) ?? false) is false)
                {
                    return new(false, ErrorCodes.NotActiveServer, "当前 PostgreSQL 处于备库或只读状态。");
                }
            }

            await using var command = new NpgsqlCommand(
                """
                SELECT active_node_id, server_epoch
                  FROM exam.server_state
                 WHERE server_id = @server_id;
                """,
                connection);
            command.Parameters.AddWithValue("server_id", identity.ServerId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return allowUninitializedState
                    ? new(true, string.Empty, null)
                    : new(false, ErrorCodes.NotActiveServer, "未建立该逻辑服务器的权威主备状态。");
            }

            var activeNodeId = reader.GetString(0);
            var serverEpoch = reader.GetInt64(1);
            if (!string.Equals(activeNodeId, identity.NodeId, StringComparison.Ordinal))
            {
                return new(false, ErrorCodes.NotActiveServer, "当前节点不是权威活动节点。");
            }

            if (serverEpoch != identity.ServerEpoch)
            {
                return new(false, ErrorCodes.StaleServerEpoch, "当前进程的服务代次与权威状态不一致。");
            }

            return new(true, string.Empty, null);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new(false, ErrorCodes.ServiceRecovering, "主备状态表尚未迁移。");
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return new(false, ErrorCodes.ServiceRecovering, "服务器存储暂不可用，请稍后重试并联系考务人员。");
        }
    }
}
