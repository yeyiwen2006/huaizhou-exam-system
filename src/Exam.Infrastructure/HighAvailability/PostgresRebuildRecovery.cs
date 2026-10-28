using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Infrastructure.HighAvailability;

public sealed partial class PostgresHighAvailabilityManagementPlatform
{
    private sealed record RebuildProgress(FailoverExecutionPlan Plan, string Phase, string? OldDatabaseIdentity,
        string? OldResourcesIdentity, string? DatabaseIdentity, string? ResourcesIdentity);
    private static string? DirectoryIdentity(string path) => Directory.Exists(path) ? ResourceDirectoryIdentity.Read(path) : null;
    private Task SaveRebuildProgressAsync(RebuildProgress progress, CancellationToken token) =>
        DurableJsonFile.WriteAsync(Path.Combine(RebuildSlot(progress.Plan), "rebuild-progress.json"), progress, token);
    private RebuildProgress? ReadRebuildProgress(FailoverExecutionPlan plan)
    {
        var progress = DurableJsonFile.Read<RebuildProgress>(Path.Combine(RebuildSlot(plan), "rebuild-progress.json"));
        if (progress is not null && !JsonSerializer.SerializeToUtf8Bytes(progress.Plan, JsonDefaults.Create()).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(plan, JsonDefaults.Create())))
            throw new IOException("重建阶段与原请求绑定不一致。");
        return progress;
    }

    public async Task<bool> VerifyFailedBeforeInstallationAsync(FailoverExecutionPlan plan, CancellationToken token)
    {
        if (plan.Kind != ManagementOperationKind.Rebuild) return false;
        ValidateRebuildPaths();
        var progress = ReadRebuildProgress(plan);
        if (progress is { Phase: not ("Preparing" or "Prepared" or "Aborted") }) return false;
        if (Directory.Exists(_options.PostgresDataDirectory + ".quarantine." + plan.ContentHash) ||
            Directory.Exists(Local.ResourcesDirectory + ".quarantine." + plan.ContentHash) ||
            ExternalPreparationProcesses.IsRunning(_options.PostgresBinDirectory, RebuildSlot(plan))) return false;
        if (progress is null)
        {
            // No working slot means preparation could not have launched. An unmarked
            // legacy working directory remains unverified; it is never guessed safe.
            if (Directory.Exists(RebuildSlot(plan))) return false;
            Directory.CreateDirectory(RebuildSlot(plan));
            progress = new(plan, "Preparing", DirectoryIdentity(_options.PostgresDataDirectory),
                DirectoryIdentity(Local.ResourcesDirectory), null, null);
        }
        if (DirectoryIdentity(_options.PostgresDataDirectory) != progress.OldDatabaseIdentity ||
            DirectoryIdentity(Local.ResourcesDirectory) != progress.OldResourcesIdentity) return false;
        // Unchanged original directories and no running preparer prove installation
        // never started. Closing this failed attempt neither changes authority nor
        // starts business, and must not depend on reaching the source database.
        await SaveRebuildProgressAsync(progress with { Phase = "Aborted" }, token);
        // Keep the owned slot and preparation for audit. A later successful rebuild
        // retires superseded slots after verifying its own receiver.
        return true;
    }

    private async Task InstallPreparedRebuildAsync(RebuildProgress progress, CancellationToken token)
    {
        Interlocked.Increment(ref _localDatabaseTransition);
        try { await InstallPreparedRebuildCoreAsync(progress, token); }
        finally { Interlocked.Decrement(ref _localDatabaseTransition); }
    }

    private async Task InstallPreparedRebuildCoreAsync(RebuildProgress progress, CancellationToken token)
    {
        var plan = progress.Plan;
        ValidateRebuildPaths();
        if (progress.Phase is not ("Prepared" or "Installing" or "Applied") || progress.DatabaseIdentity is null || progress.ResourcesIdentity is null)
            throw new IOException("重建缺少已核验暂存目录证据。");
        if (progress.Phase == "Prepared")
        {
            var source = await ReadAsync(_options.Nodes[plan.After.ActiveNodeId], token);
            if (source is null || source.Value.Recovery || source.Value.System != plan.SystemIdentifier ||
                source.Value.Database != plan.DatabaseName || !SameState(source.Value.State, plan.After))
                throw new IOException("重建权威已变化，禁止继续旧角色的目录启用。");
        }
        if (ExternalPreparationProcesses.IsRunning(_options.PostgresBinDirectory, RebuildSlot(plan)))
            throw new IOException("原基础备份或校验进程尚未退出。");
        if (progress.Phase == "Prepared")
            await SaveRebuildProgressAsync(progress with { Phase = "Installing" }, token);
        var database = Path.Combine(RebuildSlot(plan), Local.NodeId);
        var resources = Path.Combine(RebuildSlot(plan), "resources");
        var installed = DirectoryInstalled(database, _options.PostgresDataDirectory, progress.OldDatabaseIdentity,
            progress.DatabaseIdentity, plan.ContentHash) && DirectoryInstalled(resources, Local.ResourcesDirectory,
            progress.OldResourcesIdentity, progress.ResourcesIdentity, plan.ContentHash);
        if (!installed)
        {
            await _business.StopAsync(token);
            await _postgres.StopAsync(token);
            if (_business.Status != BusinessServiceStatus.Stopped || _postgres.Status != BusinessServiceStatus.Stopped)
                throw new IOException("本机服务未停止，禁止交换目录。");
            InstallDirectory(database, _options.PostgresDataDirectory,
                progress.OldDatabaseIdentity, progress.DatabaseIdentity, plan.ContentHash);
            if (DirectoryIdentity(Local.ResourcesDirectory) != progress.ResourcesIdentity)
                PreserveResourceExchangeJournal(plan.ContentHash);
            InstallDirectory(resources, Local.ResourcesDirectory,
                progress.OldResourcesIdentity, progress.ResourcesIdentity, plan.ContentHash);
        }
        await SelectAsync(plan, token);
        // A previous wait may have expired while PostgreSQL was replaying. Keep the
        // proven installation running so repeated reconciliation cannot reset progress.
        if (_postgres.Status != BusinessServiceStatus.Running) await _postgres.StartAsync(token);
        // Base backup/resources were verified before installation. Startup waits only
        // for fixed database identity/state and a cached resource inventory, not a full
        // resource rehash every poll or a moving zero-lag target under exam traffic.
        var deadline = _time.GetUtcNow().AddMinutes(5);
        while (!await VerifyRebuiltLocalAsync(plan, token, requireMarker: false))
        {
            if (_time.GetUtcNow() >= deadline) throw new IOException("重建节点未核实为正确备用库。");
            await Task.Delay(TimeSpan.FromSeconds(1), token);
        }
        // A verified installed standby is already a successful rebuild. Starting its
        // confirmation listener continues independently of the cancelable copy phase.
        await SaveRebuildProgressAsync(progress with { Phase = "Applied" }, token);
        await WriteRebuildMarkerAsync(plan, token);
        await _business.StartAsync(token);
        if (_business.Status != BusinessServiceStatus.Running) throw new IOException("备用确认服务尚未启动。");
        await SaveRebuildProgressAsync(progress with { Phase = "Completed" }, token);
        // The same durable marker was written at Applied, before starting the listener.
        if (progress.Phase == "Prepared") await RetireSupersededSlotsAsync(plan, token);
    }

    private void PreserveResourceExchangeJournal(string requestHash)
    {
        var resources = Local.ResourcesDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(resources)!;
        var original = PathBoundary.ResolveWithinRoot(parent, resources + ".replica-swap.json", "资源交换记录路径越界。");
        if (!File.Exists(original)) return;
        var quarantine = PathBoundary.ResolveWithinRoot(parent,
            resources + ".quarantine." + requestHash + ".replica-swap.json", "资源交换隔离路径越界。");
        if (File.Exists(quarantine)) throw new IOException("原资源交换记录与隔离记录同时存在，重建未覆盖记录。");
        File.Move(original, quarantine);
    }

    private void InstallDirectory(string staged, string target, string? oldIdentity, string newIdentity, string hash)
    {
        PathBoundary.ResolveWithinRoot(_options.RebuildRoot, staged, "重建暂存路径越界。");
        var quarantine = PathBoundary.ResolveWithinRoot(Path.GetDirectoryName(target)!, target + ".quarantine." + hash, "隔离路径越界。");
        var installed = DirectoryIdentity(target);
        if (installed == newIdentity && !Directory.Exists(staged) && DirectoryIdentity(quarantine) == oldIdentity) return;
        if (DirectoryIdentity(staged) != newIdentity) throw new IOException("重建暂存目录身份变化。");
        if (installed is not null)
        {
            if (installed != oldIdentity || Directory.Exists(quarantine)) throw new IOException("原部署目录与重建阶段不一致。");
            PreserveRebuildAccess(staged, target);
            ReplicaResourceTransfer.MoveDirectory(target, quarantine);
        }
        else if (DirectoryIdentity(quarantine) != oldIdentity) throw new IOException("原部署隔离证据不完整。");
        ReplicaResourceTransfer.MoveDirectory(staged, target);
    }

    private static bool DirectoryInstalled(string staged, string target, string? oldIdentity, string newIdentity, string hash) =>
        DirectoryIdentity(target) == newIdentity && !Directory.Exists(staged) &&
        DirectoryIdentity(target + ".quarantine." + hash) == oldIdentity;

    private bool HasRebuildCompletion(FailoverExecutionPlan plan)
    {
        // Applied is itself a durable completion proof, saved only after the
        // installed local standby was verified. A crash before the redundant
        // marker must not force another installation or a read of the old source.
        if (ReadRebuildProgress(plan) is { Phase: "Applied" or "Completed" }) return true;
        var marker = Path.Combine(RebuildSlot(plan), "rebuild-complete.json");
        var stored = DurableJsonFile.Read<FailoverExecutionPlan>(marker);
        return stored is not null && JsonSerializer.SerializeToUtf8Bytes(stored, JsonDefaults.Create()).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(plan, JsonDefaults.Create()));
    }
}
