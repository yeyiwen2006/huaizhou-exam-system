using System.Security.Cryptography;
using Exam.Contracts.HighAvailability;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;
using Npgsql;

namespace Exam.Infrastructure.HighAvailability;

/// <summary>Consumes both durable heads. It never infers latest acknowledgement from a backup or
/// from a historical standby head alone. An unreadable primary DATABASE is allowed; its independent
/// local confirmation journal and the actual standby remain required.</summary>
public sealed class PostgresConfirmedRecoveryEvidence(FileConfirmationStore primary, IDurableConfirmationPeer peer,
    string standbyConnection, string recoveryConnection, long epoch, string? recoveryTimelineConnection = null,
    string? postgresBinDirectory = null, string? standbyTimelineConnection = null)
{
    public async Task<StartupCheckResult> RunAsync(ServiceConfiguration configuration, string recoveredResources,
        CancellationToken token)
    {
        try
        {
            using var locked = primary.Lock();
            var head = primary.Read();
            if (head is not { Confirmed: true } || head.Record.Mode != ProtectionMode.SynchronousDualNode ||
                head.Record.ServerId != configuration.ServerId || head.Record.PrimaryNodeId != configuration.NodeId ||
                head.Record.Epoch != epoch || !configuration.HighAvailabilityEnabled)
                throw new IOException("最新本机确认记录缺失、待确认或处于单机应急。");
            var independent = await peer.ReadAsync(token);
            if (independent is not { Confirmed: true } || independent.Record != head.Record)
                throw new IOException("备节点的独立持久确认未覆盖本机最新确认。");
            await using var standby = ManagementProbeConnection.Create(standbyConnection, 30);
            await using var recovery = ManagementProbeConnection.Create(recoveryConnection, 30);
            await PostgresStandbyConfirmation.VerifyPositionAsync(standby, head.Record, head.Record.StandbyNodeId, token,
                standbyTimelineConnection ?? standbyConnection, postgresBinDirectory);
            int timeline;
            await using (var connection = await recovery.OpenConnectionAsync(token))
            await using (var command = new NpgsqlCommand("""
                SELECT system_identifier::text,timeline_id,current_database(),pg_is_in_recovery(),current_setting('transaction_read_only'),
                       pg_current_wal_flush_lsn()::text
                  FROM pg_control_system() CROSS JOIN pg_control_checkpoint()
                """, connection))
            await using (var row = await command.ExecuteReaderAsync(token))
            {
                if (!await row.ReadAsync(token) || row.GetString(0) != head.Record.SystemIdentifier ||
                    row.GetString(2) != head.Record.DatabaseName || row.GetBoolean(3) || row.GetString(4) != "off" ||
                    FileConfirmationStore.Lsn(row.GetString(5)) < FileConfirmationStore.Lsn(head.Record.FlushLsn))
                    throw new IOException("恢复数据库不属于确认记录的实际集群或时间线。");
                timeline = row.GetInt32(1);
            }
            if (timeline != head.Record.Timeline)
                _ = await VerifiedTimelineContinuation.ReadAsync(recovery, head.Record, timeline, recoveryTimelineConnection, postgresBinDirectory, token);
            var comparison = new PostgresRecoveryStateComparison(standbyConnection, recoveryConnection, epoch);
            var before = await comparison.SnapshotAsync(standby, configuration, token);
            var after = await comparison.SnapshotAsync(recovery, configuration, token);
            if (!CryptographicOperations.FixedTimeEquals(before, after) ||
                FileConfirmationStore.ResourcesDigest(recoveredResources) != head.Record.ResourcesDigest ||
                await peer.ReadAsync(token) != independent || primary.Read() != head)
                throw new IOException("恢复状态、资源或最新确认记录不匹配。");
            await PostgresStandbyConfirmation.VerifyPositionAsync(standby, head.Record, head.Record.StandbyNodeId, token,
                standbyTimelineConnection ?? standbyConnection, postgresBinDirectory);
            return new("recovery-confirmed-state", true,
                "原库不可读时，独立备节点持久确认、实际数据库及恢复副本完整状态已核实。", true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new("recovery-confirmed-state", false,
                "无法核实双侧最新持久确认及实际恢复状态，保持待核对并禁止开放业务。", true);
        }
    }
}
