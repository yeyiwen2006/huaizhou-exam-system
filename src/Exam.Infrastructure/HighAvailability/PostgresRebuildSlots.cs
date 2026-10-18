using Exam.Contracts.HighAvailability;
using Exam.Infrastructure.ServiceManagement;
using Npgsql;

namespace Exam.Infrastructure.HighAvailability;

public sealed partial class PostgresHighAvailabilityManagementPlatform
{
    private sealed record SlotOwner(string ServerId, string NodeId, string SourceNodeId, string SystemIdentifier,
        string DatabaseName, string RequestId, string ContentHash, string SlotName, bool CreationClaimed, bool Retired = false);
    private static string OwnerPath(string slot) => Path.Combine(slot, "replication-slot.json");

    private async Task<string> CreateOwnedRebuildSlotAsync(FailoverExecutionPlan plan, CancellationToken token)
    {
        var slot = RebuildSlot(plan);
        var name = StagedPostgresNodeRebuilder.SlotName(Local.NodeId, Path.Combine(slot, Local.NodeId));
        var owner = new SlotOwner(_options.ServerId, Local.NodeId, plan.After.ActiveNodeId, plan.SystemIdentifier,
            plan.DatabaseName, plan.RequestId, plan.ContentHash, name, true);
        var source = StateSource(_options.Nodes[owner.SourceNodeId]);
        await using var exists = source.CreateCommand("SELECT EXISTS(SELECT 1 FROM pg_replication_slots WHERE slot_name=$1)");
        exists.Parameters.AddWithValue(name);
        if (await exists.ExecuteScalarAsync(token) is true) throw new IOException("重建槽已存在且不属于本次新建流程，禁止复用。");
        // Claim a previously absent, request-specific name before the external effect.
        // If the process dies after PostgreSQL creates it, the durable owner can still
        // retire that inactive slot; a crash before creation simply finds no slot.
        await DurableJsonFile.WriteAsync(OwnerPath(slot), owner, token);
        await using var create = source.CreateCommand("SELECT slot_name FROM pg_create_physical_replication_slot($1, true)");
        create.Parameters.AddWithValue(name);
        if ((string?)await create.ExecuteScalarAsync(token) != name) throw new IOException("重建复制槽未创建。");
        return name;
    }

    private async Task RetireSupersededSlotsAsync(FailoverExecutionPlan plan, CancellationToken token)
    {
        try { await RetireSupersededSlotsCoreAsync(plan, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is NpgsqlException or IOException or TimeoutException)
        {
            // Cleanup cannot turn an already installed, verified standby into a failed
            // rebuild. Retain every unproven slot and report cleanup for later retry.
            try { _recordProbeFailure?.Invoke("Exam.Management.Rebuild.SlotCleanupDeferred", error); }
            catch { /* Diagnostic storage cannot invalidate the verified rebuild either. */ }
        }
    }

    private async Task RetireSupersededSlotsCoreAsync(FailoverExecutionPlan plan, CancellationToken token)
    {
        var currentOwner = DurableJsonFile.Read<SlotOwner>(OwnerPath(RebuildSlot(plan)), 8192);
        if (currentOwner is null) return; // Legacy slots have no ownership evidence and are never guessed disposable.
        var source = StateSource(_options.Nodes[plan.After.ActiveNodeId]);
        var local = StateSource(Local);
        await using var receiver = local.CreateCommand("SELECT slot_name FROM pg_stat_wal_receiver WHERE status='streaming'");
        if ((string?)await receiver.ExecuteScalarAsync(token) != currentOwner.SlotName) return;
        await using var sender = source.CreateCommand("""
            SELECT EXISTS(SELECT 1 FROM pg_replication_slots s JOIN pg_stat_replication r ON r.pid=s.active_pid
                WHERE s.slot_name=$1 AND s.slot_type='physical' AND r.application_name=$2 AND r.state='streaming')
            """);
        sender.Parameters.AddWithValue(currentOwner.SlotName); sender.Parameters.AddWithValue(Local.NodeId);
        if (await sender.ExecuteScalarAsync(token) is not true) return;
        foreach (var directory in Directory.EnumerateDirectories(_options.RebuildRoot))
        {
            var owner = DurableJsonFile.Read<SlotOwner>(OwnerPath(directory), 8192);
            if (owner is null || owner.Retired || owner.SlotName == currentOwner.SlotName || !OwnedByPlan(owner, plan, directory)) continue;
            var progress = DurableJsonFile.Read<RebuildProgress>(Path.Combine(directory, "rebuild-progress.json"));
            if (progress is null || progress.Phase is not ("Completed" or "Aborted") ||
                progress.Plan.ContentHash != owner.ContentHash || progress.Plan.RequestId != owner.RequestId ||
                progress.Plan.SystemIdentifier != owner.SystemIdentifier ||
                ExternalPreparationProcesses.IsRunning(_options.PostgresBinDirectory, directory)) continue;
            await DropOwnedSlotAsync(owner, directory, source, token);
        }
    }

    private bool OwnedByPlan(SlotOwner owner, FailoverExecutionPlan plan, string directory) =>
        owner.CreationClaimed && owner.ServerId == _options.ServerId && owner.NodeId == Local.NodeId &&
        owner.SourceNodeId == plan.After.ActiveNodeId && owner.SystemIdentifier == plan.SystemIdentifier && owner.DatabaseName == plan.DatabaseName &&
        Path.GetFileName(directory) == owner.ContentHash && owner.SlotName == StagedPostgresNodeRebuilder.SlotName(Local.NodeId, Path.Combine(directory, Local.NodeId));

    private static async Task DropOwnedSlotAsync(SlotOwner owner, string directory, NpgsqlDataSource source, CancellationToken token)
    {
        await using var identity = source.CreateCommand("SELECT system_identifier::text FROM pg_control_system()");
        if ((string?)await identity.ExecuteScalarAsync(token) != owner.SystemIdentifier) throw new IOException("复制槽源集群发生变化，禁止清理。");
        await using var active = source.CreateCommand("SELECT active FROM pg_replication_slots WHERE slot_name=$1 AND slot_type='physical'");
        active.Parameters.AddWithValue(owner.SlotName);
        var used = await active.ExecuteScalarAsync(token);
        if (used is true) return;
        if (used is false)
        {
            await using var drop = source.CreateCommand("SELECT pg_drop_replication_slot($1)");
            drop.Parameters.AddWithValue(owner.SlotName);
            // PostgreSQL rechecks activity atomically. A concurrent consumer makes this fail safely.
            await drop.ExecuteNonQueryAsync(token);
        }
        await DurableJsonFile.WriteAsync(OwnerPath(directory), owner with { Retired = true }, token);
    }
}
