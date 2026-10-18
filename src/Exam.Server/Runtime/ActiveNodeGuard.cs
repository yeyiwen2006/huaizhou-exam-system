using Exam.Contracts.Common;
using Npgsql;

namespace Exam.Server.Runtime;

public sealed record ActiveNodeCheck(bool Allowed, string ErrorCode, string? Reason);

public sealed class ActiveNodeGuard(
    NpgsqlDataSource dataSource,
    ServerIdentityOptions identity,
    bool allowUninitializedState,
    NodeAuthorityGuard? authority = null)
{
    public async Task<ActiveNodeCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (authority is not null && !await authority.CheckAsync(cancellationToken))
                return new(false, ErrorCodes.NotActiveServer, "本机旧代次已隔离，须从当前活动服务器补齐后作为备机加入。");
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            // Writable role, active node and epoch belong to one request decision.
            // Read them together instead of making two database round trips.
            await using var command = new NpgsqlCommand(
                """
                SELECT NOT pg_is_in_recovery() AND current_setting('transaction_read_only') = 'off',
                       s.active_node_id, s.server_epoch
                  FROM (VALUES (1)) AS local_probe(value)
                  LEFT JOIN exam.server_state s ON s.server_id = @server_id;
                """,
                connection);
            command.Parameters.AddWithValue("server_id", identity.ServerId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || !reader.GetBoolean(0))
                return new(false, ErrorCodes.NotActiveServer, "当前 PostgreSQL 处于备库或只读状态。");
            if (reader.IsDBNull(1))
            {
                return allowUninitializedState
                    ? new(true, string.Empty, null)
                    : new(false, ErrorCodes.NotActiveServer, "未建立该逻辑服务器的权威主备状态。");
            }

            var activeNodeId = reader.GetString(1);
            var serverEpoch = reader.GetInt64(2);
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
