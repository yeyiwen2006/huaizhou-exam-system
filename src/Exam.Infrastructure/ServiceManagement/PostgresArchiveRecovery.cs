using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Npgsql;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>A fixed replay boundary is not proof that all acknowledged business state is covered.</summary>
public sealed record PostgresArchiveRecoveryOptions(string ArchiveDirectory, ulong SystemIdentifier, uint Timeline,
    string TargetLsn, long MaximumBytes = 4L * 1024 * 1024 * 1024)
{
    public ulong Position => ParseLsn(TargetLsn);

    public void Validate()
    {
        if (!Path.IsPathFullyQualified(ArchiveDirectory) || SystemIdentifier == 0 || Timeline != 1 ||
            MaximumBytes is < 1024 * 1024 or > 16L * 1024 * 1024 * 1024 || Position == 0)
            throw new ArgumentException("归档恢复要求绝对目录、有效集群标识、原始时间线1和有界恢复范围。");
    }

    public static ulong ParseLsn(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || parts.Any(p => p.Length is < 1 or > 8 || p.Any(c => !"0123456789ABCDEF".Contains(c))) ||
            !uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var high) ||
            !uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var low))
            throw new ArgumentException("恢复LSN必须为规范十六进制位置，不能携带启动参数。");
        var result = ((ulong)high << 32) | low;
        if (value != $"{high:X}/{low:X}") throw new ArgumentException("恢复LSN不是规范位置。");
        return result;
    }
}

public sealed record RecoveryWalFile(string Name, long Length, string Sha256);
public sealed record ArchiveRecoveryBinding(string RequestId, string ActorId, string BackupId, string WorkingDirectory,
    PostgresArchiveRecoveryOptions Options, string BackupManifestSha256, IReadOnlyList<RecoveryWalFile> Files);

/// <summary>Supplements only a stopped independent working copy. Never changes the backup or trusts archive freshness.</summary>
public sealed class PostgresArchiveRecovery(PostgresArchiveRecoveryOptions options)
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();
    public PostgresArchiveRecoveryOptions Options { get; } = Validate(options);
    private static PostgresArchiveRecoveryOptions Validate(PostgresArchiveRecoveryOptions value) { value.Validate(); return value; }

    public ArchiveRecoveryBinding ReadBinding(RecoveryWorkingCopy working, RestoreRequest request)
    {
        var binding = ReadBound(working, request, "wal-recovery.json");
        var plan = ReadBound(working, request, "wal-recovery-plan.json");
        if (!SameBinding(binding, plan)) throw new InvalidDataException("归档恢复最终记录与复制前计划不匹配。");
        return binding;
    }

    /// <summary>Checks the actual promoted PostgreSQL server after replay, never infers success from files alone.</summary>
    public async Task<StartupCheckResult> VerifyReplayedAsync(RecoveryWorkingCopy working, RestoreRequest request,
        string recoveryConnection, CancellationToken token)
    {
        try
        {
            _ = ReadBinding(working, request);
            await using var source = ManagementProbeConnection.Create(recoveryConnection, 10);
            await using var command = source.CreateCommand("""
                SELECT pg_is_in_recovery(), pg_last_wal_replay_lsn()::text,
                       current_setting('recovery_target_lsn'), current_setting('recovery_target_timeline'),
                       current_setting('restore_command')
                """);
            await using var row = await command.ExecuteReaderAsync(token);
            if (!await row.ReadAsync(token) || row.GetBoolean(0) || row.IsDBNull(1) ||
                PostgresArchiveRecoveryOptions.ParseLsn(row.GetString(1)) < Options.Position ||
                row.GetString(2) != Options.TargetLsn || row.GetString(3) != Options.Timeline.ToString(CultureInfo.InvariantCulture) ||
                row.GetString(4) != "exit 1" || await row.ReadAsync(token))
                throw new InvalidDataException("实际数据库未按固定目标完成受控重放。");
            return new("recovery-wal-replay", true, "实际数据库已核对归档重放位置及固定部署边界；此位置不证明最新已确认状态。", true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new("recovery-wal-replay", false, "无法核对实际数据库的归档重放位置或原请求绑定，禁止开放业务。", true);
        }
    }

    private static bool SameBinding(ArchiveRecoveryBinding left, ArchiveRecoveryBinding right)
        => JsonSerializer.SerializeToUtf8Bytes(left, Json).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(right, Json));

    private ArchiveRecoveryBinding ReadBound(RecoveryWorkingCopy working, RestoreRequest request, string name)
    {
        Bind(working, request);
        var path = Child(working.WorkingDirectory, name);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1 or > 256 * 1024) throw new InvalidDataException("归档恢复记录大小无效。");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        var binding = JsonSerializer.Deserialize<ArchiveRecoveryBinding>(bytes, Json)
            ?? throw new InvalidDataException("归档恢复记录无效。");
        if (!bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(binding, Json)) ||
            binding.RequestId != request.RequestId || binding.ActorId != request.ActorId || binding.BackupId != request.BackupId ||
            binding.WorkingDirectory != working.WorkingDirectory || binding.Options != Options ||
            binding.BackupManifestSha256 is not { Length: 64 } || binding.Files is not { Count: > 0 and <= 1024 } ||
            binding.Files.Any(f => f is null || f.Sha256 is not { Length: 64 } || f.Name is not { Length: 24 } ||
                f.Length is < 1024 * 1024 or > 1024L * 1024 * 1024 || f.Name.Any(c => !"0123456789ABCDEF".Contains(c))))
            throw new InvalidDataException("归档恢复证据与原请求或固定部署边界不匹配。");
        var size = checked((uint)binding.Files[0].Length);
        if ((size & (size - 1)) != 0 || binding.Files.Any(file => file.Length != size) ||
            (ulong)binding.Files.Count * size > (ulong)Options.MaximumBytes)
            throw new InvalidDataException("归档恢复记录的容量或段长度无效。");
        var manifestPath = Child(Child(working.WorkingDirectory, "database"), "backup_manifest");
        using var manifestFile = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (manifestFile.Length is < 1 or > 16 * 1024 * 1024) throw new InvalidDataException("备份清单长度无效。");
        var manifestBytes = new byte[checked((int)manifestFile.Length)]; manifestFile.ReadExactly(manifestBytes);
        if (Convert.ToHexString(SHA256.HashData(manifestBytes)) != binding.BackupManifestSha256)
            throw new InvalidDataException("归档恢复绑定的备份清单已改变。");
        using var manifest = JsonDocument.Parse(manifestBytes);
        var ranges = manifest.RootElement.GetProperty("WAL-Ranges").EnumerateArray().ToArray();
        if (manifest.RootElement.GetProperty("System-Identifier").GetUInt64() != Options.SystemIdentifier || ranges.Length != 1 ||
            ranges[0].GetProperty("Timeline").GetUInt32() != Options.Timeline)
            throw new InvalidDataException("归档恢复记录的集群或时间线无效。");
        var startLsn = PostgresArchiveRecoveryOptions.ParseLsn(ranges[0].GetProperty("Start-LSN").GetString()!);
        var endLsn = PostgresArchiveRecoveryOptions.ParseLsn(ranges[0].GetProperty("End-LSN").GetString()!);
        var start = startLsn / size;
        if (startLsn >= endLsn || Options.Position <= endLsn || Options.Position / size < start ||
            Options.Position / size - start + 1 != (ulong)binding.Files.Count ||
            binding.Files.Where((file, index) => file.Name != SegmentName(start + (ulong)index, size)).Any())
            throw new InvalidDataException("归档恢复记录未完整覆盖固定目标。");
        return binding;
    }

    public async Task PrepareAsync(RecoveryWorkingCopy working, RestoreRequest request, CancellationToken token)
    {
        Bind(working, request);
        var archive = Path.GetFullPath(Options.ArchiveDirectory);
        if (!Directory.Exists(archive)) throw new DirectoryNotFoundException("归档目录必须预先部署。");
        PathBoundary.RejectExistingReparsePoints(archive, archive);
        if (Inside(archive, working.WorkingDirectory) || Inside(working.WorkingDirectory, archive) ||
            Inside(archive, working.SourceDirectory) || Inside(working.SourceDirectory, archive))
            throw new IOException("归档目录不能与恢复槽或备份原件交叠。");
        var database = Child(working.WorkingDirectory, "database");
        foreach (var name in new[] { "postmaster.pid", "standby.signal" })
            if (File.Exists(Child(database, name))) throw new IOException("运行或备用实例不能补充归档WAL。");
        await using var guard = new FileStream(Child(working.WorkingDirectory, ".wal-recovery.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var marker = Child(working.WorkingDirectory, "wal-recovery.json");
        var planPath = Child(working.WorkingDirectory, "wal-recovery-plan.json");
        var plan = File.Exists(planPath) ? ReadBound(working, request, "wal-recovery-plan.json") : null;
        var signal = Child(database, "recovery.signal");
        var previous = File.Exists(marker) ? ReadBinding(working, request) : null;
        if ((File.Exists(signal) && previous is null) || (previous is not null && !File.Exists(signal)))
            throw new IOException("归档恢复阶段无法核实，保留信号及副本，不自动重新执行恢复。");
        if (File.Exists(signal) && new FileInfo(signal).Length != 0) throw new IOException("恢复信号不是受控空文件。");

        var manifestPath = Child(database, "backup_manifest");
        byte[] manifestBytes;
        await using (var input = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (input.Length is < 1 or > 16 * 1024 * 1024) throw new InvalidDataException("物理备份清单大小无效。");
            manifestBytes = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(manifestBytes, token);
        }
        using var manifest = JsonDocument.Parse(manifestBytes);
        var root = manifest.RootElement;
        if (root.GetProperty("System-Identifier").GetUInt64() != Options.SystemIdentifier)
            throw new InvalidDataException("归档配置与基础备份不是同一集群。");
        var ranges = root.GetProperty("WAL-Ranges").EnumerateArray().ToArray();
        if (ranges.Length != 1 || ranges[0].GetProperty("Timeline").GetUInt32() != Options.Timeline)
            throw new InvalidDataException("本路径不跨时间线恢复或推断时间线历史。");
        var start = PostgresArchiveRecoveryOptions.ParseLsn(ranges[0].GetProperty("Start-LSN").GetString()!);
        var end = PostgresArchiveRecoveryOptions.ParseLsn(ranges[0].GetProperty("End-LSN").GetString()!);
        if (start >= end || Options.Position <= end) throw new InvalidDataException("归档目标必须在完整基础备份结束位置之后。");
        var walDirectory = Child(database, "pg_wal");
        var bundled = Directory.EnumerateFiles(walDirectory).FirstOrDefault(p => Path.GetFileName(p).Length == 24)
            ?? throw new InvalidDataException("基础备份没有可核实WAL段。");
        var size = await SegmentSizeAsync(bundled, token);
        var first = start / size;
        // Include the target's containing segment: PostgreSQL stops at a record start at/after the LSN.
        var last = Options.Position / size;
        if (last < first || last - first >= 1024 || (last - first + 1) * size > (ulong)Options.MaximumBytes)
            throw new InvalidDataException("归档恢复范围超出文件数量或容量上限。");
        var files = new List<RecoveryWalFile>();
        // Verify the entire required range before creating signal/marker or changing any WAL file.
        for (var number = first; number <= last; number++)
        {
            token.ThrowIfCancellationRequested();
            var name = SegmentName(number, size);
            var destination = Child(walDirectory, name);
            var source = Child(archive, name);
            if (!File.Exists(source)) source = destination;
            var file = await InspectSegmentAsync(source, name, number * size, size, token);
            if (File.Exists(destination) && await DigestAsync(destination, token) != file.Sha256)
                throw new IOException("恢复槽已有同名不同内容WAL，保留原件。");
            files.Add(file);
        }
        var binding = new ArchiveRecoveryBinding(request.RequestId, request.ActorId, request.BackupId, working.WorkingDirectory,
            Options, Convert.ToHexString(SHA256.HashData(manifestBytes)), files);
        if (plan is not null && !SameBinding(plan, binding))
            throw new IOException("已绑定归档证据发生变化，禁止替换或重放。");
        // Persist the immutable intent before any WAL copy; interrupted preparation cannot change target on retry.
        if (plan is null) await WriteNewAsync(planPath, JsonSerializer.SerializeToUtf8Bytes(binding, Json), token);
        foreach (var file in files)
        {
            var destination = Child(walDirectory, file.Name);
            if (File.Exists(destination))
            {
                if (await DigestAsync(destination, token) != file.Sha256)
                    throw new IOException("恢复槽已有同名不同内容WAL，保留原件。");
                continue;
            }
            var source = Child(archive, file.Name);
            var temporary = Child(walDirectory, "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough))
                {
                    if (input.Length != file.Length) throw new IOException("归档WAL长度变化。");
                    await CopyExactAsync(input, output, file.Length, token);
                    await output.FlushAsync(token); output.Flush(true);
                }
                if (new FileInfo(temporary).Length != file.Length || await DigestAsync(temporary, token) != file.Sha256)
                    throw new IOException("归档WAL复制期间发生变化。");
                token.ThrowIfCancellationRequested();
                File.Move(temporary, destination, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (previous is not null) return;
        token.ThrowIfCancellationRequested();
        await WriteNewAsync(signal, [], token);
        // Failure after signal creation is deliberately ambiguous and cannot silently recreate/overwrite the slot.
        await WriteNewAsync(marker, JsonSerializer.SerializeToUtf8Bytes(binding, Json), token);
    }

    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await output.WriteAsync(bytes, token); await output.FlushAsync(token); output.Flush(true);
    }

    private async Task<uint> SegmentSizeAsync(string path, CancellationToken token)
    {
        PathBoundary.RejectExistingReparsePoints(Path.GetDirectoryName(path)!, path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[40]; await input.ReadExactlyAsync(header, token);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(32));
        if (size is < 1024 * 1024 or > 1024 * 1024 * 1024 || (size & (size - 1)) != 0 || input.Length != size)
            throw new InvalidDataException("WAL段长度无效。");
        return size;
    }

    private async Task<RecoveryWalFile> InspectSegmentAsync(string path, string name, ulong address, uint size, CancellationToken token)
    {
        PathBoundary.RejectExistingReparsePoints(Path.GetDirectoryName(path)!, path);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length != size) throw new InvalidDataException("归档缺少完整WAL段。");
        var header = new byte[40]; await input.ReadExactlyAsync(header, token);
        // PostgreSQL 18, little-endian 64-bit standard WAL layout; record CRC/redo are verified by PostgreSQL itself.
        if (BinaryPrimitives.ReadUInt16LittleEndian(header) != 0xD118 ||
            (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)) & 2) == 0 ||
            (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)) & ~15) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != Options.Timeline ||
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8)) != address ||
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(24)) != Options.SystemIdentifier ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(32)) != size ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(36)) != 8192)
            throw new InvalidDataException("归档WAL的版本、集群、时间线、位置或块大小不匹配。");
        input.Position = 0;
        return new(name, input.Length, await HashExactAsync(input, input.Length, token));
    }

    private string SegmentName(ulong number, uint size)
    {
        var perId = 0x100000000UL / size;
        return $"{Options.Timeline:X8}{number / perId:X8}{number % perId:X8}";
    }
    private static async Task<string> DigestAsync(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 1024 * 1024 or > 1024L * 1024 * 1024) throw new InvalidDataException("WAL摘要读取长度无效。");
        return await HashExactAsync(input, input.Length, token);
    }
    private static async Task CopyExactAsync(Stream input, Stream output, long length, CancellationToken token)
    {
        var buffer = new byte[65536];
        for (var remaining = length; remaining > 0;)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), token);
            if (read == 0) throw new EndOfStreamException("WAL读取提前结束。");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            remaining -= read;
        }
        if (input.ReadByte() != -1) throw new IOException("WAL读取期间长度增加。");
    }
    private static async Task<string> HashExactAsync(Stream input, long length, CancellationToken token)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        for (var remaining = length; remaining > 0;)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), token);
            if (read == 0) throw new EndOfStreamException("WAL摘要读取提前结束。");
            digest.AppendData(buffer.AsSpan(0, read)); remaining -= read;
        }
        if (input.ReadByte() != -1) throw new IOException("WAL摘要读取期间长度增加。");
        return Convert.ToHexString(digest.GetHashAndReset());
    }
    private static string Child(string root, string name) => PathBoundary.ResolveWithinRoot(root, Path.Combine(root, name), "归档恢复路径越界。");
    private static bool Inside(string root, string path) => Path.GetRelativePath(root, path) is var relative &&
        (relative == "." || !Path.IsPathFullyQualified(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    private static void Bind(RecoveryWorkingCopy working, RestoreRequest request)
    {
        if (working.RequestId != request.RequestId || working.ActorId != request.ActorId || working.BackupId != request.BackupId ||
            !Path.IsPathFullyQualified(working.WorkingDirectory) || !Path.IsPathFullyQualified(working.SourceDirectory) ||
            !Directory.Exists(working.WorkingDirectory))
            throw new IOException("归档恢复工作副本与原请求不匹配。");
        PathBoundary.RejectExistingReparsePoints(working.WorkingDirectory, working.WorkingDirectory);
    }
}
