using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class LocalStorageStartupReadinessProbeTests
{
    [Fact]
    public async Task ExistingWritableDirectory_PassesAndLeavesNoProbeFile()
    {
        using var directory = new TemporaryDirectory();
        var probe = new LocalStorageStartupReadinessProbe(1);

        var results = await probe.RunAsync(Configuration(directory.Path), CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.True(result.Passed, result.Message));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task MissingDirectory_BlocksWithoutCreatingIt()
    {
        using var parent = new TemporaryDirectory();
        var missing = Path.Combine(parent.Path, "missing");
        var probe = new LocalStorageStartupReadinessProbe(1);

        var results = await probe.RunAsync(Configuration(missing), CancellationToken.None);

        Assert.All(results, result =>
        {
            Assert.False(result.Passed);
            Assert.True(result.BlocksWrites);
        });
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task InsufficientSpace_BlocksEvenWhenDirectoryIsWritable()
    {
        using var directory = new TemporaryDirectory();
        var probe = new LocalStorageStartupReadinessProbe(long.MaxValue);

        var results = await probe.RunAsync(Configuration(directory.Path), CancellationToken.None);

        Assert.False(Assert.Single(results, result => result.Name == "data-free-space").Passed);
        Assert.True(Assert.Single(results, result => result.Name == "data-write").Passed);
    }

    private static ServiceConfiguration Configuration(string dataDirectory) =>
        new("synthetic-server", "synthetic-node", "127.0.0.1", 443,
            dataDirectory, false, 1);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "exam-f01-storage-" + Guid.NewGuid().ToString("N"));

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
