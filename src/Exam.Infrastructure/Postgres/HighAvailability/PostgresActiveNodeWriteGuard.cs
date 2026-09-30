using Exam.Contracts.Commands;
using Npgsql;

namespace Exam.Infrastructure.Postgres.HighAvailability;

/// <summary>
/// 在实际业务写连接上校验并锁定权威主备状态。调用方必须在返回的同一事务中写入并提交，
/// 不能另开连接代写；连接终止时状态锁与业务修改一起回滚。
/// 此边界只串行化同一数据库内的状态变化，不能替代接管时对旧主机的隔离。
/// </summary>
public sealed class PostgresActiveNodeWriteGuard(ServerNodeEnvelope identity, bool allowUninitializedState = false)
{
    public async ValueTask<NpgsqlTransaction> BeginAsync(NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var writable = new NpgsqlCommand(
                             "SELECT NOT pg_is_in_recovery() AND current_setting('transaction_read_only') = 'off';",
                             connection, transaction))
            {
                if ((bool)(await writable.ExecuteScalarAsync(cancellationToken) ?? false) is false)
                    throw new InvalidOperationException("当前 PostgreSQL 处于备库或只读状态，禁止计分写入。");
            }

            await using var command = new NpgsqlCommand("""
                SELECT active_node_id, server_epoch
                  FROM exam.server_state
                 WHERE server_id = @server_id
                   FOR SHARE;
                """, connection, transaction);
            command.Parameters.AddWithValue("server_id", identity.ServerId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                if (!allowUninitializedState)
                    throw new InvalidOperationException("未建立权威主备状态，禁止计分写入。");
            }
            else if (!string.Equals(reader.GetString(0), identity.NodeId, StringComparison.Ordinal) ||
                     reader.GetInt64(1) != identity.ServerEpoch)
            {
                throw new InvalidOperationException("当前节点或服务代次已失效，禁止计分写入。");
            }
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }
}
