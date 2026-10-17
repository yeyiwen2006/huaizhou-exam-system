using System.Data;
using Exam.Contracts.HighAvailability;
using Npgsql;

namespace Exam.Infrastructure.Postgres.HighAvailability;

public sealed class PostgresFailoverStateStore(NpgsqlDataSource dataSource)
{
    /// <summary>Explicit first-install bootstrap only. Never changes an existing deployment or promotes a replica.</summary>
    public async Task<bool> BootstrapPrimaryAsync(string serverId, string nodeId, long epoch,
        IReadOnlyList<string> nodeIds, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(nodeId) ||
            nodeIds.Count is < 1 or > 2 || nodeIds.Any(string.IsNullOrWhiteSpace) ||
            nodeIds.Distinct(StringComparer.Ordinal).Count() != nodeIds.Count || !nodeIds.Contains(nodeId))
            throw new ArgumentException("初始节点配置无效。");
        await using var connection = await dataSource.OpenConnectionAsync(token);
        await using var transaction = await connection.BeginTransactionAsync(token);
        await using (var settings = new NpgsqlCommand("SET LOCAL synchronous_commit=local; SELECT pg_advisory_xact_lock(7427101701);", connection, transaction))
            await settings.ExecuteNonQueryAsync(token);
        await using (var existing = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM exam.server_state WHERE server_id=$1)", connection, transaction))
        {
            existing.Parameters.AddWithValue(serverId);
            if ((bool)(await existing.ExecuteScalarAsync(token) ?? false)) return false;
        }
        await using (var safe = new NpgsqlCommand("""
            SELECT NOT pg_is_in_recovery() AND current_setting('transaction_read_only')='off'
              AND current_setting('fsync')='on' AND current_setting('full_page_writes')='on'
              AND NOT EXISTS(SELECT 1 FROM exam.server_state)
              AND NOT EXISTS(SELECT 1 FROM exam.exam_catalog)
              AND NOT EXISTS(SELECT 1 FROM exam.operator_accounts)
              AND NOT EXISTS(SELECT 1 FROM exam.activation_devices)
              AND NOT EXISTS(SELECT 1 FROM exam.final_submissions)
            """, connection, transaction))
        {
            if (epoch != 1 || (bool)(await safe.ExecuteScalarAsync(token) ?? false) is false)
                throw new IOException("仅允许在空的可写数据库上建立第一代初始主节点；已有考试或备库须走恢复/接管流程。");
        }
        await using (var state = new NpgsqlCommand("""
            INSERT INTO exam.server_state(server_id,active_node_id,server_epoch,protection_mode,state_version,updated_at)
            VALUES($1,$2,1,'EmergencySingleNode',1,clock_timestamp())
            """, connection, transaction))
        {
            state.Parameters.AddWithValue(serverId); state.Parameters.AddWithValue(nodeId);
            await state.ExecuteNonQueryAsync(token);
        }
        foreach (var id in nodeIds)
        {
            await using var node = new NpgsqlCommand("""
                INSERT INTO exam.server_nodes(server_id,node_id,node_role,reachable,database_in_recovery,
                    replay_lag_bytes,key_material_ready,certificate_ready,observed_at)
                VALUES($1,$2,$3,$4,$5,0,false,false,clock_timestamp())
                """, connection, transaction);
            node.Parameters.AddWithValue(serverId); node.Parameters.AddWithValue(id);
            node.Parameters.AddWithValue(id == nodeId ? "Primary" : "Standby");
            node.Parameters.AddWithValue(id == nodeId); node.Parameters.AddWithValue(id != nodeId);
            await node.ExecuteNonQueryAsync(token);
        }
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO exam.failover_audit(server_id,action,actor_id,reason,node_id,server_epoch,occurred_at)
            VALUES($1,'BootstrapPrimary','deployment','首次部署显式建立初始主节点',$2,1,clock_timestamp())
            """, connection, transaction))
        {
            audit.Parameters.AddWithValue(serverId); audit.Parameters.AddWithValue(nodeId);
            await audit.ExecuteNonQueryAsync(token);
        }
        await transaction.CommitAsync(token);
        return true;
    }

    public async Task SaveAsync(
        ServerDeploymentState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // Control-plane transitions must flush locally when the isolated synchronous peer is absent.
            // Business success retains the independent confirmation gate; this does not authorize its writes.
            await using (var durability = new NpgsqlCommand("SET LOCAL synchronous_commit=local", connection, transaction))
                await durability.ExecuteNonQueryAsync(cancellationToken);
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
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        const string stateSql = """
            SELECT active_node_id, server_epoch, protection_mode, state_version
            FROM exam.server_state
            WHERE server_id = $1;
            """;
        string activeNodeId;
        long epoch;
        ProtectionMode protection;
        long version;
        await using (var command = new NpgsqlCommand(stateSql, connection, transaction))
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
        await using (var command = new NpgsqlCommand(nodesSql, connection, transaction))
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
        await using (var command = new NpgsqlCommand(auditSql, connection, transaction))
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

        await transaction.CommitAsync(cancellationToken);
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
