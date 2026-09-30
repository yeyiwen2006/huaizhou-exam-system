using Exam.Contracts.HighAvailability;
using Exam.Infrastructure.HighAvailability;

namespace Exam.UnitTests.HighAvailability;

public sealed class FileConfirmationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "confirmation-" + Guid.NewGuid().ToString("N"));
    public FileConfirmationStoreTests() => Directory.CreateDirectory(_root);
    private DurableConfirmation Record(string lsn = "0/100") => new(Guid.NewGuid().ToString("N"), "server", "primary", "standby",
        1, 1, ProtectionMode.SynchronousDualNode, "12345", "exam", 1, lsn, new string('A', 64));

    [Fact]
    public async Task PendingSurvivesRestartAndCannotBeConfusedWithConfirmedHead()
    {
        var store = new FileConfirmationStore(_root); var record = Record();
        using (store.Lock()) await store.SaveAsync(new(record, false), default);
        Assert.Equal(new(record, false), new FileConfirmationStore(_root).Read());
        using (store.Lock()) await store.SaveAsync(new(record, true), default);
        Assert.Equal(new(record, true), new FileConfirmationStore(_root).Read());
    }

    [Theory]
    [InlineData("position")]
    [InlineData("database")]
    [InlineData("epoch")]
    [InlineData("node")]
    public async Task EarlierOrUnrelatedBoundaryCannotReplaceHead(string change)
    {
        var store = new FileConfirmationStore(_root); var record = Record();
        using var locked = store.Lock(); await store.SaveAsync(new(record, true), default);
        var wrong = change switch { "position" => record with { FlushLsn = "0/FF" },
            "database" => record with { DatabaseName = "other" }, "epoch" => record with { Epoch = 2 },
            _ => record with { StandbyNodeId = "other" } };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new(wrong, true), default));
        Assert.Equal(new(record, true), store.Read());
    }

    [Fact]
    public async Task InterruptedWriteAndCorruptionNeverInventConfirmation()
    {
        var store = new FileConfirmationStore(_root); var record = Record();
        using var locked = store.Lock(); await store.SaveAsync(new(record, false), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new(record, true), new(true)));
        Assert.False(store.Read()!.Confirmed);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
        await File.WriteAllTextAsync(Path.Combine(_root, "confirmation-head.json"), "{}");
        Assert.ThrowsAny<Exception>(() => store.Read());
    }

    [Fact]
    public async Task ResourceDigestBindsNamesAndContentsAndDetectsMissingFiles()
    {
        var before = FileConfirmationStore.ResourcesDigest(_root);
        var path = Path.Combine(_root, "synthetic-source.tex"); await File.WriteAllTextAsync(path, "synthetic only");
        var saved = FileConfirmationStore.ResourcesDigest(_root); Assert.NotEqual(before, saved);
        File.Move(path, Path.Combine(_root, "other.tex")); Assert.NotEqual(saved, FileConfirmationStore.ResourcesDigest(_root));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
