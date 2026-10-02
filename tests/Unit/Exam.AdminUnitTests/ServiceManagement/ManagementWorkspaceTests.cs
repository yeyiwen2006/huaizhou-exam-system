using System.Net;
using System.Text.Json;
using Exam.Admin.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;

namespace Exam.AdminUnitTests.ServiceManagement;

public sealed class ManagementWorkspaceTests
{
    private static readonly ManagementTarget Target = new("https://localhost:9443/", "synthetic-server", "synthetic-node");

    [Theory]
    [InlineData("http://localhost:9443/")]
    [InlineData("https://exam.example.test/")]
    [InlineData("https://localhost:9443/management/v1")]
    [InlineData("https://localhost:9443/?secret=x")]
    [InlineData("https://localhost:9443/#fragment")]
    [InlineData("https://synthetic:synthetic@localhost:9443/")]
    public void TargetRejectsNonLocalHttpsRoots(string address) => Assert.Throws<ArgumentException>(() => (Target with { Address = address }).Validate());

    [Theory]
    [InlineData("https://localhost:9443/")]
    [InlineData("https://127.0.0.1:9443/")]
    [InlineData("https://[::1]:9443/")]
    public void TargetAcceptsIndependentLocalEndpoint(string address) => Assert.Equal("https", (Target with { Address = address }).Validate().Scheme);

    [Fact]
    public void ManagementRouteSurvivesBusinessDisconnectAndCannotUnlockBusiness()
    {
        var router = new AdminWorkspaceRouter();
        router.OpenManagement(); router.BusinessConnectionChanged(false);
        Assert.Equal(AdminWorkspaceRoute.LocalManagement, router.Route);
        router.ReturnToBusiness(); Assert.Equal(AdminWorkspaceRoute.BusinessLogin, router.Route);
        router.BusinessConnectionChanged(true); router.OpenManagement(); router.ReturnToBusiness();
        Assert.Equal(AdminWorkspaceRoute.BusinessWorkspace, router.Route);
    }

    [Fact]
    public async Task ConfirmationCancelHasNoRequestRecordAndNoSideEffect()
    {
        var connection = new Connection(); var store = new Store();
        using var vm = ViewModel(connection, store); await vm.LoginAsync("synthetic");
        vm.ConfirmAsync = _ => Task.FromResult(false);
        await vm.RequestAsync(ManagementOperationKind.Stop);
        Assert.Equal(0, connection.Writes); Assert.Null(store.Request); Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task FailedPendingPersistencePreventsNetworkWrite()
    {
        var connection = new Connection(); var store = new Store { FailSave = true };
        using var vm = ViewModel(connection, store); await vm.LoginAsync("synthetic");
        await Assert.ThrowsAsync<System.IO.IOException>(() => vm.RequestAsync(ManagementOperationKind.Start));
        Assert.Equal(0, connection.Writes);
    }

    [Fact]
    public async Task LostResponseRetainsOriginalBodyAndIdAcrossGuiRestart()
    {
        var connection = new Connection { LoseWriteResponse = true }; var store = new Store();
        using (var vm = ViewModel(connection, store))
        {
            await vm.LoginAsync("synthetic"); await vm.RequestAsync(ManagementOperationKind.Stop);
            Assert.NotNull(vm.Pending); Assert.Equal(1, connection.Writes);
        }
        var original = store.Request!;
        connection = new Connection { Find = original }; // Server already completed the original operation.
        using var restarted = ViewModel(connection, store); await restarted.LoginAsync("synthetic"); await restarted.QueryAsync();
        Assert.Equal(original.RequestId, connection.LastQuery); Assert.Equal(0, connection.Writes);
        Assert.Null(restarted.Pending); Assert.Contains("完成", restarted.OperationText);
    }

    [Fact]
    public async Task NotFoundDoesNotReleasePendingOrInventReplacementId()
    {
        var connection = new Connection { LoseWriteResponse = true, NotFound = true }; var store = new Store();
        using var vm = ViewModel(connection, store); await vm.LoginAsync("synthetic"); await vm.RequestAsync(ManagementOperationKind.Stop);
        var pending = vm.Pending!; await vm.QueryAsync(); await vm.RequestAsync(ManagementOperationKind.Start);
        Assert.Equal(pending, vm.Pending); Assert.Equal(1, connection.Writes);
        Assert.Contains("不能据此", vm.Message);
    }

    [Fact]
    public async Task ExplicitRetryAfterNotFoundKeepsOriginalIdentityAndContent()
    {
        var connection = new Connection { LoseWriteResponse = true, NotFound = true }; var store = new Store();
        using var vm = ViewModel(connection, store); await vm.LoginAsync("synthetic"); await vm.RequestAsync(ManagementOperationKind.Stop);
        var original = vm.Pending!; await vm.QueryAsync();
        connection.LoseWriteResponse = false;
        await vm.RetryOriginalAsync(); Assert.Equal(2, connection.Writes); Assert.Equal(original.Body, connection.LastBody);
        Assert.Equal(original.RequestId, vm.Operation!.RequestId); Assert.Null(vm.Pending);
    }

    [Fact]
    public async Task OneNodeGateCoversDifferentCommandsAndConfirmation()
    {
        var connection = new Connection(); var store = new Store();
        using var vm = ViewModel(connection, store); await vm.LoginAsync("synthetic");
        var confirm = new TaskCompletionSource<bool>(); vm.ConfirmAsync = _ => confirm.Task;
        var first = vm.RequestAsync(ManagementOperationKind.Stop);
        await vm.RequestAsync(ManagementOperationKind.Start); await vm.RequestAsync(ManagementOperationKind.Checks);
        Assert.False(vm.StartCommand.CanExecute(null)); Assert.Equal(0, connection.Writes);
        confirm.SetResult(true); await first; Assert.Equal(1, connection.Writes);
    }

    [Theory]
    [InlineData("different-actor", false)]
    [InlineData("synthetic-operator", true)]
    public async Task ChangedActorOrBodyHashCannotCompleteOriginal(string actor, bool corruptHash)
    {
        var connection = new Connection { ReturnedActor = actor, CorruptHash = corruptHash }; var store = new Store();
        using var vm = ViewModel(connection, store); await vm.LoginAsync("synthetic"); await vm.RequestAsync(ManagementOperationKind.Stop);
        Assert.NotNull(vm.Pending); Assert.DoesNotContain("操作已完成", vm.Message);
    }

    [Fact]
    public async Task LateReplyAfterLogoutCannotChangeNewSessionOrClearPending()
    {
        var connection = new Connection(); var store = new Store();
        var pendingResponse = new TaskCompletionSource<ManagementReply<ManagementOperation>>(); connection.WriteResponse = pendingResponse;
        using var vm = ViewModel(connection, store); await vm.LoginAsync("synthetic");
        var write = vm.RequestAsync(ManagementOperationKind.Stop); var original = store.Request!;
        await vm.LogoutAsync(); pendingResponse.SetResult(Connection.Completed(original)); await write;
        Assert.False(vm.IsSignedIn); Assert.Null(vm.Operation); Assert.Equal(original, store.Request);
    }

    [Fact]
    public async Task DeactivationRejectsLateWriteAndPreservesPending()
    {
        var connection = new Connection(); var store = new Store();
        var response = new TaskCompletionSource<ManagementReply<ManagementOperation>>(); connection.WriteResponse = response;
        using var vm = ViewModel(connection, store); vm.Activate(); await vm.LoginAsync("synthetic");
        var write = vm.RequestAsync(ManagementOperationKind.Stop); var original = store.Request!;
        vm.Deactivate(); response.SetResult(Connection.Completed(original)); await write;
        Assert.NotNull(vm.Pending); Assert.Null(vm.Operation); Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ProcessRunningIsSeparateFromUnknownReadinessAndUnknownEpoch()
    {
        using var vm = ViewModel(new Connection { Status = new(BusinessServiceStatus.Running, BusinessReadiness.Unknown, null, []) }, new Store());
        await vm.LoginAsync("synthetic"); Assert.Equal("正在运行", vm.ProcessText);
        Assert.Contains("未核实", vm.ReadinessText); Assert.Contains("未核实", vm.EpochText);
        Assert.Contains(vm.Checks, c => c.Result == "未核实");
    }

    [Fact]
    public async Task CompletedChecksWithBlockingFailureNeverDisplayBusinessSuccess()
    {
        using var vm = ViewModel(new Connection(), new Store()); await vm.LoginAsync("synthetic");
        await vm.RequestAsync(ManagementOperationKind.Checks);
        Assert.Contains("阻止业务写入", vm.Message); Assert.Contains(vm.Checks, c => c.WriteGate == "阻止写入");
    }

    [Fact]
    public async Task ConflictPreservesFormAndExpectedVersion()
    {
        using var vm = ViewModel(new Connection { Conflict = true }, new Store()); await vm.LoginAsync("synthetic");
        vm.BindAddress = "127.0.0.2"; vm.HttpsPort = "9555"; vm.DataDirectory = "C:\\synthetic-data";
        await vm.RequestAsync(ManagementOperationKind.Configure);
        Assert.Equal("9555", vm.HttpsPort); Assert.Contains("读取版本：7", vm.ConfigurationVersionText);
        Assert.Contains("冲突", vm.Message); Assert.NotNull(vm.Pending);
    }

    [Fact]
    public async Task RestoreMissingConfirmationOnlyAllowsOriginalBodyContinuation()
    {
        var connection = new Connection { Staged = true }; var store = new Store();
        using var vm = ViewModel(connection, store); await vm.LoginAsync("synthetic");
        vm.RestoreBackupId = "synthetic-backup"; vm.RestoreSource = "C:\\synthetic-backup";
        await vm.RequestAsync(ManagementOperationKind.Restore); var original = vm.Pending!;
        Assert.True(vm.CanResumeRestore); Assert.Contains("业务就绪：未完成", vm.RestoreText);
        vm.RestoreSource = "C:\\different"; vm.RestoreBackupId = "different";
        await vm.ResumeRestoreAsync(); Assert.Equal(original.Body, connection.LastBody); Assert.Equal(2, connection.Writes);
        Assert.Equal(original.RequestId, vm.Pending!.RequestId);
    }

    [Fact]
    public async Task DifferentAccountCannotQueryOrResumePending()
    {
        var store = new Store { Request = PendingManagementRequest.Create(Target, "original-operator", ManagementOperationKind.Stop,
            "original", JsonSerializer.Serialize(new ManagementServiceRequest("original", Target.ServerId, Target.NodeId), JsonDefaults.Create())) };
        var connection = new Connection(); using var vm = ViewModel(connection, store); await vm.LoginAsync("synthetic"); await vm.QueryAsync();
        Assert.Null(connection.LastQuery); Assert.Contains("原账号", vm.Message);
    }

    private static ServiceManagementViewModel ViewModel(Connection connection, Store store) => new(_ => connection, store)
    { Address = Target.Address, ServerId = Target.ServerId, NodeId = Target.NodeId, Username = "synthetic-operator", ConfirmAsync = _ => Task.FromResult(true) };

    private sealed class Store : IPendingManagementStore
    {
        public PendingManagementRequest? Request { get; set; }
        public bool FailSave { get; set; }
        public PendingManagementRequest? Load(ManagementTarget target) => Request;
        public void Save(PendingManagementRequest request) { if (FailSave) throw new System.IO.IOException("synthetic"); Request = request; }
        public void Clear(PendingManagementRequest request) => Request = null;
    }
    private sealed class Connection : IManagementConnection
    {
        public ManagementTarget Target => ManagementWorkspaceTests.Target;
        public string? Actor => "synthetic-operator";
        public int Writes { get; private set; }
        public bool LoseWriteResponse { get; set; }
        public bool NotFound { get; set; }
        public bool Conflict { get; set; }
        public bool Staged { get; set; }
        public bool CorruptHash { get; set; }
        public string ReturnedActor { get; set; } = "synthetic-operator";
        public string? LastQuery { get; private set; }
        public string? LastBody { get; private set; }
        public PendingManagementRequest? Find { get; set; }
        public TaskCompletionSource<ManagementReply<ManagementOperation>>? WriteResponse { get; set; }
        public ManagementStatus Status { get; set; } = new(BusinessServiceStatus.Stopped, BusinessReadiness.Unknown,
            new(ManagementWorkspaceTests.Target.ServerId, ManagementWorkspaceTests.Target.NodeId, "127.0.0.1", 8443, "C:\\synthetic-data", false, 7), []);
        public Task LoginAsync(string username, string password, CancellationToken token) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken token) => Task.CompletedTask;
        public Task<ManagementReply<ManagementStatus>> StatusAsync(CancellationToken token) => Task.FromResult(Reply(Status));
        public Task<ManagementReply<IReadOnlyList<ManagementAuditEntry>>> AuditAsync(int limit, CancellationToken token) => Task.FromResult(Reply<IReadOnlyList<ManagementAuditEntry>>([]));
        public Task<ManagementReply<IReadOnlyList<ProtectedRuntimeLogEntry>>> LogsAsync(int limit, CancellationToken token) => Task.FromResult(Reply<IReadOnlyList<ProtectedRuntimeLogEntry>>([]));
        public Task<ManagementReply<ManagementOperation>> ExecuteAsync(ManagementOperationKind kind, string body, CancellationToken token)
        {
            Writes++; LastBody = body;
            if (LoseWriteResponse) throw new System.Net.Http.HttpRequestException("synthetic response loss");
            using var document = JsonDocument.Parse(body);
            var request = PendingManagementRequest.Create(Target, Actor!, kind, document.RootElement.GetProperty("requestId").GetString()!, body);
            Find = request;
            if (WriteResponse is not null) return WriteResponse.Task;
            if (Conflict) return Task.FromResult(Reply<ManagementOperation>(null!, HttpStatusCode.Conflict, new(ErrorCodes.VersionConflict, "synthetic-private-detail")));
            var completed = Completed(request);
            var op = completed.Envelope.Result! with { ActorId = ReturnedActor, ContentHash = CorruptHash ? "different" : request.BodyHash };
            if (Staged) op = op with { Phase = ManagementOperationPhase.NeedsVerification, Restore = new("synthetic-backup", true, false, false),
                Checks = [new("recovery-confirmed-state", false, "synthetic", true)] };
            return Task.FromResult(Reply(op));
        }
        public Task<ManagementReply<ManagementOperation>> FindAsync(string id, CancellationToken token)
        {
            LastQuery = id;
            return Task.FromResult(NotFound ? Reply<ManagementOperation>(null!, HttpStatusCode.NotFound, new(ErrorCodes.NotFound, "synthetic")) : Completed(Find!));
        }
        public static ManagementReply<ManagementOperation> Completed(PendingManagementRequest request) => Reply(new ManagementOperation(
            request.RequestId, ManagementWorkspaceTests.Target.ServerId, ManagementWorkspaceTests.Target.NodeId, request.Actor, request.Kind, request.BodyHash, ManagementOperationPhase.Completed,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            Status: new(BusinessServiceStatus.Stopped, BusinessReadiness.Unknown, null, []), Checks: [new("synthetic-blocking-check", false, "synthetic", true)]));
        private static ManagementReply<T> Reply<T>(T value, HttpStatusCode status = HttpStatusCode.OK, ApiError? error = null) =>
            new(status, new(ManagementWorkspaceTests.Target.ServerId, ManagementWorkspaceTests.Target.NodeId, null, false, null, "synthetic-correlation", value, error));
        public void Dispose() { }
    }
}
