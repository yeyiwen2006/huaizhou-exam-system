using Exam.Infrastructure.ServiceManagement;

namespace Exam.IntegrationTests.ServiceManagement;

public sealed class BackupRetentionPolicyTests
{
    [Fact]
    public void KeepIncludesBackupBeforeWindowAndAtLeastTwoBackups()
    {
        var now = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        var backups = Enumerable.Range(0, 12).Select(i =>
            new RetainedBackup(i.ToString(), i.ToString(), now.AddDays(-i).AddHours(-1), "", "cluster", true)).ToArray();
        var keep = BackupRetentionPolicy.Keep(backups, now, 7);
        Assert.Equal(8, keep.Count);
        Assert.Contains(backups[7], keep);
        Assert.DoesNotContain(backups[8], keep);
        Assert.Equal(2, BackupRetentionPolicy.Keep(backups.TakeLast(2), now, 7).Count);
    }

    [Fact]
    public void ArchiveCleanupPreservesBoundaryOtherTimelinesAndHistory()
    {
        const ulong size = 16 * 1024 * 1024;
        var names = Enumerable.Range(2, 5).Select(n => BackupRetentionPolicy.WalName(1, (ulong)n, size)).ToList();
        names.AddRange([BackupRetentionPolicy.WalName(2, 1, size), "00000002.history", names[0] + ".00000028.backup"]);
        Assert.Equal([names[0]], BackupRetentionPolicy.ObsoleteWal(names, 1, size, 3, 7));
    }

    [Fact]
    public void MissingRequiredWalStopsCleanup()
    {
        const ulong size = 16 * 1024 * 1024;
        var names = new ulong[] { 1, 2, 3, 5 }.Select(n => BackupRetentionPolicy.WalName(1, n, size));
        Assert.Throws<InvalidDataException>(() => BackupRetentionPolicy.ObsoleteWal(names, 1, size, 3, 6));
    }

    [Fact]
    public void WalPositionHandlesLogRollover()
    {
        const ulong size = 16 * 1024 * 1024;
        var name = BackupRetentionPolicy.WalName(1, 256, size);
        Assert.Equal("000000010000000100000000", name);
        Assert.Equal(256UL, BackupRetentionPolicy.Segment(name, size));
    }

    [Fact]
    public void CleanupCannotTargetRootOrEscapeIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "exam-retention-boundary");
        Assert.Throws<InvalidDataException>(() => BackupRetentionPolicy.Child(root, root));
        Assert.Throws<InvalidDataException>(() => BackupRetentionPolicy.Child(root, Path.Combine(root, "..", "unrelated")));
    }
}
