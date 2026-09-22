using System.Data;
using Exam.Contracts.HighAvailability;
using Npgsql;

namespace Exam.Infrastructure.Postgres.HighAvailability;

public sealed class PostgresFailoverStateStore(NpgsqlDataSource dataSource)
{
    public async Task SaveAsync(
        ServerDeploymentState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string stateSql = """
                INSERT INTO exam.server_state
                    (server_id, active_node_id, server_epoch, protection_mode, state_version, updated_at)
                VALUES ($1, $2, $3, $4, $5, clock_timestamp())
                ON CONFLICT (server_id) DO UPDATE SET
                    active_node_id = EXCLUDED.active_node_id,
                    server_epoch = EXCLUDED.server_epoch,
                    protection_mode = EXCLUDED.protection_mode,
                    state_version = EXCLUDED.state_version,
                    updated_at = EXCLUDED.updated_at
                WHERE exam.server_state.state_version = EXCLUDED.state_version - 1
                  AND exam.server_state.server_epoch <= EXCLUDED.server_epoch;
                """;
            await using (var command = new NpgsqlCommand(stateSql, connection, transaction))
            {
                command.Parameters.AddWithValue(state.ServerId);
                command.Parameters.AddWithValue(state.ActiveNodeId);
                command.Parameters.AddWithValue(state.ServerEpoch);
                command.Parameters.AddWithValue(state.ProtectionMode.ToString());
                command.Parameters.AddWithValue(state.Version);
                var affected = await command.ExecuteNonQueryAsync(cancellationToken);
                if (affected == 0)
                {
                    throw new DBConcurrencyException("主备状态必须以精确前一版本更新，且服务代次不得回退。");
                }
            }

            const string nodeSql = """
                INSERT INTO exam.server_nodes
                    (server_id, node_id, node_role, reachable, database_in_recovery,
                     replay_lag_bytes, key_material_ready, certificate_ready, observed_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                ON CONFLICT (server_id, node_id) DO UPDATE SET
                    node_role = EXCLUDED.node_role,
                    reachable = EXCLUDED.reachable,
                    database_in_recovery = EXCLUDED.database_in_recovery,
                    replay_lag_bytes = EXCLUDED.replay_lag_bytes,
                    key_material_ready = EXCLUDED.key_material_ready,
                    certificate_ready = EXCLUDED.certificate_ready,
                    observed_at = EXCLUDED.observed_at;
                """;
            foreach (var node in state.Nodes.Values)
            {
                await using var command = new NpgsqlCommand(nodeSql, connection, transaction);
                command.Parameters.AddWithValue(state.ServerId);
                command.Parameters.AddWithValue(node.NodeId);
                command.Parameters.AddWithValue(node.Role.ToString());
                command.Parameters.AddWithValue(node.Reachable);
                command.Parameters.AddWithValue(node.DatabaseInRecovery);
                command.Parameters.AddWithValue(node.ReplayLagBytes);
                command.Parameters.AddWithValue(node.KeyMaterialReady);
                command.Parameters.AddWithValue(node.CertificateReady);
                command.Parameters.AddWithValue(node.ObservedAt);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            const string auditSql = """
                INSERT INTO exam.failover_audit
                    (server_id, action, actor_id, reason, node_id, server_epoch, occurred_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                ON CONFLICT DO NOTHING;
                """;
            foreach (var audit in state.Audit)
            {
                await using var command = new NpgsqlCommand(auditSql, connection, transaction);
                command.Parameters.AddWithValue(state.ServerId);
                command.Parameters.AddWithValue(audit.Action);
                command.Parameters.AddWithValue(audit.ActorId);
                command.Parameters.AddWithValue(audit.Reason);
                command.Parameters.AddWithValue(audit.NodeId);
                command.Parameters.AddWithValue(audit.ServerEpoch);
                command.Parameters.AddWithValue(audit.OccurredAt);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ServerDeploymentState?> LoadAsync(
        string serverId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        const string stateSql = """
            SELECT active_node_id, server_epoch, protection_mode, state_version
            FROM exam.server_state
            WHERE server_id = $1;
            """;
        string activeNodeId;
        long epoch;
        ProtectionMode protection;
        long version;
        await using (var command = new NpgsqlCommand(stateSql, connection))
        {
            command.Parameters.AddWithValue(serverId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            activeNodeId = reader.GetString(0);
            epoch = reader.GetInt64(1);
            protection = Enum.Parse<ProtectionMode>(reader.GetString(2), ignoreCase: false);
            version = reader.GetInt64(3);
        }

        var nodes = new Dictionary<string, NodeState>(StringComparer.Ordinal);
        const string nodesSql = """
            SELECT node_id, node_role, reachable, database_in_recovery, replay_lag_bytes,
                   key_material_ready, certificate_ready, observed_at
            FROM exam.server_nodes
            WHERE server_id = $1;
            """;
        await using (var command = new NpgsqlCommand(nodesSql, connection))
        {
            command.Parameters.AddWithValue(serverId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var node = new NodeState(
                    reader.GetString(0),
                    Enum.Parse<DeploymentNodeRole>(reader.GetString(1), ignoreCase: false),
                    reader.GetBoolean(2),
                    reader.GetBoolean(3),
                    reader.GetInt64(4),
                    reader.GetBoolean(5),
                    reader.GetBoolean(6),
                    new DateTimeOffset(reader.GetDateTime(7), TimeSpan.Zero));
                nodes.Add(node.NodeId, node);
            }
        }

        var audits = new List<FailoverAuditEntry>();
        const string auditSql = """
            SELECT action, actor_id, reason, node_id, server_epoch, occurred_at
            FROM exam.failover_audit
            WHERE server_id = $1
            ORDER BY occurred_at, audit_id;
            """;
        await using (var command = new NpgsqlCommand(auditSql, connection))
        {
            command.Parameters.AddWithValue(serverId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                audits.Add(new FailoverAuditEntry(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero)));
            }
        }

        return new ServerDeploymentState(
            serverId,
            activeNodeId,
            epoch,
            protection,
            nodes,
            version,
            audits.AsReadOnly());
    }
}
