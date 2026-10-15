using System.Text.Json;
using System.Text.RegularExpressions;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;
using Npgsql;

namespace Exam.Management;

/// <summary>Online maintenance belongs to the management host, not to client requests or OS scheduled tasks.</summary>
public sealed class AutomaticBackupWorker(IConfiguration configuration, Func<string, string?> selected) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ExternalProcessRunner _runner = new();
    private DateTimeOffset _retryAt;
    private string Required(string key) => selected(key) is { Length: > 0 } value ? value
        : throw new InvalidDataException("自动备份缺少配置：" + key);
    private string StateRoot => Path.GetFullPath(Required("Backup:Automatic:StateDirectory"));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                if (configuration.GetValue("Backup:Automatic:Enabled", false) && DateTimeOffset.UtcNow >= _retryAt)
                {
                    _retryAt = DateTimeOffset.UtcNow.AddHours(1);
                    try { await RunOnceAsync(stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception error)
                    {
                        // Do not expose native tool output, connection strings or certificate passwords.
                        try { await WriteStatusAsync("paused", error is MaintenancePaused ? error.Message :
                            "自动维护未完成，已保留现有备份和恢复归档。" + error.GetType().Name, stoppingToken); }
                        catch (Exception) when (!stoppingToken.IsCancellationRequested) { }
                    }
                }
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task RunOnceAsync(CancellationToken token)
    {
        var days = configuration.GetValue("Backup:Automatic:RetentionDays", 7);
        var hours = configuration.GetValue("Backup:Automatic:IntervalHours", 24);
        if (days < 1 || hours < 1) throw new InvalidDataException("备份保留天数和间隔必须为正数。");
        BackupRetentionPolicy.RejectLinks(StateRoot);
        Directory.CreateDirectory(StateRoot);
        await using var fileLock = new FileStream(Path.Combine(StateRoot, "maintenance.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var lastPath = Path.Combine(StateRoot, "status.json");
        if (File.Exists(lastPath))
        {
            using var previous = await ReadAsync(lastPath, token);
            if (previous.RootElement.GetProperty("status").GetString() == "completed" &&
                previous.RootElement.GetProperty("at").GetDateTimeOffset().AddHours(hours) > DateTimeOffset.UtcNow) return;
        }
        var serverId = Required("Management:ServerId");
        var nodeId = Required("Management:NodeId");
        var epoch = long.Parse(Required("ExamServer:ServerEpoch"), System.Globalization.CultureInfo.InvariantCulture);
        var connection = FileRecoveryDeploymentSelection.ResolveConnection(selected, serverId, nodeId, epoch)
            ?? throw new InvalidDataException("自动备份数据库未配置。");
        var resources = FileRecoveryDeploymentSelection.ResolveResources(selected, serverId, nodeId, epoch)
            ?? throw new InvalidDataException("自动备份资源未配置。");
        var settings = new NpgsqlConnectionStringBuilder(connection)
        { Pooling = false, Timeout = 5, CommandTimeout = 10, ApplicationName = "Exam.AutomaticBackup" };
        await using var database = new NpgsqlConnection(settings.ConnectionString);
        await database.OpenAsync(token);
        var state = await ObserveAsync(database, token);
        if (state.Recovery || state.ServerId != serverId || state.ActiveNodeId != nodeId)
            throw new MaintenancePaused("当前节点不是活动主节点，自动维护已暂停。");
        // An independent host/duplicate process must not run the same cluster's maintenance concurrently.
        await using (var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(18739042, 70931)", database))
            if (await acquire.ExecuteScalarAsync(token) is not true) return;
        RequireSynchronousStandby(state);
        var root = Path.GetFullPath(Required("Backup:AllowedBackupRoot"));
        var archive = Path.GetFullPath(Required("HighAvailability:WalArchiveDirectory"));
        BackupRetentionPolicy.RejectLinks(root);
        BackupRetentionPolicy.RejectLinks(archive);
        if (Overlaps(root, archive) || Overlaps(StateRoot, root) || Overlaps(StateRoot, archive) ||
            selected("HighAvailability:PostgresDataDirectory") is { Length: > 0 } live && Overlaps(live, archive))
            throw new InvalidDataException("备份、归档、维护状态与活动数据库目录不能重叠。");
        var backups = await CatalogueAsync(root, token);
        if (backups.Any(b => b.SystemIdentifier != state.SystemId))
            throw new MaintenancePaused("备份目录包含其他数据库实例的备份，已暂停自动维护。");
        var latest = backups.Where(b => b.Automatic).OrderByDescending(b => b.CompletedAt).FirstOrDefault();
        if (latest is null || latest.CompletedAt.AddHours(hours) <= DateTimeOffset.UtcNow)
        {
            var minimum = configuration.GetValue("Backup:Automatic:MinimumFreeBytes", 10L * 1024 * 1024 * 1024);
            if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < minimum)
                throw new MaintenancePaused("备份磁盘空间不足，已保留现有恢复材料并暂停自动维护。");
            var pendingPath = Path.Combine(StateRoot, "pending.json");
            Pending pending;
            if (File.Exists(pendingPath))
                pending = JsonSerializer.Deserialize<Pending>(await File.ReadAllTextAsync(pendingPath, token), Json)!;
            else
            {
                var name = "auto-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + Guid.NewGuid().ToString("N")[..8];
                pending = new(Path.Combine(root, name), name);
                await SaveAsync(pendingPath, pending, token);
            }
            var destination = BackupRetentionPolicy.Child(root, pending.Directory);
            if (!AutomaticName(destination)) throw new InvalidDataException("自动备份目录名称无效。");
            var manifestPath = Path.Combine(destination, "exam-backup.json");
            if (Directory.Exists(destination) && !File.Exists(manifestPath))
                throw new MaintenancePaused("上次自动备份未完成，半成品已保留；处理后才能继续，未清理旧备份。");
            if (!File.Exists(manifestPath))
            {
                var coordinator = new PostgresBackupCoordinator(new(Required("Backup:PostgresBinDirectory"), connection,
                    root, Required("Backup:RestoreStagingRoot"), resources, Required("Backup:ProtectedConfigurationDirectory"),
                    TimeSpan.FromMinutes(configuration.GetValue("Backup:ToolTimeoutMinutes", 60))), _runner);
                await coordinator.CreateAsync(new(pending.RequestId, destination, "automatic-backup"), token);
            }
            var executable = Path.Combine(Required("Backup:PostgresBinDirectory"),
                OperatingSystem.IsWindows() ? "pg_verifybackup.exe" : "pg_verifybackup");
            var verification = await _runner.RunAsync(executable,
                ["--skip-checksums", Path.Combine(destination, "database")],
                new Dictionary<string, string>(), TimeSpan.FromMinutes(10), token);
            if (verification.ExitCode != 0) throw new MaintenancePaused("新备份的文件或 WAL 校验未通过，已暂停清理。");
            using var manifest = await ReadAsync(manifestPath, token);
            if (manifest.RootElement.GetProperty("requestId").GetString() != pending.RequestId)
                throw new InvalidDataException("自动备份请求与清单不一致。");
            await SaveAsync(Path.Combine(destination, "maintenance-complete.json"),
                new { backupId = manifest.RootElement.GetProperty("backupId").GetString(), verifiedAt = DateTimeOffset.UtcNow }, token);
            File.Delete(pendingPath);
            backups = await CatalogueAsync(root, token);
        }
        foreach (var key in new[] { "Backup:RestoreStagingRoot", "Recovery:WorkingRoot", "HighAvailability:RebuildRoot" })
            if (selected(key) is { Length: > 0 } path && Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                throw new MaintenancePaused("存在恢复或重建工作目录，已暂停清理。");
        state = await ObserveAsync(database, token);
        RequireSamePrimary(state, serverId, nodeId);
        var keep = BackupRetentionPolicy.Keep(backups, DateTimeOffset.UtcNow, days);
        if (keep.Count == 0 || keep.Any(b => BackupRetentionPolicy.Timeline(b.StartWal) != state.Timeline))
            throw new MaintenancePaused("保留的备份跨越时间线，需核对恢复链后继续清理。");
        var floor = keep.Min(b => BackupRetentionPolicy.Segment(b.StartWal, state.SegmentBytes));
        floor = Math.Min(floor, state.RequiredPositions.Min(p => BackupRetentionPolicy.Lsn(p) / state.SegmentBytes));
        IReadOnlyList<string> obsoleteWal;
        try
        {
            var archiveFiles = Directory.EnumerateFiles(archive).Select(path => new FileInfo(path)).ToArray();
            if (archiveFiles.Any(f => BackupRetentionPolicy.IsWal(f.Name) &&
                                     BackupRetentionPolicy.Timeline(f.Name) == state.Timeline && (ulong)f.Length != state.SegmentBytes))
                throw new InvalidDataException("归档长度异常。");
            obsoleteWal = BackupRetentionPolicy.ObsoleteWal(archiveFiles.Select(f => f.Name),
                state.Timeline, state.SegmentBytes, floor, BackupRetentionPolicy.Lsn(state.Lsn) / state.SegmentBytes);
        }
        catch (InvalidDataException) { throw new MaintenancePaused("恢复所需归档尚未齐全，已暂停清理并等待下次重试。"); }
        var again = await ObserveAsync(database, token);
        RequireSamePrimary(again, serverId, nodeId);
        if (again.SystemId != state.SystemId || again.Timeline != state.Timeline ||
            again.RequiredPositions.Any(p => BackupRetentionPolicy.Lsn(p) / state.SegmentBytes < floor))
            throw new MaintenancePaused("主备恢复位置发生变化，已暂停清理。");
        var pinned = selected("Backup:StartupVerificationDirectory");
        var obsoleteBackups = backups.Where(b => b.Automatic && !keep.Contains(b) &&
            (string.IsNullOrEmpty(pinned) || !SamePath(b.Directory, pinned))).ToArray();
        long deleted = 0;
        foreach (var backup in obsoleteBackups)
        {
            token.ThrowIfCancellationRequested();
            var path = BackupRetentionPolicy.Child(root, backup.Directory);
            if (!AutomaticName(path)) throw new InvalidDataException("自动备份目录名称无效。");
            foreach (var child in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                BackupRetentionPolicy.Child(path, child);
            deleted += Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            Directory.Delete(path, recursive: true);
        }
        foreach (var name in obsoleteWal)
        {
            token.ThrowIfCancellationRequested();
            var path = BackupRetentionPolicy.Child(archive, Path.Combine(archive, name));
            deleted += new FileInfo(path).Length;
            File.Delete(path);
        }
        await SaveAsync(lastPath, new { status = "completed", at = DateTimeOffset.UtcNow, retentionDays = days,
            latestBackup = backups.MaxBy(b => b.CompletedAt)!.Directory,
            continuousRecoveryStartsAt = keep.Min(b => b.CompletedAt), keepWalFrom = BackupRetentionPolicy.WalName(state.Timeline, floor, state.SegmentBytes),
            deletedBackups = obsoleteBackups.Length, deletedWal = obsoleteWal.Count, deletedBytes = deleted }, token);
    }

    private void RequireSamePrimary(Observation state, string server, string node)
    {
        if (state.Recovery || state.ServerId != server || state.ActiveNodeId != node)
            throw new MaintenancePaused("活动主节点发生变化，已暂停清理。");
        RequireSynchronousStandby(state);
    }

    private static void RequireSynchronousStandby(Observation state)
    {
        if (!state.Synchronous || state.RequiredPositions.Count == 0)
            throw new MaintenancePaused("同步备机或复制槽暂不可用，已保留所有备份和恢复归档。");
    }

    private static async Task<Observation> ObserveAsync(NpgsqlConnection database, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("""
            SELECT json_build_object('recovery',pg_is_in_recovery(),
              'systemId',(SELECT system_identifier::text FROM pg_control_system()),
              'timeline',(SELECT timeline_id FROM pg_control_checkpoint()),
              'segmentBytes',pg_size_bytes(current_setting('wal_segment_size')),
              'lsn',CASE WHEN pg_is_in_recovery() THEN pg_last_wal_replay_lsn()::text ELSE pg_current_wal_lsn()::text END,
              'serverId',(SELECT server_id FROM exam.server_state LIMIT 1),
              'activeNodeId',(SELECT active_node_id FROM exam.server_state LIMIT 1),
              'synchronous',current_setting('archive_mode')='on' AND current_setting('synchronous_commit')='remote_apply'
                AND EXISTS(SELECT 1 FROM pg_stat_replication WHERE state='streaming' AND sync_state='sync' AND replay_lsn IS NOT NULL)
                AND EXISTS(SELECT 1 FROM pg_replication_slots WHERE slot_type='physical')
                AND NOT EXISTS(SELECT 1 FROM pg_replication_slots WHERE restart_lsn IS NULL OR wal_status='lost'),
              'requiredPositions',(SELECT COALESCE(json_agg(position),'[]'::json) FROM (
                SELECT restart_lsn::text AS position FROM pg_replication_slots WHERE restart_lsn IS NOT NULL
                UNION ALL SELECT replay_lsn::text FROM pg_stat_replication WHERE replay_lsn IS NOT NULL) positions));
            """, database);
        return JsonSerializer.Deserialize<Observation>((string)(await command.ExecuteScalarAsync(token))!, Json)!;
    }

    private static async Task<List<RetainedBackup>> CatalogueAsync(string root, CancellationToken token)
    {
        var result = new List<RetainedBackup>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            BackupRetentionPolicy.Child(root, directory);
            var manifestPath = Path.Combine(directory, "exam-backup.json");
            if (!File.Exists(manifestPath)) continue;
            using var manifest = await ReadAsync(manifestPath, token);
            var value = manifest.RootElement;
            if (value.GetProperty("formatVersion").GetInt32() != 2) throw new InvalidDataException("备份格式不支持。");
            var database = BackupRetentionPolicy.Child(directory, Path.Combine(directory, value.GetProperty("databaseDirectory").GetString()!));
            using var native = await ReadAsync(Path.Combine(database, "backup_manifest"), token);
            var label = await File.ReadAllTextAsync(Path.Combine(database, "backup_label"), token);
            var start = Regex.Match(label, @"START WAL LOCATION: .*\(file ([0-9A-F]{24})\)").Groups[1].Value;
            if (!BackupRetentionPolicy.IsWal(start)) throw new InvalidDataException("备份没有起始 WAL。");
            var id = value.GetProperty("backupId").GetString()!;
            var automatic = AutomaticName(directory);
            if (automatic)
            {
                var marker = Path.Combine(directory, "maintenance-complete.json");
                if (!File.Exists(marker)) continue;
                using var completion = await ReadAsync(marker, token);
                if (completion.RootElement.GetProperty("backupId").GetString() != id)
                    throw new InvalidDataException("自动备份标记不一致。");
            }
            result.Add(new(directory, id, value.GetProperty("completedAt").GetDateTimeOffset(), start,
                native.RootElement.GetProperty("System-Identifier").ToString(), automatic));
        }
        return result;
    }

    private static bool AutomaticName(string path) => Regex.IsMatch(Path.GetFileName(path), @"^auto-\d{8}T\d{6}Z-[0-9a-f]{8}$");
    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Overlaps(string a, string b)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
        b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
        return string.Equals(a, b, comparison) || a.StartsWith(b + Path.DirectorySeparatorChar, comparison)
            || b.StartsWith(a + Path.DirectorySeparatorChar, comparison);
    }
    private async Task WriteStatusAsync(string status, string reason, CancellationToken token)
    {
        BackupRetentionPolicy.RejectLinks(StateRoot);
        Directory.CreateDirectory(StateRoot);
        await SaveAsync(Path.Combine(StateRoot, "status.json"), new { status, at = DateTimeOffset.UtcNow, reason }, token);
    }
    private static async Task<JsonDocument> ReadAsync(string path, CancellationToken token)
    {
        BackupRetentionPolicy.RejectLinks(path);
        await using var stream = File.OpenRead(path);
        return await JsonDocument.ParseAsync(stream, cancellationToken: token);
    }
    private static async Task SaveAsync<T>(string path, T value, CancellationToken token)
    {
        BackupRetentionPolicy.RejectLinks(path);
        var temporary = path + ".tmp";
        BackupRetentionPolicy.RejectLinks(temporary);
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, value, Json, token);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
    private sealed record Pending(string Directory, string RequestId);
    private sealed record Observation(bool Recovery, string SystemId, uint Timeline, ulong SegmentBytes, string Lsn,
        string ServerId, string ActiveNodeId, bool Synchronous, List<string> RequiredPositions);
    private sealed class MaintenancePaused(string message) : Exception(message);
}
