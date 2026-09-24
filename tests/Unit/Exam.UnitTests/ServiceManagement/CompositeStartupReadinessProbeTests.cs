using Exam.Application.ServiceManagement;
using Exam.Contracts.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class CompositeStartupReadinessProbeTests
{
    [Fact]
    public async Task AggregatesAllChecksIncludingBlockingFailures()
    {
        var probe = new CompositeStartupReadinessProbe(
        [
            new StubProbe([new("storage", true, "ok", true)]),
            new StubProbe([new("database", false, "unavailable", true)])
        ]);

        var results = await probe.RunAsync(Configuration(), CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("storage", results[0].Name);
        Assert.False(results[1].Passed);
        Assert.True(results[1].BlocksWrites);
    }

    [Fact]
    public async Task EmptyChildProbe_IsNotTreatedAsPassed()
    {
        var probe = new CompositeStartupReadinessProbe(
            [new StubProbe([new("storage", true, "ok", true)]), new StubProbe([])]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            probe.RunAsync(Configuration(), CancellationToken.None));
    }

    private static ServiceConfiguration Configuration() =>
        new("synthetic-server", "synthetic-node", "127.0.0.1", 443,
            Path.GetTempPath(), false, 1);

    private sealed class StubProbe(IReadOnlyList<StartupCheckResult> checks) : IStartupReadinessProbe
    {
        public Task<IReadOnlyList<StartupCheckResult>> RunAsync(
            ServiceConfiguration configuration,
            CancellationToken cancellationToken) => Task.FromResult(checks);
    }
}
