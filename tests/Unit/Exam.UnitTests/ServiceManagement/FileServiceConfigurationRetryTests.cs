using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class FileServiceConfigurationRetryTests
{
    [Fact]
    public async Task WindowsReplacementRetriesUntilReaderReleasesTarget()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "f01-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "service.json");
            var store = new FileServiceConfigurationStore(path);
            var original = await store.SaveAsync(new("server", "node", "127.0.0.1", 443, root, false, 0), 0);
            Task<ServiceConfiguration> update;
            using (var heldReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                update = store.SaveAsync(original with { HttpsPort = 7443 }, 1);
                await Task.Delay(100);
                Assert.False(update.IsCompleted);
                Assert.Equal(original, await store.LoadAsync());
            }
            var saved = await update;
            Assert.Equal(2, saved.Version);
            Assert.Equal(7443, saved.HttpsPort);
            Assert.Equal(saved, await store.LoadAsync());
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancellationWhileReplacementIsBlockedPreservesOldFileAndCleansTemporaryFile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "f01-cancel-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "service.json");
            var store = new FileServiceConfigurationStore(path);
            var original = await store.SaveAsync(new("server", "node", "127.0.0.1", 443, root, false, 0), 0);
            using var cancellation = new CancellationTokenSource();
            using (var heldReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var update = store.SaveAsync(original with { HttpsPort = 7443 }, 1, cancellation.Token);
                await Task.Delay(100);
                Assert.False(update.IsCompleted);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
            }
            Assert.Equal(original, await store.LoadAsync());
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
            // 取消后锁已释放，后续正常写入仍可完成。
            Assert.Equal(2, (await store.SaveAsync(original with { HttpsPort = 7443 }, 1)).Version);
        }
        finally { Directory.Delete(root, true); }
    }
}
