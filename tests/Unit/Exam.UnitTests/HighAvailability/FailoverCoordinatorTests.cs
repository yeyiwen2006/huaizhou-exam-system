using Exam.Application.HighAvailability;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Domain.Common;

namespace Exam.UnitTests.HighAvailability;

public sealed class FailoverCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Takeover_RequiresIsolationEvidence()
    {
        var coordinator = CreateCoordinator();
        var request = new TakeoverRequest(
            "request-1",
            "node-b",
            7,
            new IsolationEvidence("node-a", false, false, "admin", "未隔离"));

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => coordinator.TakeOverAsync(request));

        Assert.Equal(ErrorCodes.NotActiveServer, error.Code);
        Assert.Equal("node-a", coordinator.GetState().ActiveNodeId);
    }

    [Fact]
    public async Task Takeover_PromotesCaughtUpStandby_Once()
    {
        var platform = new FakePlatform();
        var coordinator = CreateCoordinator(platform);
        var request = new TakeoverRequest(
            "request-1",
            "node-b",
            7,
            new IsolationEvidence("node-a", true, false, "admin", "原主机已断电"));

        var first = await coordinator.TakeOverAsync(request);
        var replay = await coordinator.TakeOverAsync(request);

        Assert.Equal("node-b", first.ActiveNodeId);
        Assert.Equal(8, first.ServerEpoch);
        Assert.Equal(ProtectionMode.EmergencySingleNode, first.ProtectionMode);
        Assert.Equal(DeploymentNodeRole.Isolated, first.Nodes["node-a"].Role);
        Assert.Equal(first, replay);
        Assert.Equal(1, platform.PromoteCount);
    }

    [Fact]
    public async Task Takeover_RejectsReplicaWithReplayLag()
    {
        var coordinator = CreateCoordinator();
        coordinator.ObserveNode(Standby() with { ReplayLagBytes = 1, ObservedAt = Now.AddSeconds(1) });

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => coordinator.TakeOverAsync(
            new TakeoverRequest(
                "request-2",
                "node-b",
                7,
                new IsolationEvidence("node-a", false, true, "admin", "业务网络已隔离"))));

        Assert.Equal(ErrorCodes.ReplicaUnavailable, error.Code);
    }

    [Fact]
    public async Task Rebuild_CannotTargetCurrentPrimary()
    {
        var coordinator = CreateCoordinator();

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => coordinator.BeginRebuildAsync(
            new RebuildNodeRequest("rebuild-1", "node-a", "admin", 7)));

        Assert.Equal(ErrorCodes.InvalidInput, error.Code);
    }

    [Fact]
    public async Task Takeover_IsBlocked_WhenEvidenceNamesAnotherNode()
    {
        var coordinator = CreateCoordinator();

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => coordinator.TakeOverAsync(
            new TakeoverRequest(
                "request-wrong-node",
                "node-b",
                7,
                new IsolationEvidence("node-x", true, false, "admin", "隔离的不是当前主机"))));

        Assert.Equal(ErrorCodes.NotActiveServer, error.Code);
    }

    [Fact]
    public async Task Rebuild_ReturnsToSynchronousProtection_OnlyAfterReplicaIsReady()
    {
        var platform = new FakePlatform();
        var coordinator = CreateCoordinator(platform);
        await coordinator.TakeOverAsync(new TakeoverRequest(
            "takeover",
            "node-b",
            7,
            new IsolationEvidence("node-a", true, false, "admin", "原主机已断电")));
        await coordinator.BeginRebuildAsync(new RebuildNodeRequest("begin", "node-a", "admin", 8));

        var notReady = await Assert.ThrowsAsync<DomainRuleException>(() => coordinator.CompleteRebuildAsync(
            new CompleteRebuildRequest("complete-early", "node-a", "admin", 8)));
        coordinator.ObserveNode(new NodeState(
            "node-a",
            DeploymentNodeRole.Primary,
            true,
            true,
            0,
            true,
            true,
            Now.AddSeconds(1)));
        var completed = await coordinator.CompleteRebuildAsync(
            new CompleteRebuildRequest("complete", "node-a", "admin", 8));

        Assert.Equal(ErrorCodes.ReplicaUnavailable, notReady.Code);
        Assert.Equal(ProtectionMode.SynchronousDualNode, completed.ProtectionMode);
        Assert.Equal(DeploymentNodeRole.Standby, completed.Nodes["node-a"].Role);
    }

    [Fact]
    public void Observation_CannotChangeConfiguredRole_OrApplyOlderHealth()
    {
        var coordinator = CreateCoordinator();
        coordinator.ObserveNode(Standby() with
        {
            Role = DeploymentNodeRole.Primary,
            ReplayLagBytes = 42,
            ObservedAt = Now.AddSeconds(1)
        });
        coordinator.ObserveNode(Standby() with
        {
            ReplayLagBytes = 0,
            ObservedAt = Now.AddMilliseconds(500)
        });

        var observed = coordinator.GetState().Nodes["node-b"];

        Assert.Equal(DeploymentNodeRole.Standby, observed.Role);
        Assert.Equal(42, observed.ReplayLagBytes);
    }

    private static FailoverCoordinator CreateCoordinator(FakePlatform? platform = null) =>
        new(
            "server-1",
            "node-a",
            7,
            [
                new NodeState("node-a", DeploymentNodeRole.Primary, true, false, 0, true, true, Now),
                Standby()
            ],
            platform ?? new FakePlatform(),
            new FakeClock());

    private static NodeState Standby() =>
        new("node-b", DeploymentNodeRole.Standby, true, true, 0, true, true, Now);

    private sealed class FakePlatform : IFailoverPlatform
    {
        public int PromoteCount { get; private set; }

        public Task PromoteStandbyAsync(string nodeId, CancellationToken cancellationToken)
        {
            PromoteCount++;
            return Task.CompletedTask;
        }

        public Task BeginRebuildAsync(string nodeId, string sourceNodeId, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }
}
