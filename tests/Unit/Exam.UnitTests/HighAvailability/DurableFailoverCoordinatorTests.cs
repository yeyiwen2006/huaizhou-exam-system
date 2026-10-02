using Exam.Application.HighAvailability;
using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.HighAvailability;

public sealed class DurableFailoverCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "synthetic-f02-" + Guid.NewGuid().ToString("N"));
    private readonly FixedClock _clock = new();
    private readonly FileManagementOperationStore _store;
    private readonly Platform _platform;
    public DurableFailoverCoordinatorTests()
    { Directory.CreateDirectory(_directory); _store = new(_directory); _platform = new(_store, _clock); }
    private DurableFailoverCoordinator Coordinator(IManagementOperationStore? store = null) => new("server", "standby", _platform, store ?? _store, _clock);
    private ManagementOperationRunner Runner(IManagementOperationStore? store = null) => new("server", "standby", store ?? _store, _clock, Coordinator(store).ReconcileAsync);
    private HighAvailabilityRequest Request(string id = "synthetic-takeover") => new(id, "server", "standby", "operator", 7, 3,
        "standby", "synthetic isolation", new("primary", true, false, "operator", "synthetic isolation"));
    private Task<ManagementOperation> Execute(HighAvailabilityRequest request, ManagementOperationKind kind = ManagementOperationKind.Takeover,
        ManagementOperationRunner? runner = null, DurableFailoverCoordinator? coordinator = null) =>
        (runner ?? Runner()).ExecuteAsync(request.RequestId, "operator", kind, request,
            (operation, token) => (coordinator ?? Coordinator()).ExecuteAsync(operation, request, token));

    [Fact]
    public async Task IntentIsOnDiskBeforePromotion_AndOriginalRequestReplaysAcrossNewInstances()
    {
        var request = Request(); var first = await Execute(request);
        Assert.Equal(ManagementOperationPhase.Completed, first.Phase);
        Assert.Equal(1, _platform.Effects);
        Assert.Equal(8, first.HighAvailability!.State!.ServerEpoch);
        Assert.Equal(ProtectionMode.EmergencySingleNode, first.HighAvailability.State.ProtectionMode);
        Assert.Equal(DeploymentNodeRole.Isolated, first.HighAvailability.State.Nodes["primary"].Role);
        var replay = await Execute(request, runner: Runner(), coordinator: Coordinator());
        Assert.Equal(first.ContentHash, replay.ContentHash); Assert.Equal(1, _platform.Effects);
        Assert.Equal("operator", Assert.Single(replay.HighAvailability!.State!.Audit).ActorId);
    }

    [Fact]
    public async Task LostReplyAfterPromotionIsReconciledAfterRestartWithoutPromotingAgain()
    {
        _platform.FailAfterEffect = true;
        var request = Request(); var uncertain = await Execute(request);
        Assert.Equal(ManagementOperationPhase.NeedsVerification, uncertain.Phase);
        Assert.NotNull((await _store.FindAsync(request.RequestId, default))!.FailoverPlan);
        var verified = await Runner().FindAsync(request.RequestId, "operator", default);
        Assert.Equal(ManagementOperationPhase.Completed, verified.Phase); Assert.Null(verified.Error);
        Assert.Equal(1, _platform.Effects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidationFailureAfterDurableIntentStillBlocksWritesUntilOriginalReconciliation(bool argument)
    {
        _platform.AfterEffectError = argument ? new ArgumentException("synthetic post-effect failure") :
            new DomainRuleException(ErrorCodes.ServiceRecovering, "synthetic confirmation unavailable", true);
        var request = Request();
        var result = await Execute(request);
        Assert.Equal(ManagementOperationPhase.NeedsVerification, result.Phase);
        Assert.NotNull(result.FailoverPlan);
        Assert.True(await _store.HasUnverifiedOperationsAsync(default));
        var blocked = await Assert.ThrowsAsync<DomainRuleException>(() => Execute(Request("other-request")));
        Assert.Equal(ErrorCodes.ServiceRecovering, blocked.Code);
        var reconciled = await Runner().FindAsync(request.RequestId, "operator", default);
        Assert.Equal(ManagementOperationPhase.Completed, reconciled.Phase);
        Assert.Equal(1, _platform.Effects);
    }

    [Theory]
    [InlineData(ManagementOperationKind.Reprotect)]
    [InlineData(ManagementOperationKind.Rebuild)]
    public async Task EmergencyStandbyCanBeReprotectedOrExplicitlyRebuilt(ManagementOperationKind kind)
    {
        var active = kind == ManagementOperationKind.Reprotect ? "standby" : "primary";
        var replica = active == "primary" ? "standby" : "primary";
        _platform.State = _platform.State with { ActiveNodeId = active, ProtectionMode = ProtectionMode.EmergencySingleNode,
            Nodes = new Dictionary<string, NodeState>(_platform.State.Nodes) {
                [active] = _platform.State.Nodes[active] with { Role = DeploymentNodeRole.Primary, DatabaseInRecovery = false },
                [replica] = _platform.State.Nodes[replica] with { Role = DeploymentNodeRole.Standby, DatabaseInRecovery = true } } };
        var request = Request() with { TargetNodeId = kind == ManagementOperationKind.Reprotect ? "primary" : "standby",
            Isolation = null, ConfirmDestructiveRebuild = true };
        var result = await Execute(request, kind);
        Assert.Equal(ManagementOperationPhase.Completed, result.Phase);
        Assert.Equal(1, _platform.Effects);
    }

    [Fact]
    public async Task LostFinalDurableWriteLeavesIntentForRestartReconciliation()
    {
        var failing = new OneFailureStore(_store, 3);
        var request = Request();
        await Assert.ThrowsAsync<IOException>(() => Execute(request, runner: Runner(failing), coordinator: Coordinator(failing)));
        var persisted = await _store.FindAsync(request.RequestId, default);
        Assert.Equal(ManagementOperationPhase.Accepted, persisted!.Phase); Assert.NotNull(persisted.FailoverPlan);
        var restored = await Runner().FindAsync(request.RequestId, "operator", default);
        Assert.Equal(ManagementOperationPhase.Completed, restored.Phase); Assert.Equal(1, _platform.Effects);
    }

    [Fact]
    public async Task FailedIntentWriteHasNoPlatformEffect_AndCanBeResolvedAsNotExecuted()
    {
        var failing = new OneFailureStore(_store, 2); var request = Request();
        var uncertain = await Execute(request, runner: Runner(failing), coordinator: Coordinator(failing));
        Assert.Equal(ManagementOperationPhase.NeedsVerification, uncertain.Phase); Assert.Equal(0, _platform.Effects);
        var observed = await Runner().FindAsync(request.RequestId, "operator", default);
        Assert.Equal(ManagementOperationPhase.Failed, observed.Phase); Assert.Equal(0, _platform.Effects);
    }

    [Fact]
    public async Task InterruptedUnprovenPromotionNeverRepeatsAndBlocksDifferentMutations()
    {
        _platform.FailBeforeEffect = true; var request = Request();
        Assert.Equal(ManagementOperationPhase.NeedsVerification, (await Execute(request)).Phase);
        Assert.Equal(ManagementOperationPhase.NeedsVerification, (await Execute(request)).Phase);
        Assert.Equal(1, _platform.Attempts); Assert.Equal(0, _platform.Effects);
        var error = await Assert.ThrowsAsync<DomainRuleException>(() => Execute(Request("other-id")));
        Assert.Equal(ErrorCodes.ServiceRecovering, error.Code);
    }

    [Fact]
    public async Task TwoCoordinatorInstancesSerializeTheSameRequestOnTheActualFileLease()
    {
        var request = Request(); var results = await Task.WhenAll(Execute(request), Execute(request, runner: Runner(), coordinator: Coordinator()));
        Assert.All(results, result => Assert.Equal(ManagementOperationPhase.Completed, result.Phase)); Assert.Equal(1, _platform.Effects);
    }

    [Theory]
    [InlineData("reason")]
    [InlineData("target")]
    [InlineData("actor")]
    [InlineData("epoch")]
    public async Task ReusingAnIdWithDifferentBoundContentIsAConflict(string field)
    {
        var original = Request(); await Execute(original);
        var changed = field switch { "reason" => original with { Reason = "different" }, "target" => original with { TargetNodeId = "primary" },
            "actor" => original with { ActorId = "different" }, _ => original with { ExpectedEpoch = 8 } };
        var error = await Assert.ThrowsAsync<DomainRuleException>(() => Execute(changed));
        Assert.Equal(ErrorCodes.VersionConflict, error.Code); Assert.Equal(1, _platform.Effects);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task HeartbeatLossAndWrongIsolationIdentityNeverAuthorizePromotion(bool powerOff, bool wrongNode)
    {
        var request = Request() with { Isolation = new(wrongNode ? "standby" : "primary", powerOff, wrongNode, "operator", "synthetic isolation") };
        var result = await Execute(request);
        Assert.Equal(ManagementOperationPhase.Failed, result.Phase); Assert.Equal(0, _platform.Effects);
        Assert.Equal(ErrorCodes.NotActiveServer, result.Error!.Code);
    }

    [Theory]
    [InlineData(6, 3, ErrorCodes.StaleServerEpoch)]
    [InlineData(7, 2, ErrorCodes.VersionConflict)]
    public async Task ExpectedEpochAndVersionAreCheckedBeforeAnyEffect(long epoch, long version, string code)
    {
        var result = await Execute(Request() with { ExpectedEpoch = epoch, ExpectedVersion = version });
        Assert.Equal(ManagementOperationPhase.Failed, result.Phase); Assert.Equal(code, result.Error!.Code); Assert.Equal(0, _platform.Effects);
    }

    [Theory]
    [InlineData("lag")]
    [InlineData("keys")]
    [InlineData("certificate")]
    [InlineData("old-observation")]
    [InlineData("unknown")]
    public async Task UnverifiedMaterialsLagAndStaleObservationsBlockPromotion(string scenario)
    {
        var node = _platform.State.Nodes["standby"];
        _platform.State = _platform.State with { Nodes = new Dictionary<string, NodeState>(_platform.State.Nodes) {
            ["standby"] = scenario switch { "lag" => node with { ReplayLagBytes = 1 }, "keys" => node with { KeyMaterialReady = false },
                "certificate" => node with { CertificateReady = false }, "old-observation" => node with { ObservedAt = _clock.UtcNow.AddSeconds(-11) }, _ => node } } };
        if (scenario == "unknown") _platform.Verified = false;
        var result = await Execute(Request()); Assert.Equal(ManagementOperationPhase.Failed, result.Phase); Assert.Equal(0, _platform.Effects);
    }

    [Fact]
    public async Task AClaimedCompletedReprotectionWithoutFullPlatformEvidenceRemainsUnverified()
    {
        _platform.State = _platform.State with { ActiveNodeId = "standby", ProtectionMode = ProtectionMode.Recovering,
            Nodes = new Dictionary<string, NodeState>(_platform.State.Nodes) { ["standby"] = _platform.State.Nodes["standby"] with { Role = DeploymentNodeRole.Primary },
                ["primary"] = _platform.State.Nodes["primary"] with { Role = DeploymentNodeRole.Rebuilding } } };
        _platform.ReconcileProof = false;
        var request = Request() with { TargetNodeId = "primary", Isolation = null };
        var result = await Execute(request, ManagementOperationKind.Reprotect);
        Assert.Equal(ManagementOperationPhase.NeedsVerification, result.Phase); Assert.Equal(1, _platform.Effects);
        Assert.Equal(ManagementOperationPhase.NeedsVerification, (await Runner().FindAsync(request.RequestId, "operator", default)).Phase);
    }

    [Fact]
    public async Task QueryCannotReadAnotherAuthenticatedOperatorsOperation()
    {
        await Execute(Request()); var error = await Assert.ThrowsAsync<DomainRuleException>(() => Runner().FindAsync(Request().RequestId, "other", default));
        Assert.Equal(ErrorCodes.Forbidden, error.Code);
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow; }
    private sealed class Platform(FileManagementOperationStore store, FixedClock clock) : IHighAvailabilityManagementPlatform
    {
        public ServerDeploymentState State = new("server", "primary", 7, ProtectionMode.SynchronousDualNode,
            new Dictionary<string, NodeState> { ["primary"] = new("primary", DeploymentNodeRole.Primary, true, false, 0, true, true, clock.UtcNow),
                ["standby"] = new("standby", DeploymentNodeRole.Standby, true, true, 0, true, true, clock.UtcNow) }, 3, []);
        public bool Verified = true, FailAfterEffect, FailBeforeEffect, ReconcileProof = true;
        public Exception? AfterEffectError;
        public int Attempts, Effects;
        public Task<HighAvailabilityStatus> ObserveAsync(CancellationToken token) => Task.FromResult(new HighAvailabilityStatus("server", "standby", State,
            Verified ? clock.UtcNow : null, State.ProtectionMode == ProtectionMode.SynchronousDualNode && ReconcileProof, []));
        public Task<FailoverExecutionPlan> PrepareAsync(ManagementOperation operation, HighAvailabilityRequest request,
            ServerDeploymentState before, ServerDeploymentState after, CancellationToken token) =>
            Task.FromResult(new FailoverExecutionPlan(operation.RequestId, operation.ContentHash, operation.Kind, before, after, "synthetic-system", "synthetic-database", 1, "synthetic-digest"));
        public async Task ExecuteAsync(FailoverExecutionPlan plan, CancellationToken token)
        {
            Assert.NotNull((await store.FindAsync(plan.RequestId, token))!.FailoverPlan); Attempts++;
            if (FailBeforeEffect) throw new IOException("synthetic interrupted operation");
            State = plan.After; Effects++; if (FailAfterEffect) throw new IOException("synthetic lost response");
            if (AfterEffectError is not null) throw AfterEffectError;
        }
        public Task<bool> ReconcileAsync(FailoverExecutionPlan plan, CancellationToken token) =>
            Task.FromResult(ReconcileProof && State.Version == plan.After.Version && State.ServerEpoch == plan.After.ServerEpoch);
    }
    private sealed class OneFailureStore(IManagementOperationStore inner, int failure) : IManagementOperationStore
    {
        private int _saves;
        public Task<IAsyncDisposable> AcquireAsync(CancellationToken token) => inner.AcquireAsync(token);
        public Task<ManagementOperation?> FindAsync(string id, CancellationToken token) => inner.FindAsync(id, token);
        public Task<bool> HasUnverifiedOperationsAsync(CancellationToken token) => inner.HasUnverifiedOperationsAsync(token);
        public Task SaveAsync(ManagementOperation operation, CancellationToken token) => ++_saves == failure
            ? throw new IOException("synthetic durable write failure") : inner.SaveAsync(operation, token);
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
