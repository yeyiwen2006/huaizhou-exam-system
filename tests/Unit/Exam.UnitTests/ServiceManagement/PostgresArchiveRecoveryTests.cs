using System.Buffers.Binary;
using System.Text.Json;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class PostgresArchiveRecoveryTests : IDisposable
{
    private const uint SegmentSize = 1024 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "archive-recovery-" + Guid.NewGuid().ToString("N"));
    private string Working => Path.Combine(_root, "working", "slot");
    private string Database => Path.Combine(Working, "database");
    private string Archive => Path.Combine(_root, "archive");
    private readonly RestoreRequest _request = new("restore", "backup", "synthetic", "operator");
    private RecoveryWorkingCopy Copy => new(_request.RequestId, _request.ActorId, _request.BackupId,
        Path.Combine(_root, "backups", "snapshot"), Working, DateTimeOffset.UtcNow);
    private PostgresArchiveRecoveryOptions Options => new(Archive, 1234, 1, "0/300028");
    public PostgresArchiveRecoveryTests()
    {
        Directory.CreateDirectory(Path.Combine(Database, "pg_wal"));
        Directory.CreateDirectory(Archive);
        File.WriteAllText(Path.Combine(Database, "backup_manifest"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["System-Identifier"] = 1234UL,
            ["WAL-Ranges"] = new[] { new Dictionary<string, object> { ["Timeline"] = 1, ["Start-LSN"] = "0/100028", ["End-LSN"] = "0/200028" } }
        }));
        for (uint number = 1; number <= 3; number++)
        {
            var path = Path.Combine(Archive, Name(number));
            WriteSegment(path, number);
            if (number < 3) File.Copy(path, Path.Combine(Database, "pg_wal", Name(number)));
        }
    }

    [Fact]
    public async Task CopiesCompleteBoundRangePreservesSourceAndReplaysWithoutRecopying()
    {
        var snapshot = File.ReadAllBytes(Path.Combine(Database, "backup_manifest"));
        var replay = new PostgresArchiveRecovery(Options);
        await replay.PrepareAsync(Copy, _request, default);
        Assert.True(File.Exists(Path.Combine(Database, "pg_wal", Name(3))));
        Assert.Empty(File.ReadAllBytes(Path.Combine(Database, "recovery.signal")));
        var binding = replay.ReadBinding(Copy, _request);
        Assert.Equal(3, binding.Files.Count);
        Assert.Equal(Options, binding.Options);
        var marker = File.ReadAllBytes(Path.Combine(Working, "wal-recovery.json"));
        await replay.PrepareAsync(Copy, _request, default);
        Assert.Equal(marker, File.ReadAllBytes(Path.Combine(Working, "wal-recovery.json")));
        Assert.Equal(snapshot, File.ReadAllBytes(Path.Combine(Database, "backup_manifest")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Archive, Name(3))), File.ReadAllBytes(Path.Combine(Database, "pg_wal", Name(3))));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("short")]
    [InlineData("cluster")]
    [InlineData("timeline")]
    [InlineData("address")]
    [InlineData("version")]
    [InlineData("block")]
    [InlineData("manifest-cluster")]
    [InlineData("capacity")]
    public async Task InvalidRangeCannotCreateRecoverySignalOrFinalBinding(string scenario)
    {
        var path = Path.Combine(Archive, Name(3));
        var options = Options;
        if (scenario == "missing") File.Delete(path);
        if (scenario == "short") { using var file = File.OpenWrite(path); file.SetLength(40); }
        if (scenario is "cluster" or "timeline" or "address" or "version" or "block")
        {
            using var file = File.OpenWrite(path);
            file.Position = scenario switch { "cluster" => 24, "timeline" => 4, "address" => 8, "block" => 36, _ => 0 };
            file.WriteByte(255);
        }
        if (scenario == "manifest-cluster") options = options with { SystemIdentifier = 5678 };
        if (scenario == "capacity") options = options with { MaximumBytes = SegmentSize };
        if (scenario == "missing")
            await Assert.ThrowsAnyAsync<IOException>(() => new PostgresArchiveRecovery(options).PrepareAsync(Copy, _request, default));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => new PostgresArchiveRecovery(options).PrepareAsync(Copy, _request, default));
        Assert.False(File.Exists(Path.Combine(Database, "recovery.signal")));
        Assert.False(File.Exists(Path.Combine(Working, "wal-recovery.json")));
    }

    [Fact]
    public async Task MissingMiddleSegmentBlocksDespiteHavingLatestFile()
    {
        File.Delete(Path.Combine(Archive, Name(2)));
        File.Delete(Path.Combine(Database, "pg_wal", Name(2)));
        await Assert.ThrowsAnyAsync<IOException>(() => new PostgresArchiveRecovery(Options).PrepareAsync(Copy, _request, default));
        Assert.False(File.Exists(Path.Combine(Database, "pg_wal", Name(3))));
    }

    [Fact]
    public async Task SameNameDifferentSlotWalIsNeverOverwritten()
    {
        var path = Path.Combine(Database, "pg_wal", Name(2));
        using (var file = File.OpenWrite(path)) { file.Position = 64; file.WriteByte(99); }
        var original = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<IOException>(() => new PostgresArchiveRecovery(Options).PrepareAsync(Copy, _request, default));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(Path.Combine(Database, "recovery.signal")));
    }

    [Theory]
    [InlineData("postmaster.pid")]
    [InlineData("standby.signal")]
    [InlineData("recovery.signal")]
    public async Task UnboundExistingSignalsOrProcessFilesArePreserved(string name)
    {
        var path = Path.Combine(Database, name);
        await File.WriteAllTextAsync(path, "synthetic-existing");
        await Assert.ThrowsAsync<IOException>(() => new PostgresArchiveRecovery(Options).PrepareAsync(Copy, _request, default));
        Assert.Equal("synthetic-existing", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ChangedBoundaryActorAndCompletedSignalCannotRestartReplay()
    {
        var replay = new PostgresArchiveRecovery(Options);
        await replay.PrepareAsync(Copy, _request, default);
        Assert.Throws<InvalidDataException>(() => new PostgresArchiveRecovery(Options with { TargetLsn = "0/300030" }).ReadBinding(Copy, _request));
        Assert.Throws<IOException>(() => replay.ReadBinding(Copy, _request with { ActorId = "other" }));
        File.Delete(Path.Combine(Database, "recovery.signal"));
        // PostgreSQL removes the signal when recovery finishes; do not recreate it on a stopped replay.
        await Assert.ThrowsAsync<IOException>(() => replay.PrepareAsync(Copy, _request, default));
    }

    [Fact]
    public async Task CancellationBeforeCopyHasNoSignalOrBinding()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PostgresArchiveRecovery(Options).PrepareAsync(Copy, _request, cancellation.Token));
        Assert.False(File.Exists(Path.Combine(Database, "recovery.signal")));
        Assert.False(File.Exists(Path.Combine(Working, "wal-recovery.json")));
    }

    [Fact]
    public async Task InterruptedWalCopyKeepsOriginalPlanAndRejectsChangedTarget()
    {
        var destination = Path.Combine(Database, "pg_wal", Name(3));
        Directory.CreateDirectory(destination);
        await Assert.ThrowsAnyAsync<IOException>(() => new PostgresArchiveRecovery(Options).PrepareAsync(Copy, _request, default));
        var planPath = Path.Combine(Working, "wal-recovery-plan.json");
        var plan = File.ReadAllBytes(planPath);
        Assert.False(File.Exists(Path.Combine(Database, "recovery.signal")));
        await Assert.ThrowsAsync<InvalidDataException>(() => new PostgresArchiveRecovery(Options with { TargetLsn = "0/400028" })
            .PrepareAsync(Copy, _request, default));
        Assert.Equal(plan, File.ReadAllBytes(planPath));
        Assert.True(Directory.Exists(destination));
        Directory.Delete(destination);
        await new PostgresArchiveRecovery(Options).PrepareAsync(Copy, _request, default);
        Assert.Equal(plan, File.ReadAllBytes(planPath));
        Assert.Equal(Options, new PostgresArchiveRecovery(Options).ReadBinding(Copy, _request).Options);
    }

    [Fact]
    public async Task FinalBindingWriteFailurePreservesSignalAndBlocksBlindRestart()
    {
        Directory.CreateDirectory(Path.Combine(Working, "wal-recovery.json"));
        var replay = new PostgresArchiveRecovery(Options);
        await Assert.ThrowsAnyAsync<IOException>(() => replay.PrepareAsync(Copy, _request, default));
        Assert.True(File.Exists(Path.Combine(Database, "recovery.signal")));
        Assert.True(Directory.Exists(Path.Combine(Working, "wal-recovery.json")));
        await Assert.ThrowsAsync<IOException>(() => replay.PrepareAsync(Copy, _request, default));
    }

    [Theory]
    [InlineData("0/1 -c unsafe")]
    [InlineData("0/00000001")]
    [InlineData("0/abcdef")]
    [InlineData("0/FFFFFFFFF")]
    public void InvalidDeploymentLsnCannotBecomeServiceArguments(string lsn)
        => Assert.Throws<ArgumentException>(() => new PostgresArchiveRecovery(Options with { TargetLsn = lsn }));

    private static string Name(uint number) => $"0000000100000000{number:X8}";
    private static void WriteSegment(string path, uint number)
    {
        using var file = File.Create(path);
        file.SetLength(SegmentSize);
        var header = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(header, 0xD118);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), (ulong)number * SegmentSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), 1234);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), SegmentSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), 8192);
        file.Write(header);
    }
    public void Dispose() => Directory.Delete(_root, true);
}
