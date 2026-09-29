using Exam.Infrastructure.HighAvailability;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.HighAvailability;

public sealed class StagedPostgresNodeRebuilderTests
{
    [Theory]
    [InlineData("unknown", "node-a")]
    [InlineData("node-a", "node-a")]
    public async Task Rebuild_RejectsUnknownOrSelfTarget_BeforeCreatingStagingDirectory(
        string targetNodeId,
        string sourceNodeId)
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), $"exam-rebuild-{Guid.NewGuid():N}");
        var nodes = new Dictionary<string, PostgresNodeConnection>(StringComparer.Ordinal)
        {
            ["node-a"] = new("node-a", "Host=localhost;Username=replicator;Password=synthetic"),
            ["node-b"] = new("node-b", "Host=localhost;Username=replicator;Password=synthetic")
        };
        var runner = new RecordingProcessRunner();
        var rebuilder = new StagedPostgresNodeRebuilder(
            new StagedNodeRebuildOptions("/unused", stagingRoot, nodes, TimeSpan.FromSeconds(1)),
            runner);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            rebuilder.PrepareRebuildAsync(targetNodeId, sourceNodeId, CancellationToken.None));

        Assert.False(Directory.Exists(stagingRoot));
        Assert.False(runner.WasCalled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rebuild_RejectsUnknownOrMisidentifiedSource_BeforeCreatingStagingDirectory(
        bool sourceIsMisidentified)
    {
        var stagingRoot = Path.Combine(Path.GetTempPath(), $"exam-rebuild-{Guid.NewGuid():N}");
        var nodes = new Dictionary<string, PostgresNodeConnection>(StringComparer.Ordinal)
        {
            ["node-a"] = new(sourceIsMisidentified ? "node-c" : "node-a", "Host=localhost;Username=replicator;Password=synthetic"),
            ["node-b"] = new("node-b", "Host=localhost;Username=replicator;Password=synthetic")
        };
        var runner = new RecordingProcessRunner();
        var rebuilder = new StagedPostgresNodeRebuilder(
            new StagedNodeRebuildOptions("/unused", stagingRoot, nodes, TimeSpan.FromSeconds(1)),
            runner);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            rebuilder.PrepareRebuildAsync("node-b", sourceIsMisidentified ? "node-a" : "unknown", CancellationToken.None));

        Assert.False(Directory.Exists(stagingRoot));
        Assert.False(runner.WasCalled);
    }

    private sealed class RecordingProcessRunner : IExternalProcessRunner
    {
        public bool WasCalled { get; private set; }

        public Task<ExternalProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment,
            TimeSpan timeout,
            CancellationToken cancellationToken = default,
            string? workingDirectory = null,
            ExternalProcessLimits? limits = null)
        {
            WasCalled = true;
            return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
        }
    }
}
