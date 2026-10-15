using Exam.Infrastructure.HighAvailability;

namespace Exam.UnitTests.HighAvailability;

public sealed class ResourceDirectoryDigestCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exam-resource-cache-" + Guid.NewGuid().ToString("N"));
    public ResourceDirectoryDigestCacheTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void UnchangedDirectoryReusesContentsAcrossConfirmationCalls()
    {
        File.WriteAllText(Path.Combine(_root, "paper.bin"), "synthetic");
        var scans = 0;
        using var cache = new ResourceDirectoryDigestCache(_root, _ => (++scans).ToString());
        Assert.Equal("1", cache.Read());
        for (var index = 0; index < 5; index++) Assert.Equal("1", cache.Read());
        Assert.Equal(1, scans);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("replace")]
    [InlineData("rename")]
    [InlineData("delete")]
    public async Task FileChangesInvalidateCachedContents(string change)
    {
        var path = Path.Combine(_root, "paper.bin");
        File.WriteAllText(path, "synthetic");
        var scans = 0;
        using var cache = new ResourceDirectoryDigestCache(_root, _ => (++scans).ToString());
        Assert.Equal("1", cache.Read());
        var observed = cache.ChangesObserved;
        switch (change)
        {
            case "add": File.WriteAllText(Path.Combine(_root, "extra.bin"), "new"); break;
            case "replace": File.WriteAllText(path, "replacement"); break;
            case "rename": File.Move(path, Path.Combine(_root, "renamed.bin")); break;
            case "delete": File.Delete(path); break;
        }
        await ChangedAsync(cache, observed);
        Assert.NotEqual("1", ReadAfterEvents(cache));
    }

    [Fact]
    public async Task NotificationsInvalidateSameLengthReplacementWithRestoredTimestamp()
    {
        var path = Path.Combine(_root, "paper.bin");
        File.WriteAllText(path, "first");
        var written = File.GetLastWriteTimeUtc(path);
        var scans = 0;
        using var cache = new ResourceDirectoryDigestCache(_root, _ => (++scans).ToString());
        Assert.Equal("1", cache.Read());
        var observed = cache.ChangesObserved;
        File.WriteAllText(path, "other");
        File.SetLastWriteTimeUtc(path, written);
        await ChangedAsync(cache, observed);
        Assert.NotEqual("1", ReadAfterEvents(cache));
    }

    [Fact]
    public void FailedContentReadIsNotCached()
    {
        var scans = 0;
        using var cache = new ResourceDirectoryDigestCache(_root,
            _ => ++scans == 1 ? throw new IOException("Synthetic read failure") : "verified");
        Assert.Throws<IOException>(() => cache.Read());
        Assert.Equal("verified", cache.Read());
        Assert.Equal("verified", cache.Read());
        Assert.Equal(2, scans);
    }

    private static async Task ChangedAsync(ResourceDirectoryDigestCache cache, long previous)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (cache.ChangesObserved == previous) await Task.Delay(10, timeout.Token);
    }

    private static string ReadAfterEvents(ResourceDirectoryDigestCache cache)
    {
        // File operations can generate more than one notification; an overlapping observation
        // must be retried instead of being cached as successful.
        for (var attempt = 0; ; attempt++)
        {
            try { return cache.Read(); }
            catch (IOException) when (attempt < 10) { Thread.Yield(); }
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
