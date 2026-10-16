using System.Text.Json;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;
using Npgsql;

namespace Exam.Management;

/// <summary>Backup creation and retention have independent gates; neither changes exam or replication state.</summary>
public sealed partial class AutomaticBackupWorker(IConfiguration configuration, Func<string, string?> selected,
    IExternalProcessRunner? processRunner = null, TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly IExternalProcessRunner _runner = processRunner ?? new ExternalProcessRunner();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private DateTimeOffset Now => _time.GetUtcNow();
    private string Required(string key) => selected(key) is { Length: > 0 } value ? value :
        throw new MaintenanceDeferred($"自动维护缺少配置项 {key}。");
    private string StateRoot => Path.GetFullPath(Required("Backup:Automatic:StateDirectory"));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), _time, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (configuration.GetValue<bool>("Backup:Automatic:Enabled")) await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { /* RunOnce persists a bounded, non-sensitive status whenever its state directory is usable. */ }
            await Task.Delay(TimeSpan.FromMinutes(5), _time, stoppingToken);
        }
    }

    public async Task<BackupMaintenanceStatus> RunOnceAsync(CancellationToken token)
    {
        var status = new BackupMaintenanceStatus { Enabled = configuration.GetValue<bool>("Backup:Automatic:Enabled"), ObservedAt = Now };
        if (!status.Enabled) return status;
        var warnings = new List<string>();
        Paths? paths = null;
        var stateDirectoryValidated = false;
        FileStream? cycle = null;
        async Task Publish()
        {
            if (stateDirectoryValidated && paths is not null) status = Measure(status, paths);
            var notices = warnings.ToList();
            AddSpaceWarnings(status, notices);
            status = status with { ObservedAt = Now, Warnings = notices.Distinct(StringComparer.Ordinal).Take(16).ToArray() };
            if (stateDirectoryValidated)
                await AutomaticBackupStatusFile.SaveAsync(Path.Combine(StateRoot, "status.json"), status, token);
        }
        try
        {
            paths = ReadPaths();
            ValidatePaths(paths);
            stateDirectoryValidated = true;
            Directory.CreateDirectory(StateRoot);
            Directory.CreateDirectory(paths.Backups);
            BackupRetentionPolicy.RejectLinks(Path.Combine(StateRoot, "maintenance.lock"));
            try { cycle = new FileStream(Path.Combine(StateRoot, "maintenance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException)
            {
                // Never overwrite the heartbeat of the cycle that currently holds this lock.
                return await AutomaticBackupStatusFile.ReadAsync(true, StateRoot, token) ?? status;
            }
            var days = configuration.GetValue("Backup:Automatic:RetentionDays", 7);
            var hours = configuration.GetValue("Backup:Automatic:IntervalHours", 24);
            var minimum = configuration.GetValue("Backup:Automatic:MinimumFreeBytes", 10L * 1024 * 1024 * 1024);
            if (days is < 1 or > 365 || hours is < 1 or > 168 || minimum <= 0)
                throw new MaintenanceDeferred("自动维护的保留天数、备份间隔或空间阈值无效。");
            status = status with { RetentionDays = days, MinimumAvailableBytes = minimum,
                BackupPhase = BackupMaintenancePhase.Waiting, CleanupPhase = BackupMaintenancePhase.Waiting };
            var server = Required("Management:ServerId");
            var node = Required("Management:NodeId");
            var epoch = long.Parse(Required("ExamServer:ServerEpoch"), System.Globalization.CultureInfo.InvariantCulture);
            var connection = FileRecoveryDeploymentSelection.ResolveConnection(selected, server, node, epoch)
                ?? throw new MaintenanceDeferred("自动维护没有可用的当前数据库配置。");
            var builder = new NpgsqlConnectionStringBuilder(connection) { Pooling = false, Timeout = 5,
                CommandTimeout = 10, ApplicationName = "Exam.AutomaticBackup" };
            await using var db = new NpgsqlConnection(builder.ConnectionString);
            await db.OpenAsync(token);
            var before = await Observe(db, token);
            if (before.Recovery || before.ServerId != server || before.ActiveNodeId != node)
            {
                status = status with { Message = "本节点不是已核实的活动主库，不执行备份或清理。" };
                await Publish(); return status;
            }
            await using (var advisory = new NpgsqlCommand("SELECT pg_try_advisory_lock(18739042, 70931);", db))
                if (await advisory.ExecuteScalarAsync(token) is not true)
                    throw new MaintenanceDeferred("另一个自动维护任务正在运行，本轮等待，不删除任何材料。");

            var resources = FileRecoveryDeploymentSelection.ResolveResources(selected, server, node, epoch)
                ?? throw new MaintenanceDeferred("当前版本化资源目录未配置。");
            var coordinator = new PostgresBackupCoordinator(new PostgresBackupOptions(
                Required("Backup:PostgresBinDirectory"), connection, paths.Backups, Required("Backup:RestoreStagingRoot"),
                resources, Required("Backup:ProtectedConfigurationDirectory"),
                TimeSpan.FromMinutes(configuration.GetValue("Backup:ToolTimeoutMinutes", 60))), _runner, _time);
            var catalogue = Catalogue(paths.Backups);
            status = status with { LastSuccessfulBackupAt = LatestAutomatic(catalogue.Backups)?.CompletedAt,
                IncompleteBackups = catalogue.Incomplete, StandbyConnected = before.StandbyConnected,
                ReplicationRetainedBytes = RetainedBytes(before) };
            if (!before.StandbyConnected && RequiresStandby)
                warnings.Add("备机未建立流复制连接；本机备份仍独立运行，清理将保留有效复制槽需要的 WAL。");
            if (before.ArchiveFailure) warnings.Add("PostgreSQL 最近一次归档失败尚未被成功归档取代，请检查归档服务与磁盘。");
            await Publish();

            // Failure or deferral in this phase must not bypass retention's separate safety assessment.
            try
            {
                var result = await BackupPhaseAsync(db, before, paths, catalogue.Backups, coordinator, hours, minimum,
                    warnings, async () => { status = status with { BackupPhase = BackupMaintenancePhase.Running }; await Publish(); }, token);
                status = status with { BackupPhase = result.Phase, NextBackupAttemptAt = result.NextAttempt };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (MaintenanceDeferred error)
            { status = status with { BackupPhase = BackupMaintenancePhase.Deferred }; warnings.Add(error.Message); }
            catch (Exception error)
            {
                status = status with { BackupPhase = BackupMaintenancePhase.Failed };
                warnings.Add($"新备份未完成（{error.GetType().Name}）；有效备份保持不变，未完成目录将在受控重试时处理。");
            }
            catalogue = Catalogue(paths.Backups);
            status = status with { LastSuccessfulBackupAt = LatestAutomatic(catalogue.Backups)?.CompletedAt,
                IncompleteBackups = catalogue.Incomplete };
            await Publish();
            try
            {
                var result = await CleanupPhaseAsync(db, before, paths, catalogue, coordinator, days, token);
                status = status with { CleanupPhase = BackupMaintenancePhase.Completed,
                    RemovedBackups = result.Backups, RemovedWal = result.Wal, ReclaimedBytes = result.Bytes };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (MaintenanceDeferred error)
            { status = status with { CleanupPhase = BackupMaintenancePhase.Deferred }; warnings.Add(error.Message); }
            catch (Exception error)
            {
                status = status with { CleanupPhase = BackupMaintenancePhase.Failed };
                warnings.Add($"清理未完成（{error.GetType().Name}）；未证明可删除的备份和归档均保留。");
            }
            if (status.IncompleteBackups > 0)
                warnings.Add("备份目录中仍有未完成或旧版本遗留的自动备份；未计入有效恢复链，未确认归属的目录不会自动删除。");
            if (status.LastSuccessfulBackupAt is null || Now - status.LastSuccessfulBackupAt > TimeSpan.FromHours(hours * 2))
                warnings.Add("尚无有效自动备份，或最近成功备份已超过两个备份周期，请及时处理备份告警。");
            status = status with { Message = "备份创建与保留清理已分别检查；不会删除复制槽、停用归档或修改考试数据。" };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            status = status with { BackupPhase = BackupMaintenancePhase.Deferred, CleanupPhase = BackupMaintenancePhase.Deferred };
            warnings.Add(error is MaintenanceDeferred ? error.Message : $"自动维护暂不可用（{error.GetType().Name}），未执行未获安全证明的清理。");
        }
        finally
        {
            if (cycle is not null)
            {
                try { if (!token.IsCancellationRequested) await Publish(); }
                finally { await cycle.DisposeAsync(); }
            }
        }
        if (cycle is null) await Publish();
        return status;
    }

    private bool RequiresStandby => configuration.GetSection("HighAvailability:Nodes").GetChildren().Any();

    private async Task<(BackupMaintenancePhase Phase, DateTimeOffset? NextAttempt)> BackupPhaseAsync(
        NpgsqlConnection db, Observation before, Paths paths, IReadOnlyList<RetainedBackup> backups,
        PostgresBackupCoordinator coordinator, int hours, long minimum, List<string> warnings, Func<Task> progress, CancellationToken token)
    {
        var pendingPath = Path.Combine(StateRoot, "pending.json");
        var pending = ReadSmall<Pending>(pendingPath);
        if (pending is not null)
        {
            ValidatePending(paths.Backups, pending);
            if (pending.SystemIdentifier is not null && pending.SystemIdentifier != before.SystemId)
                throw new MaintenanceDeferred("未完成备份属于另一个数据库集群，保留原目录并停止对此请求的重试。");
            var manifestPath = Path.Combine(pending.Directory, "exam-backup.json");
            if (File.Exists(manifestPath))
            {
                await progress();
                try
                {
                    // A crashed manifest write is a recoverable partial, not a permanent scheduler failure.
                    // Reclamation below still requires a cluster-bound pending request and the exclusive lease.
                    using var manifest = ReadDocument(manifestPath);
                    var value = manifest.RootElement;
                    if (value.ValueKind != JsonValueKind.Object ||
                        !value.TryGetProperty("requestId", out var requestId) || requestId.ValueKind != JsonValueKind.String ||
                        !value.TryGetProperty("actorId", out var actorId) || actorId.ValueKind != JsonValueKind.String ||
                        !value.TryGetProperty("backupId", out var backupId) || backupId.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(backupId.GetString()))
                        throw new InvalidDataException("上次备份清单尚未写入完整。");
                    if (requestId.GetString() != pending.RequestId || actorId.GetString() != "automatic-backup")
                        throw new MaintenanceDeferred("未完成目录的请求身份不一致，未删除或覆盖该目录。");
                    var id = backupId.GetString()!;
                    await coordinator.VerifyAvailableBackupAsync(pending.Directory, id, token);
                    var recovered = ReadBackup(pending.Directory, true, requireCompletion: false);
                    if (recovered.SystemIdentifier != before.SystemId) throw new MaintenanceDeferred("未完成备份的原生集群身份不匹配。");
                    RequireSamePrimary(before, await Observe(db, token));
                    await MarkComplete(pending.Directory, id, token);
                    File.Delete(pendingPath); ResetRetry();
                    return (BackupMaintenancePhase.Completed, recovered.CompletedAt.AddHours(hours));
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (MaintenanceDeferred) { throw; }
                catch when (!File.Exists(Path.Combine(pending.Directory, "maintenance-complete.json")))
                { warnings.Add("上次未完成备份校验未通过，将在确认没有活动读取后回收本程序的半成品并受控重试。"); }
            }
            if (Directory.Exists(pending.Directory))
            {
                if (pending.SystemIdentifier is null)
                {
                    // Legacy pending records did not bind a cluster identity. Preserve that data once,
                    // retire only its request metadata, and let new owned requests resume automatically.
                    await AutomaticBackupStatusFile.SaveAsync(Path.Combine(StateRoot, "legacy-" + pending.RequestId + ".json"), pending, token);
                    warnings.Add("旧版本半成品缺少归属证明，已保留原文件；新的自动备份不再被该半成品永久阻塞。");
                }
                else
                {
                    await using var lease = await BackupDirectoryLease.CleanupAsync(paths.Backups, token);
                    var current = await Observe(db, token); RequireSamePrimary(before, current);
                    if (current.NativeBackupsRunning > 0) throw new MaintenanceDeferred("仍有原生备份进程运行，暂不回收半成品。");
                    EnsureRecoveryIdle();
                    if (File.Exists(Path.Combine(pending.Directory, "maintenance-complete.json")) || IsPinned(pending.Directory))
                        throw new MaintenanceDeferred("该目录已有完成凭据或被恢复配置固定保留，未作为半成品删除。");
                    DeleteTree(paths.Backups, pending.Directory);
                }
            }
            File.Delete(pendingPath);
        }
        var latest = LatestAutomatic(backups);
        if (pending is null && latest is not null && latest.CompletedAt.AddHours(hours) > Now)
            return (BackupMaintenancePhase.Waiting, latest.CompletedAt.AddHours(hours));
        var currentState = await Observe(db, token); RequireSamePrimary(before, currentState);
        if (currentState.NativeBackupsRunning > 0) throw new MaintenanceDeferred("其他原生备份正在运行，本轮不并发创建新备份。");
        var available = Available(paths.Backups);
        var estimate = latest is not null ? DirectoryBytes(latest.Directory) : DirectoryBytes(paths.Database);
        if (MinimumAvailable(paths) is not { } minimumFree || minimumFree < minimum ||
            available is null || estimate is null || available < minimum ||
            estimate > long.MaxValue / 2 || available - minimum < estimate + estimate / 4)
            throw new MaintenanceDeferred("备份卷空间不足以容纳预计的新备份并保留安全余量，已暂停新备份；仍会独立评估安全清理。");
        var retryPath = Path.Combine(StateRoot, "retry.json");
        var retry = ReadSmall<RetryWindow>(retryPath) ?? new(Now, 0, Now);
        if (Now >= retry.WindowStart.AddHours(24)) retry = new(Now, 0, Now);
        var limit = Math.Clamp(configuration.GetValue("Backup:Automatic:MaximumAttemptsPerDay", 3), 1, 8);
        var next = retry.Attempts >= limit ? retry.WindowStart.AddHours(24) : retry.NextAttempt;
        if (next > Now) { warnings.Add("自动备份处于受控重试间隔，清理与磁盘检查仍继续运行。"); return (BackupMaintenancePhase.Deferred, next); }
        retry = retry with { Attempts = retry.Attempts + 1, NextAttempt = Now.AddHours(1) };
        await AutomaticBackupStatusFile.SaveAsync(retryPath, retry, token);
        var name = "auto-" + Now.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];
        pending = new(Path.Combine(paths.Backups, name), name, before.SystemId, Now);
        await AutomaticBackupStatusFile.SaveAsync(pendingPath, pending, token);
        await progress();
        using var createToken = CancellationTokenSource.CreateLinkedTokenSource(token);
        var task = CreateAndVerify();
        try
        {
        while (!task.IsCompleted)
        {
            var tick = Task.Delay(TimeSpan.FromMinutes(1), _time, token);
            if (await Task.WhenAny(task, tick) == task) break;
            token.ThrowIfCancellationRequested();
            await progress();
            if (MinimumAvailable(paths) is not { } free || free < minimum)
            {
                createToken.Cancel();
                try { await task; } catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                throw new MaintenanceDeferred("创建备份期间可用空间跌破安全余量，已停止本次新备份并保留重试状态；继续评估安全清理。");
            }
        }
        await task;
        }
        catch
        {
            // Always reap the native backup before releasing its lock or disposing its database observation.
            createToken.Cancel();
            try { await task; } catch { }
            throw;
        }
        File.Delete(pendingPath); ResetRetry();
        return (BackupMaintenancePhase.Completed, Now.AddHours(hours));
        async Task CreateAndVerify()
        {
            var result = await coordinator.CreateAsync(new(pending.RequestId, pending.Directory, "automatic-backup"), createToken.Token);
            // Full native checksums and WAL parsing, plus versioned resource/configuration hashes.
            await coordinator.VerifyAvailableBackupAsync(pending.Directory, result.BackupId, createToken.Token);
            if (ReadBackup(pending.Directory, true, requireCompletion: false).SystemIdentifier != before.SystemId)
                throw new MaintenanceDeferred("新备份的原生集群身份与活动主库不匹配，未标记完成。");
            RequireSamePrimary(before, await Observe(db, createToken.Token));
            await MarkComplete(pending.Directory, result.BackupId, createToken.Token);
        }
    }

    private async Task<(int Backups, int Wal, long Bytes)> CleanupPhaseAsync(NpgsqlConnection db, Observation before,
        Paths paths, BackupCatalogue catalogue, PostgresBackupCoordinator coordinator, int days, CancellationToken token)
    {
        var current = await Observe(db, token); RequireSamePrimary(before, current); RequireCleanupEvidence(current);
        if (catalogue.Errors.Count > 0 || catalogue.Backups.Any(b => b.SystemIdentifier != before.SystemId))
            throw new MaintenanceDeferred("已有备份清单损坏或混入其他集群，新的备份可独立运行，但本轮保留旧备份和归档。");
        EnsureRecoveryIdle();
        var keep = BackupRetentionPolicy.Keep(catalogue.Backups, Now, days);
        var plan = ArchivePlan(paths.Archive, keep, current);
        var obsolete = catalogue.Backups.Where(b => b.Automatic && !keep.Contains(b) && !IsPinned(b.Directory)).ToArray();
        if (obsolete.Length == 0 && plan.ObsoleteWal.Count == 0) return (0, 0, 0);
        foreach (var backup in keep) await coordinator.VerifyAvailableBackupAsync(backup.Directory, backup.BackupId, token);
        // Built-in backup/restore readers share this lease; deletion takes it exclusively only after verification.
        await using var lease = await BackupDirectoryLease.CleanupAsync(paths.Backups, token);
        EnsureRecoveryIdle();
        var final = await Observe(db, token); RequireSamePrimary(current, final); RequireCleanupEvidence(final);
        var fresh = Catalogue(paths.Backups);
        var finalKeep = BackupRetentionPolicy.Keep(fresh.Backups, Now, days);
        if (fresh.Errors.Count > 0 || !keep.Select(b => b.BackupId).Order(StringComparer.Ordinal)
                .SequenceEqual(finalKeep.Select(b => b.BackupId).Order(StringComparer.Ordinal)))
            throw new MaintenanceDeferred("保留备份集合在核对期间变化，等待下一轮重新证明恢复链。");
        var finalPlan = ArchivePlan(paths.Archive, finalKeep, final);
        var wal = plan.ObsoleteWal.Intersect(finalPlan.ObsoleteWal, StringComparer.Ordinal).ToArray();
        long bytes = 0;
        foreach (var backup in obsolete) { token.ThrowIfCancellationRequested(); bytes += DeleteTree(paths.Backups, backup.Directory); }
        foreach (var name in wal)
        {
            token.ThrowIfCancellationRequested();
            var path = BackupRetentionPolicy.Child(paths.Archive, Path.Combine(paths.Archive, name));
            bytes += new FileInfo(path).Length; File.Delete(path);
        }
        return (obsolete.Length, wal.Length, bytes);
    }

    private ArchiveRetentionPlan ArchivePlan(string archive, IReadOnlyCollection<RetainedBackup> keep, Observation state)
    {
        try
        {
            var history = new Dictionary<uint, string>();
            if (state.Timeline > 1)
            {
                var path = BackupRetentionPolicy.Child(archive, Path.Combine(archive, state.Timeline.ToString("X8") + ".history"));
                if (File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024) history[state.Timeline] = File.ReadAllText(path);
            }
            var plan = BackupRetentionPolicy.PlanArchiveCleanup(Directory.EnumerateFiles(archive).Select(path => Path.GetFileName(path)),
                history, keep, state.Timeline, state.SegmentBytes, state.Lsn, state.RequiredPositions);
            foreach (var name in plan.RequiredWal)
                if ((ulong)new FileInfo(BackupRetentionPolicy.Child(archive, Path.Combine(archive, name))).Length != state.SegmentBytes)
                    throw new MaintenanceDeferred("保留恢复链包含不完整的 WAL 文件，未执行清理。");
            return plan;
        }
        catch (InvalidDataException error) { throw new MaintenanceDeferred(error.Message); }
    }

    private void RequireCleanupEvidence(Observation value)
    {
        if (!value.ArchivingEnabled) throw new MaintenanceDeferred("连续归档未启用或归档命令未配置，保留现有恢复材料。");
        if (!value.SlotsHealthy || RequiresStandby && value.PhysicalSlots == 0)
            throw new MaintenanceDeferred("复制槽缺失、失效或存在没有受控保留位置的备机，暂不清理恢复材料；本机备份不受此条件阻止。");
    }
    private static void RequireSamePrimary(Observation expected, Observation actual)
    {
        if (actual.Recovery || expected.SystemId != actual.SystemId || expected.Timeline != actual.Timeline ||
            expected.ServerId != actual.ServerId || expected.ActiveNodeId != actual.ActiveNodeId)
            throw new MaintenanceDeferred("活动主库身份或时间线在维护期间改变，等待下一轮重新核对，未继续清理。");
    }
    private sealed class MaintenanceDeferred(string message) : Exception(message);
    private sealed record Pending(string Directory, string RequestId, string? SystemIdentifier = null, DateTimeOffset? CreatedAt = null);
    private sealed record RetryWindow(DateTimeOffset WindowStart, int Attempts, DateTimeOffset NextAttempt);
}
