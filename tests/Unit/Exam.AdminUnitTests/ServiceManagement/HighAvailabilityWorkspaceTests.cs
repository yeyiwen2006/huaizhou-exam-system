using System.Net;
using System.IO;
using System.Text.Json;
using Exam.Admin.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Contracts.ServiceManagement;

namespace Exam.AdminUnitTests.ServiceManagement;

public sealed class HighAvailabilityWorkspaceTests
{
    [Fact]
    public async Task UnknownProtectionDisablesHaActionsAndNeverTreatsMissingLagAsZero()
    {
        var connection = new HaGuiConnection { Unavailable = true }; using var model = Model(connection);
        await model.LoginAsync("synthetic"); Assert.Empty(model.Nodes); Assert.False(model.TakeoverCommand.CanExecute(null));
        await Assert.ThrowsAsync<ArgumentException>(() => model.RequestAsync(ManagementOperationKind.Takeover));
        Assert.Equal(0, connection.Writes); Assert.Contains("未核实", model.ProtectionText);
    }

    [Fact]
    public async Task HeartbeatLossWithoutPhysicalIsolationCannotCreateTakeoverRequest()
    {
        var connection = new HaGuiConnection(); using var model = Model(connection); await model.LoginAsync("synthetic");
        await Assert.ThrowsAsync<ArgumentException>(() => model.RequestAsync(ManagementOperationKind.Takeover)); Assert.Equal(0, connection.Writes);
        model.OldPrimaryNetworkIsolated = true; model.ConfirmAsync = _ => Task.FromResult(false);
        await model.RequestAsync(ManagementOperationKind.Takeover); Assert.Equal(0, connection.Writes); Assert.Null(model.Pending);
    }

    [Theory]
    [InlineData(ManagementOperationKind.Takeover)]
    [InlineData(ManagementOperationKind.Emergency)]
    [InlineData(ManagementOperationKind.Rebuild)]
    [InlineData(ManagementOperationKind.Reprotect)]
    public async Task EachHaActionKeepsAuthenticatedActorEpochVersionAndOneStableOriginalRequest(ManagementOperationKind kind)
    {
        var connection = new HaGuiConnection(); var store = new HaPendingStore(); using var model = Model(connection, store);
        await model.LoginAsync("synthetic"); model.OldPrimaryPoweredOff = true; model.ConfirmDestructiveRebuild = true;
        ManagementConfirmation? confirmation = null; model.ConfirmAsync = value => { confirmation = value; return Task.FromResult(true); };
        await model.RequestAsync(kind);
        Assert.Equal(1, connection.Writes); Assert.NotNull(model.Pending); Assert.True(confirmation!.Dangerous);
        var body = JsonSerializer.Deserialize<HighAvailabilityRequest>(connection.LastBody!, JsonDefaults.Create())!;
        Assert.Equal(connection.Actor, body.ActorId); Assert.Equal(3, body.ExpectedEpoch); Assert.Equal(8, body.ExpectedVersion);
        Assert.Equal("synthetic-node-b", body.TargetNodeId); Assert.Equal(model.Pending!.RequestId, body.RequestId);
        if (kind == ManagementOperationKind.Takeover) { Assert.True(body.Isolation!.HostPoweredOff); Assert.Contains("synthetic-node-a", confirmation.Body); }
        model.HighAvailabilityReason = "changed form"; await model.QueryAsync();
        Assert.Equal(model.Pending.RequestId, connection.LastQuery); Assert.Equal(1, connection.Writes);
        Assert.Equal("synthetic verified reason", JsonSerializer.Deserialize<HighAvailabilityRequest>(model.Pending.Body, JsonDefaults.Create())!.Reason);
    }

    [Fact]
    public async Task EmergencyWarningSurvivesLeavingLogoutLostConnectionAndANewViewModel()
    {
        var memory = new HaWarningMemory();
        var connection = new HaGuiConnection { Mode = ProtectionMode.EmergencySingleNode };
        using (var model = Model(connection, warning: memory))
        {
            model.Activate(); await model.LoginAsync("synthetic"); Assert.True(model.ShowProtectionWarning); Assert.Contains("单机应急", model.ProtectionText);
            model.Deactivate(); Assert.True(model.ShowProtectionWarning); Assert.Contains("此前", model.ProtectionText);
            await model.LogoutAsync(); Assert.True(model.ShowProtectionWarning);
        }
        connection = new() { Unavailable = true }; using var restarted = Model(connection, warning: memory); await restarted.LoginAsync("synthetic");
        Assert.True(restarted.ShowProtectionWarning); Assert.Contains("未核实", restarted.ProtectionText);
        connection.Unavailable = false; connection.Mode = ProtectionMode.SynchronousDualNode; connection.FullProof = false;
        await restarted.RefreshAsync(); Assert.True(restarted.ShowProtectionWarning);
        connection.FullProof = true; await restarted.RefreshAsync(); Assert.False(restarted.ShowProtectionWarning); Assert.Contains("已核实", restarted.ProtectionText);
    }

    [Fact]
    public async Task OnlineAndZeroLagWithoutFullConfirmationDoNotClearAnEmergencyWarning()
    {
        var connection = new HaGuiConnection { Mode = ProtectionMode.EmergencySingleNode }; using var model = Model(connection);
        await model.LoginAsync("synthetic"); connection.Mode = ProtectionMode.SynchronousDualNode; connection.FullProof = false;
        await model.RefreshAsync(); Assert.True(model.ShowProtectionWarning); Assert.DoesNotContain("同步双机保护已核实", model.ProtectionText);
        Assert.All(model.Nodes, row => Assert.Equal("0", row.ReplayLag));
    }

    [Fact]
    public async Task StaleStateIsUnknownAndStaleNodeLagIsExplicitlyUnverified()
    {
        var connection = new HaGuiConnection { StaleState = true }; using var model = Model(connection); await model.LoginAsync("synthetic");
        Assert.Empty(model.Nodes); Assert.False(model.ReprotectCommand.CanExecute(null));
        connection.StaleState = false; connection.StaleNode = true; connection.FullProof = false;
        await model.RefreshAsync(); Assert.Contains(model.Nodes, row => row.ReplayLag == "未核实" && row.ObservedAt == "已过期");
    }

    [Fact]
    public async Task ForgedFullProtectionWithMissingCertificateIsRejected()
    {
        var connection = new HaGuiConnection { Mode = ProtectionMode.EmergencySingleNode }; using var model = Model(connection); await model.LoginAsync("synthetic");
        connection.Mode = ProtectionMode.SynchronousDualNode; connection.CorruptCertificate = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => model.RefreshAsync()); Assert.True(model.ShowProtectionWarning); Assert.Contains("未核实", model.ProtectionText);
    }

    [Fact]
    public void ActualProtectedWarningStoreSurvivesRestartAndRejectsOldProof()
    {
        var root = Path.Combine(Path.GetTempPath(), "synthetic-protection-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProtectedProtectionWarningStore(root); var target = HaGuiConnection.FixedTarget; var now = DateTimeOffset.UtcNow;
            store.Save(target, 3, 8, now, true); store.Save(target, 2, 99, now.AddSeconds(1), false);
            Assert.True(new ProtectedProtectionWarningStore(root).Load(target)!.Degraded);
            Assert.DoesNotContain("synthetic-node", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Directory.GetFiles(root, "*.protection").Single())));
            store.Save(target, 3, 9, now.AddSeconds(1), false); Assert.False(new ProtectedProtectionWarningStore(root).Load(target)!.Degraded);
            Assert.Null(store.Load(target with { NodeId = "another-node" }));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    internal static ServiceManagementViewModel Model(HaGuiConnection connection, HaPendingStore? pending = null, IProtectionWarningStore? warning = null) =>
        new(_ => connection, pending ?? new(), protectionMemory: warning) { Address = connection.Target.Address, ServerId = connection.Target.ServerId,
            NodeId = connection.Target.NodeId, Username = connection.Actor!, HighAvailabilityReason = "synthetic verified reason", ConfirmAsync = _ => Task.FromResult(true) };
}

internal sealed class HaPendingStore : IPendingManagementStore
{
    public PendingManagementRequest? Request;
    public PendingManagementRequest? Load(ManagementTarget target) => Request;
    public void Save(PendingManagementRequest request) => Request = request;
    public void Clear(PendingManagementRequest request) => Request = null;
}
internal sealed class HaWarningMemory : IProtectionWarningStore
{
    public ProtectionWarningMemory? Value;
    public ProtectionWarningMemory? Load(ManagementTarget target) => Value;
    public void Save(ManagementTarget target, long epoch, long version, DateTimeOffset observed, bool degraded) => Value = new(target.Key, epoch, version, observed, degraded);
}
internal sealed class HaGuiConnection : IManagementConnection
{
    public static readonly ManagementTarget FixedTarget = new("https://localhost:9443/", "synthetic-server", "synthetic-node-b");
    public ManagementTarget Target => FixedTarget;
    public string? Actor => "synthetic-operator";
    public ProtectionMode Mode = ProtectionMode.SynchronousDualNode;
    public bool Unavailable, StaleState, StaleNode, CorruptCertificate, FullProof = true;
    public int Writes; public string? LastBody, LastQuery;
    public Task LoginAsync(string user, string password, CancellationToken token) => Task.CompletedTask;
    public Task LogoutAsync(CancellationToken token) => Task.CompletedTask;
    public Task<ManagementReply<ManagementStatus>> StatusAsync(CancellationToken token) => Task.FromResult(Reply(new ManagementStatus(BusinessServiceStatus.Stopped, BusinessReadiness.Unknown,
        new(Target.ServerId, Target.NodeId, "127.0.0.1", 8443, "C:\\synthetic-data", true, 1), [])));
    public Task<ManagementReply<IReadOnlyList<ManagementAuditEntry>>> AuditAsync(int limit, CancellationToken token) => Task.FromResult(Reply<IReadOnlyList<ManagementAuditEntry>>([]));
    public Task<ManagementReply<IReadOnlyList<ProtectedRuntimeLogEntry>>> LogsAsync(int limit, CancellationToken token) => Task.FromResult(Reply<IReadOnlyList<ProtectedRuntimeLogEntry>>([]));
    public Task<ManagementReply<HighAvailabilityStatus>> HighAvailabilityAsync(CancellationToken token) => Task.FromResult(Reply(Availability()));
    public HighAvailabilityStatus Availability()
    {
        var now = DateTimeOffset.UtcNow; var fresh = StaleState ? now.AddSeconds(-15) : now;
        var state = new ServerDeploymentState(Target.ServerId, "synthetic-node-a", 3, Mode,
            new Dictionary<string, NodeState> { ["synthetic-node-a"] = new("synthetic-node-a", DeploymentNodeRole.Primary, true, false, 0, true, true, now),
                ["synthetic-node-b"] = new("synthetic-node-b", Mode == ProtectionMode.Recovering ? DeploymentNodeRole.Rebuilding : DeploymentNodeRole.Standby,
                    true, true, 0, true, !CorruptCertificate, StaleNode ? now.AddSeconds(-15) : now) }, 8, []);
        var proof = FullProof && !Unavailable && Mode == ProtectionMode.SynchronousDualNode;
        return new(Target.ServerId, Target.NodeId, Unavailable ? null : state, Unavailable ? null : fresh, proof,
            [new("failover-state", true, "synthetic actual observation", true), new("synchronous-protection", proof, "synthetic confirmation", true)]);
    }
    public Task<ManagementReply<ManagementOperation>> ExecuteAsync(ManagementOperationKind kind, string body, CancellationToken token)
    {
        Writes++; LastBody = body; using var json = JsonDocument.Parse(body); var id = json.RootElement.GetProperty("requestId").GetString()!;
        return Task.FromResult(Reply(new ManagementOperation(id, Target.ServerId, Target.NodeId, Actor!, kind, PendingManagementRequest.Hash(body),
            ManagementOperationPhase.NeedsVerification, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Error: new(ErrorCodes.ServiceRecovering, "synthetic unknown outcome"))));
    }
    public Task<ManagementReply<ManagementOperation>> FindAsync(string id, CancellationToken token)
    { LastQuery = id; return Task.FromResult(new ManagementReply<ManagementOperation>(HttpStatusCode.NotFound,
        new(Target.ServerId, Target.NodeId, null, false, null, "synthetic", null, new(ErrorCodes.NotFound, "synthetic no proof")))); }
    private ManagementReply<T> Reply<T>(T value) => new(HttpStatusCode.OK, new(Target.ServerId, Target.NodeId, null, false, null, "synthetic", value, null));
    public void Dispose() { }
}
