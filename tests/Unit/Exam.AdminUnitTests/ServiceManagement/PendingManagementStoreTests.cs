using System.IO;
using System.Text.Json;
using Exam.Admin.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;

namespace Exam.AdminUnitTests.ServiceManagement;

public sealed class PendingManagementStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "f04-pending-" + Guid.NewGuid().ToString("N"));
    private static PendingManagementRequest Request(string id = "synthetic-request")
    {
        var target = new ManagementTarget("https://localhost:9443/", "synthetic-server", "synthetic-node");
        var body = JsonSerializer.Serialize(new BackupRequest(id, "C:\\synthetic-backup", "synthetic-operator"), JsonDefaults.Create());
        return PendingManagementRequest.Create(target, "synthetic-operator", ManagementOperationKind.Backup, id, body);
    }
    [Fact]
    public void EncryptedPendingSurvivesStoreRestartWithoutCredentialOrPlaintext()
    {
        var request = Request(); var store = new ProtectedPendingManagementStore(_root); store.Save(request);
        var bytes = File.ReadAllBytes(Assert.Single(Directory.GetFiles(_root, "*.pending")));
        Assert.DoesNotContain("synthetic-operator", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("synthetic-backup", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Equal(request, new ProtectedPendingManagementStore(_root).Load(request.Target));
        store.Clear(request); Assert.Null(store.Load(request.Target));
    }
    [Fact]
    public void SameNodeCannotReplaceUnresolvedRequestWithAnotherIdOrBody()
    {
        var request = Request(); var store = new ProtectedPendingManagementStore(_root); store.Save(request);
        Assert.Throws<InvalidOperationException>(() => new ProtectedPendingManagementStore(_root).Save(Request("different")));
        Assert.Equal(request, store.Load(request.Target));
    }
    [Fact]
    public async Task ConcurrentGuiInstancesCanPersistOnlyOneOriginalNodeRequest()
    {
        var first = Request("synthetic-first"); var second = Request("synthetic-second");
        using var ready = new CountdownEvent(2); using var start = new ManualResetEventSlim();
        var tasks = new[] { first, second }.Select(request => Task.Run(() =>
        {
            ready.Signal();
            if (!start.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("synthetic barrier");
            try { new ProtectedPendingManagementStore(_root).Save(request); return true; }
            catch (Exception error) when (error is IOException or InvalidOperationException) { return false; }
        })).ToArray();
        try { Assert.True(ready.Wait(TimeSpan.FromSeconds(5))); }
        finally { start.Set(); }
        var results = await Task.WhenAll(tasks); Assert.Single(results, value => value);
        var persisted = new ProtectedPendingManagementStore(_root).Load(first.Target);
        Assert.True(persisted == first || persisted == second);
    }
    [Fact]
    public void CorruptProtectedFileFailsClosedAndIsPreserved()
    {
        var request = Request(); var store = new ProtectedPendingManagementStore(_root); store.Save(request);
        var path = Assert.Single(Directory.GetFiles(_root, "*.pending")); File.WriteAllBytes(path, [1, 2, 3]);
        Assert.ThrowsAny<Exception>(() => store.Load(request.Target)); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }
    [Fact]
    public void BodyHashAndOriginalIdAreValidatedBeforeStoreWrite()
    {
        var request = Request(); var store = new ProtectedPendingManagementStore(_root);
        Assert.Throws<InvalidDataException>(() => store.Save(request with { BodyHash = "different" }));
        Assert.Throws<InvalidDataException>(() => store.Save(request with { RequestId = "different" }));
        Assert.False(Directory.Exists(_root));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
