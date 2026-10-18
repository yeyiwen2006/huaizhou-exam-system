using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;
using Npgsql;

namespace Exam.Management;

public sealed partial class AutomaticBackupWorker
{
    private sealed record Paths(string Backups, string Archive, string Database, string State);
    private sealed record BackupCatalogue(IReadOnlyList<RetainedBackup> Backups, int Incomplete, IReadOnlyList<string> Errors);
    private Paths ReadPaths() => new(Path.GetFullPath(Required("Backup:AllowedBackupRoot")),
        Path.GetFullPath(Required("HighAvailability:WalArchiveDirectory")),
        Path.GetFullPath(Required("HighAvailability:PostgresDataDirectory")), StateRoot);

    private static void ValidatePaths(Paths paths)
    {
        var roots = new[] { paths.Backups, paths.Archive, paths.Database, paths.State };
        foreach (var root in roots)
        {
            BackupRetentionPolicy.RejectLinks(root);
            if (SamePath(root, Path.GetPathRoot(root)!)) throw new MaintenanceDeferred("自动维护目录不能是磁盘根目录。");
        }
        for (var a = 0; a < roots.Length; a++)
            for (var b = a + 1; b < roots.Length; b++)
                if (Overlaps(roots[a], roots[b])) throw new MaintenanceDeferred("数据库、备份、归档和维护状态目录必须互不包含。");
    }

    private static BackupCatalogue Catalogue(string root)
    {
        var backups = new List<RetainedBackup>(); var errors = new List<string>(); var incomplete = 0;
        foreach (var candidate in Directory.EnumerateDirectories(root))
        {
            try
            {
                var directory = BackupRetentionPolicy.Child(root, candidate);
                var automatic = AutomaticName().IsMatch(Path.GetFileName(directory));
                if (automatic && !File.Exists(Path.Combine(directory, "maintenance-complete.json")))
                { incomplete++; continue; } // Never parse half-written manifests before this completion gate.
                if (!File.Exists(Path.Combine(directory, "exam-backup.json"))) continue;
                backups.Add(ReadBackup(directory, automatic, requireCompletion: automatic));
            }
            catch { errors.Add("备份元数据不能完整核实。"); }
        }
        return new(backups, incomplete, errors);
    }

    private static RetainedBackup ReadBackup(string directory, bool automatic, bool requireCompletion)
    {
        using var metadata = ReadDocument(BackupRetentionPolicy.Child(directory, Path.Combine(directory, "exam-backup.json")));
        var root = metadata.RootElement;
        if (root.GetProperty("formatVersion").GetInt32() != 2) throw new InvalidDataException("备份清单版本不支持。");
        var id = root.GetProperty("backupId").GetString() ?? throw new InvalidDataException("备份编号缺失。");
        if (requireCompletion)
        {
            using var completion = ReadDocument(BackupRetentionPolicy.Child(directory, Path.Combine(directory, "maintenance-complete.json")));
            if (completion.RootElement.GetProperty("backupId").GetString() != id)
                throw new InvalidDataException("自动备份完成凭据不匹配。");
            _ = completion.RootElement.GetProperty("verifiedAt").GetDateTimeOffset();
        }
        var database = BackupRetentionPolicy.Child(directory, Path.Combine(directory, root.GetProperty("databaseDirectory").GetString()!));
        using var native = ReadDocument(BackupRetentionPolicy.Child(database, Path.Combine(database, "backup_manifest")));
        var labelPath = BackupRetentionPolicy.Child(database, Path.Combine(database, "backup_label"));
        var label = File.ReadAllText(labelPath);
        var match = Regex.Match(label, @"(?m)^START WAL LOCATION: [0-9A-F]+/[0-9A-F]+ \(file ([0-9A-F]{24})\)\r?$");
        if (!match.Success) throw new InvalidDataException("备份起始 WAL 标签无效。");
        var ranges = native.RootElement.GetProperty("WAL-Ranges").EnumerateArray()
            .Select(r => new BackupWalRange(r.GetProperty("Timeline").GetUInt32(),
                r.GetProperty("Start-LSN").GetString()!, r.GetProperty("End-LSN").GetString()!)).ToArray();
        if (ranges.Length == 0) throw new InvalidDataException("原生备份没有 WAL 范围。");
        return new(directory, id, root.GetProperty("completedAt").GetDateTimeOffset(), match.Groups[1].Value,
            native.RootElement.GetProperty("System-Identifier").ToString(), automatic, ranges);
    }

    private static RetainedBackup? LatestAutomatic(IEnumerable<RetainedBackup> backups) =>
        backups.Where(b => b.Automatic).OrderByDescending(b => b.CompletedAt).FirstOrDefault();
    private static JsonDocument ReadDocument(string path)
    {
        BackupRetentionPolicy.RejectLinks(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 64L * 1024 * 1024) throw new InvalidDataException("备份清单过大。");
        return JsonDocument.Parse(input);
    }
    private static T? ReadSmall<T>(string path) where T : class
    {
        BackupRetentionPolicy.RejectLinks(path);
        if (!File.Exists(path)) return default;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 65536) throw new InvalidDataException("维护请求状态无效。");
        return JsonSerializer.Deserialize<T>(input, AutomaticBackupStatusFile.Json) ?? throw new InvalidDataException("维护请求状态为空。");
    }
    private static void ValidatePending(string root, Pending pending)
    {
        var path = BackupRetentionPolicy.Child(root, pending.Directory);
        if (!AutomaticName().IsMatch(pending.RequestId) || Path.GetFileName(path) != pending.RequestId ||
            !SamePath(path, Path.Combine(root, pending.RequestId)))
            throw new MaintenanceDeferred("未完成备份的目录或请求编号无效，未删除该目录。");
    }
    private Task MarkComplete(string directory, string id, CancellationToken token) =>
        AutomaticBackupStatusFile.SaveAsync(Path.Combine(directory, "maintenance-complete.json"),
            new { backupId = id, verifiedAt = Now, verification = "pg_verifybackup-full-and-resource-hashes" }, token);
    private void ResetRetry()
    {
        var path = Path.Combine(StateRoot, "retry.json"); BackupRetentionPolicy.RejectLinks(path);
        if (File.Exists(path)) File.Delete(path);
    }
    private bool IsPinned(string path) => selected("Backup:StartupVerificationDirectory") is { Length: > 0 } pinned && SamePath(path, pinned);
    private void EnsureRecoveryIdle()
    {
        foreach (var key in new[] { "Backup:RestoreStagingRoot", "Recovery:WorkingRoot", "HighAvailability:RebuildRoot" })
        {
            if (selected(key) is not { Length: > 0 } path) continue;
            BackupRetentionPolicy.RejectLinks(path);
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                throw new MaintenanceDeferred("存在隔离恢复或重建工作材料，保留相关备份和归档；新备份仍独立调度。");
        }
    }

    private static long DeleteTree(string root, string directory)
    {
        directory = BackupRetentionPolicy.Child(root, directory);
        long bytes = 0;
        // Inspect each entry before descending; SearchOption.AllDirectories could follow an unexpected junction.
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var child = BackupRetentionPolicy.Child(root, entry);
            if (Directory.Exists(child)) bytes += DeleteTree(root, child);
            else { bytes += new FileInfo(child).Length; File.Delete(child); }
        }
        Directory.Delete(directory, recursive: false);
        return bytes;
    }
    private static long? DirectoryBytes(string directory)
    {
        try
        {
            BackupRetentionPolicy.RejectLinks(directory);
            if (!Directory.Exists(directory)) return 0;
            long bytes = 0; var stack = new Stack<string>(); stack.Push(directory);
            while (stack.TryPop(out var current))
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    BackupRetentionPolicy.RejectLinks(entry);
                    if (Directory.Exists(entry)) stack.Push(entry);
                    else bytes = checked(bytes + new FileInfo(entry).Length);
                }
            return bytes;
        }
        catch { return null; }
    }
    private static long? Available(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var drive = DriveInfo.GetDrives().Where(d => full.StartsWith(d.Name.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
                .OrderByDescending(d => d.Name.Length).FirstOrDefault() ?? new DriveInfo(Path.GetPathRoot(full)!);
            return drive.AvailableFreeSpace;
        }
        catch { return null; }
    }
    private static long? MinimumAvailable(Paths paths)
    {
        var values = new[] { Available(paths.Backups), Available(paths.Archive), Available(paths.Database) };
        return values.Any(v => v is null) ? null : values.Min();
    }
    private static BackupMaintenanceStatus Measure(BackupMaintenanceStatus status, Paths paths) => status with
    {
        AvailableBytes = MinimumAvailable(paths), BackupBytes = DirectoryBytes(paths.Backups),
        ArchiveBytes = DirectoryBytes(paths.Archive), WalBytes = DirectoryBytes(Path.Combine(paths.Database, "pg_wal"))
    };
    private void AddSpaceWarnings(BackupMaintenanceStatus status, List<string> warnings)
    {
        if (status.AvailableBytes is not { } available) warnings.Add("无法核实某个数据库、备份或归档卷的可用空间。");
        else if (available < status.MinimumAvailableBytes)
            warnings.Add("磁盘可用空间低于安全阈值：新备份已受限，归档和 pg_wal 仍可能增长；请立即处理空间或复制积压。");
        else if (available - status.MinimumAvailableBytes < status.MinimumAvailableBytes)
            warnings.Add("磁盘空间接近安全阈值，请留意归档、备份和 pg_wal 的持续增长。");
        if (status.ArchiveBytes > configuration.GetValue("Backup:Automatic:ArchiveWarningBytes", 10L * 1024 * 1024 * 1024))
            warnings.Add("恢复归档超过容量告警阈值；保留窗口或复制位置仍需要的 WAL 不会被强制删除。");
        if (status.BackupBytes > configuration.GetValue("Backup:Automatic:BackupWarningBytes", 20L * 1024 * 1024 * 1024))
            warnings.Add("备份目录超过容量告警阈值，请核对保留窗口、手动备份和未完成目录。");
        var walLimit = configuration.GetValue("Backup:Automatic:WalWarningBytes", 8L * 1024 * 1024 * 1024);
        if (status.WalBytes > walLimit || status.ReplicationRetainedBytes > walLimit)
            warnings.Add("pg_wal 或复制槽保留量超过容量告警阈值，请接回备机或明确安排重建；程序不会自行废弃复制槽。");
    }
    private static long RetainedBytes(Observation state)
    {
        var current = BackupRetentionPolicy.Lsn(state.Lsn);
        var floor = state.RequiredPositions.Select(BackupRetentionPolicy.Lsn).DefaultIfEmpty(current).Min();
        return (long)Math.Min((ulong)long.MaxValue, current >= floor ? current - floor : 0);
    }
    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool Overlaps(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        left = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        right = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return left.StartsWith(right, comparison) || right.StartsWith(left, comparison);
    }
    [GeneratedRegex("^auto-[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}$", RegexOptions.CultureInvariant)]
    private static partial Regex AutomaticName();

    private async Task<Observation> Observe(NpgsqlConnection connection, CancellationToken token)
    {
        const string sql = """
            SELECT json_build_object(
              'recovery', pg_is_in_recovery(),
              'systemId', (SELECT system_identifier::text FROM pg_control_system()),
              'timeline', (SELECT timeline_id FROM pg_control_checkpoint()),
              'segmentBytes', pg_size_bytes(current_setting('wal_segment_size')),
              'lsn', CASE WHEN pg_is_in_recovery() THEN pg_last_wal_replay_lsn()::text ELSE pg_current_wal_lsn()::text END,
              'serverId', (SELECT server_id FROM exam.server_state WHERE server_id=$1),
              'activeNodeId', (SELECT active_node_id FROM exam.server_state WHERE server_id=$1),
              'serverEpoch', (SELECT server_epoch FROM exam.server_state WHERE server_id=$1),
              'archivingEnabled', current_setting('archive_mode') IN ('on', 'always')
                AND (current_setting('archive_command') <> '' OR current_setting('archive_library') <> ''),
              'archiveFailure', EXISTS(SELECT 1 FROM pg_stat_archiver WHERE last_failed_time > COALESCE(last_archived_time, '-infinity')),
              'standbyConnected', EXISTS(SELECT 1 FROM pg_stat_replication WHERE state = 'streaming' AND replay_lsn IS NOT NULL),
              'physicalSlots', (SELECT count(*) FROM pg_replication_slots WHERE slot_type = 'physical' AND NOT temporary),
              'slotsHealthy', NOT EXISTS(SELECT 1 FROM pg_replication_slots WHERE restart_lsn IS NULL OR wal_status = 'lost' OR invalidation_reason IS NOT NULL)
                AND NOT EXISTS(SELECT 1 FROM pg_stat_replication r WHERE replay_lsn IS NULL
                    OR NOT EXISTS(SELECT 1 FROM pg_replication_slots s WHERE s.active_pid = r.pid)),
              'nativeBackupsRunning', (SELECT count(*) FROM pg_stat_progress_basebackup),
              'requiredPositions', COALESCE((SELECT json_agg(position) FROM (
                SELECT restart_lsn::text AS position FROM pg_replication_slots WHERE restart_lsn IS NOT NULL
                UNION SELECT replay_lsn::text FROM pg_stat_replication WHERE replay_lsn IS NOT NULL
              ) positions), '[]'::json)
            )::text;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(Required("Management:ServerId"));
        var json = (string?)await command.ExecuteScalarAsync(token) ?? throw new MaintenanceDeferred("无法读取数据库维护证据。");
        return JsonSerializer.Deserialize<Observation>(json, AutomaticBackupStatusFile.Json)
            ?? throw new MaintenanceDeferred("数据库维护证据无效。");
    }
    private sealed record Observation(bool Recovery, string SystemId, uint Timeline, ulong SegmentBytes, string Lsn,
        string ServerId, string ActiveNodeId, long ServerEpoch, bool ArchivingEnabled, bool ArchiveFailure, bool StandbyConnected,
        int PhysicalSlots, bool SlotsHealthy, int NativeBackupsRunning, IReadOnlyList<string> RequiredPositions);
}
