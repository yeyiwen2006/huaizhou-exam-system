using System.Data;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class FileServiceConfigurationStoreTests
{
    [Fact]
    public async Task Save_Reopen_AndRejectStaleUpdate()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "service.json");
        var store = new FileServiceConfigurationStore(path);
        var original = Configuration();

        var saved = await store.SaveAsync(original, expectedVersion: 0);
        var reopened = new FileServiceConfigurationStore(path);
        Assert.Equal(1, saved.Version);
        Assert.Equal(saved, await reopened.LoadAsync());

        var updated = await reopened.SaveAsync(saved with { HttpsPort = 7443 }, expectedVersion: 1);
        Assert.Equal(2, updated.Version);
        Assert.Equal(7443, (await store.LoadAsync()).HttpsPort);
        await Assert.ThrowsAsync<DBConcurrencyException>(() =>
            store.SaveAsync(saved with { HttpsPort = 8443 }, expectedVersion: 1));
        Assert.Equal(updated, await store.LoadAsync());
    }

    [Fact]
    public async Task ConcurrentWriters_OnlyOneVersionWins()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "service.json");
        var first = new FileServiceConfigurationStore(path);
        var second = new FileServiceConfigurationStore(path);
        var saved = await first.SaveAsync(Configuration(), expectedVersion: 0);

        var changes = await Task.WhenAll(
            CaptureAsync(() => first.SaveAsync(saved with { HttpsPort = 7443 }, expectedVersion: 1)),
            CaptureAsync(() => second.SaveAsync(saved with { HttpsPort = 8443 }, expectedVersion: 1)));

        Assert.Single(changes, result => result is ServiceConfiguration);
        Assert.Single(changes, result => result is DBConcurrencyException);
        var persisted = await first.LoadAsync();
        Assert.Equal(2, persisted.Version);
        Assert.Contains(persisted.HttpsPort, new[] { 7443, 8443 });
    }

    [Fact]
    public async Task CorruptConfiguration_IsRetainedAndNeverTreatedAsNew()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "service.json");
        await File.WriteAllTextAsync(path, "{not-json");
        var store = new FileServiceConfigurationStore(path);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(Configuration(), expectedVersion: 0));
        Assert.Equal("{not-json", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task InvalidUpdate_DoesNotReplaceReliableVersionOrNodeIdentity()
    {
        using var directory = new TemporaryDirectory();
        var store = new FileServiceConfigurationStore(Path.Combine(directory.Path, "service.json"));
        var saved = await store.SaveAsync(Configuration(), expectedVersion: 0);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.SaveAsync(saved with { DataDirectory = "relative" }, expectedVersion: 1));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.SaveAsync(saved with { NodeId = "other-node" }, expectedVersion: 1));
        Assert.Equal(saved, await store.LoadAsync());
    }

    [Fact]
    public async Task MissingConfigurationAndCancelledSave_DoNotCreateOrReplaceFile()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "service.json");
        var store = new FileServiceConfigurationStore(path);
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.LoadAsync());

        var saved = await store.SaveAsync(Configuration(), expectedVersion: 0);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(saved with { HttpsPort = 7443 }, expectedVersion: 1, cancelled.Token));
        Assert.Equal(saved, await store.LoadAsync());
    }

    private static ServiceConfiguration Configuration() =>
        new("synthetic-server", "synthetic-node", "127.0.0.1", 443,
            Path.Combine(Path.GetTempPath(), "synthetic-exam-data"), false, 0);

    private static async Task<object> CaptureAsync(Func<Task<ServiceConfiguration>> action)
    {
        try
        {
            return await action();
        }
        catch (DBConcurrencyException exception)
        {
            return exception;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "exam-f01-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
