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
    [InlineData("https://exam.example.test:0/")]
    [InlineData("https://localhost:9443/management/v1")]
    [InlineData("https://localhost:9443/?secret=x")]
    [InlineData("https://localhost:9443/#fragment")]
    [InlineData("https://synthetic:synthetic@localhost:9443/")]
    public void TargetRejectsInvalidHttpsRoots(string address) => Assert.Throws<ArgumentException>(() => (Target with { Address = address }).Validate());

    [Theory]
    [InlineData("https://localhost:9443/")]
    [InlineData("https://127.0.0.1:9443/")]
    [InlineData("https://[::1]:9443/")]
    [InlineData("https://management.example.test/")]
    [InlineData("https://192.0.2.10:9443/")]
    public void TargetAcceptsIndependentHttpsEndpoint(string address) => Assert.Equal("https", (Target with { Address = address }).Validate().Scheme);

    [Theory]
    [InlineData("/srv/exam/backups/snapshot")]
    [InlineData("D:\\Exam\\backups\\snapshot")]
    [InlineData("\\\\backup-server\\exam\\snapshot")]
    public async Task BackupPathsAreSentUnchangedToTheTargetServer(string path)
    {
        var connection = new Connection(); var store = new Store();
        using var vm = ViewModel(connection, store); await vm.LoginAsync("synthetic");
        vm.BackupDestination = path;
        await vm.RequestAsync(ManagementOperationKind.Backup);
        Assert.Equal(1, connection.Writes);
        using var document = JsonDocument.Parse(connection.LastBody!);
        Assert.Equal(path, document.RootElement.GetProperty("destinationDirectory").GetString());
    }

    [Fact]
    public void ManagementRouteRequiresBusinessLoginAndClosesOnDisconnect()
    {
        var router = new AdminWorkspaceRouter();
        router.OpenManagement(); router.BusinessConnectionChanged(false);
        Assert.Equal(AdminWorkspaceRoute.BusinessLogin, router.Route);
        router.ReturnToBusiness(); Assert.Equal(AdminWorkspaceRoute.BusinessLogin, router.Route);
        router.BusinessConnectionChanged(true); router.OpenManagement(); router.ReturnToBusiness();
        Assert.Equal(AdminWorkspaceRoute.BusinessWorkspace, router.Route);
        router.OpenManagement(); Assert.Equal(AdminWorkspaceRoute.LocalManagement, router.Route);
        router.BusinessConnectionChanged(false); Assert.Equal(AdminWorkspaceRoute.BusinessLogin, router.Route);
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
    public async Task QueryDefaultsToTheMostRecentCompletedRequest()
    {
        var connection = new Connection(); using var vm = ViewModel(connection, new Store());
        await vm.LoginAsync("synthetic"); await vm.RequestAsync(ManagementOperationKind.Checks);
        var id = vm.Operation!.RequestId;
        Assert.Null(vm.Pending); Assert.True(vm.QueryCommand.CanExecute(null));
        vm.RequestIdToQuery = string.Empty;
        await vm.QueryAsync();
        Assert.Equal(id, connection.LastQuery); Assert.Contains("完成", vm.Message);
    }

    [Fact]
    public async Task HistoricalQueryAndUnknownIdNeverReleaseAnotherPendingRequest()
    {
        var connection = new Connection { LoseWriteResponse = true }; var store = new Store();
        using var vm = ViewModel(connection, store); await vm.LoginAsync("synthetic");
        await vm.RequestAsync(ManagementOperationKind.Stop); var pending = vm.Pending!;
        connection.Find = PendingManagementRequest.Create(Target, "synthetic-operator", ManagementOperationKind.Checks,
            "history", JsonSerializer.Serialize(new ManagementServiceRequest("history", Target.ServerId, Target.NodeId), JsonDefaults.Create()));
        vm.RequestIdToQuery = "history"; await vm.QueryAsync();
        Assert.Equal("history", vm.Operation!.RequestId); Assert.Same(pending, vm.Pending); Assert.Same(pending, store.Request);
        connection.NotFound = true; vm.RequestIdToQuery = "unknown"; await vm.QueryAsync();
        Assert.Contains("未找到请求 unknown", vm.Message); Assert.Same(pending, vm.Pending); Assert.Same(pending, store.Request);
    }

    [Fact]
    public async Task ClearingBusinessIdentityDiscardsManagementSessionAndLateSessionExchange()
    {
        var connection = new Connection { SessionExchange = new TaskCompletionSource() };
        using var vm = ViewModel(connection, new Store());
        vm.SetBusinessSession(await BusinessSessionFixture.CaptureAsync());
        var connecting = vm.ReconnectAsync();
        vm.SetBusinessSession(null);
        connection.SessionExchange.SetResult(); await connecting;
        Assert.False(vm.IsSignedIn); Assert.False(vm.ReconnectCommand.CanExecute(null));
        Assert.False(vm.QueryCommand.CanExecute(null)); Assert.Equal(string.Empty, vm.OperationText);
    }

    [Fact]
    public async Task NotFoundDoesNotReleasePendingOrInventReplacementId()
    {
        var connection = new Connection { LoseWriteResponse = true, NotFound = true }; var store = new Store();
        using var vm = ViewModel(connection, store); await vm.LoginAsync("synthetic"); await vm.RequestAsync(ManagementOperationKind.Stop);
        var pending = vm.Pending!; await vm.QueryAsync(); await vm.RequestAsync(ManagementOperationKind.Start);
        Assert.Equal(pending, vm.Pending); Assert.Equal(1, connection.Writes);
        Assert.Contains("未找到请求", vm.Message);
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

    [Theory]
    [InlineData(BusinessServiceStatus.Stopped, BusinessReadiness.Unknown, "服务器已停止")]
    [InlineData(BusinessServiceStatus.Running, BusinessReadiness.Ready, "服务器程序正常运行")]
    [InlineData(BusinessServiceStatus.Running, BusinessReadiness.NotReady, "服务器程序运行异常")]
    [InlineData(BusinessServiceStatus.Running, BusinessReadiness.Unknown, "服务器程序状态未确认")]
    [InlineData(BusinessServiceStatus.Faulted, BusinessReadiness.Unknown, "服务器程序运行异常")]
    public async Task ServerStatusCombinesProcessAndReadiness(BusinessServiceStatus process, BusinessReadiness readiness, string expected)
    {
        using var vm = ViewModel(new Connection { Status = new(process, readiness, null, [new("business-endpoint", true, "synthetic", true)]) }, new Store());
        await vm.LoginAsync("synthetic"); Assert.Equal(expected, vm.ServerStatusText);
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
        Assert.True(vm.CanResumeRestore); Assert.Equal("恢复条件核对未通过", vm.RestoreText);
        vm.RestoreSource = "C:\\different"; vm.RestoreBackupId = "different";
        await vm.ResumeRestoreAsync(); Assert.Equal(original.Body, connection.LastBody); Assert.Equal(2, connection.Writes);
        Assert.Equal(original.RequestId, vm.Pending!.RequestId);
    }

    [Fact]
    public async Task RestoreReadsServerBackupBeforeConfirmingAndUsesItsIdentityAndNormalizedDirectory()
    {
        var backup = new BackupInfo("server-backup", "/srv/exam/backups/snapshot", new DateTimeOffset(2026, 10, 13, 3, 0, 0, TimeSpan.Zero));
        var connection = new Connection { BackupMetadata = backup, Staged = true }; var store = new Store();
        using var vm = ViewModel(connection, store); await vm.LoginAsync("synthetic");
        vm.RestoreSource = "/srv/exam/backups/./snapshot";
        vm.RestoreBackupId = "obsolete-manual-value";
        vm.ConfirmAsync = confirmation =>
        {
            Assert.Equal(1, connection.BackupInfoReads); Assert.Equal(0, connection.Writes); Assert.Null(store.Request);
            Assert.Equal("确认按此备份恢复", confirmation.Title);
            Assert.Contains("是否让服务器回到此备份所保存的状态", confirmation.Body);
            Assert.Contains(backup.SourceDirectory, confirmation.Body);
            Assert.Contains("2026-10-13 11:00:00", confirmation.Body);
            Assert.Contains("缺少后续已确认的数据", confirmation.Body);
            return Task.FromResult(true);
        };
        await vm.RequestAsync(ManagementOperationKind.Restore);
        Assert.Equal("/srv/exam/backups/./snapshot", connection.BackupDirectory);
        using var document = JsonDocument.Parse(connection.LastBody!);
        Assert.Equal(backup.BackupId, document.RootElement.GetProperty("backupId").GetString());
        Assert.Equal(backup.SourceDirectory, document.RootElement.GetProperty("sourceDirectory").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableBackupOrCancelledRestoreConfirmationNeverSubmits(bool unreadable)
    {
        var connection = new Connection { BackupInfoUnavailable = unreadable }; var store = new Store();
        using var vm = ViewModel(connection, store); await vm.LoginAsync("synthetic");
        vm.RestoreSource = "/srv/exam/backups/snapshot";
        var confirmations = 0; vm.ConfirmAsync = _ => { confirmations++; return Task.FromResult(false); };
        await vm.RequestAsync(ManagementOperationKind.Restore);
        Assert.Equal(1, connection.BackupInfoReads); Assert.Equal(unreadable ? 0 : 1, confirmations);
        Assert.Equal(0, connection.Writes); Assert.Null(store.Request); Assert.Null(vm.Pending);
    }

    [Fact]
    public async Task LongRestoreReadsDurableStagesDuringOneWriteAndRefreshesActualServerState()
    {
        var connection = new Connection { WriteResponse = new() }; var store = new Store();
        using var vm = ViewModel(connection, store); await vm.LoginAsync("synthetic");
        vm.RestoreSource = "/srv/exam/backups/snapshot";
        var write = vm.RequestAsync(ManagementOperationKind.Restore); var original = vm.Pending!;
        Assert.Equal("恢复副本准备中", vm.RestoreText);
        var prepared = Connection.Completed(original).Envelope.Result! with
        {
            Phase = ManagementOperationPhase.Staged, Restore = new("synthetic-backup", true, false, false),
            Checks = [new("recovery-working-copy", true, "synthetic", true)], UpdatedAt = DateTimeOffset.UtcNow
        };
        connection.QueryResponse = Connection.Reply(prepared);
        await WaitForRestoreTextAsync(vm, "恢复条件核对中");
        var starting = prepared with { Restore = new("synthetic-backup", true, true, false),
            RecoveryActivation = RecoveryActivationPhase.StartingBusiness, UpdatedAt = DateTimeOffset.UtcNow };
        connection.QueryResponse = Connection.Reply(starting);
        await WaitForRestoreTextAsync(vm, "恢复后业务状态检测中");
        connection.Status = new(BusinessServiceStatus.Running, BusinessReadiness.Ready, null, [new("business-readiness", true, "synthetic", true)]);
        connection.WriteResponse.SetResult(Connection.Reply(starting with { Phase = ManagementOperationPhase.Completed,
            Restore = new("synthetic-backup", true, true, true), RecoveryActivation = RecoveryActivationPhase.BusinessReady,
            Status = null, UpdatedAt = DateTimeOffset.UtcNow }));
        await write;
        Assert.Equal("恢复完毕，服务器已就绪", vm.RestoreText);
        Assert.Equal("服务器程序正常运行", vm.ServerStatusText); Assert.Null(vm.Pending);
        Assert.Equal(1, connection.Writes); Assert.Equal(original.RequestId, connection.LastQuery);
    }

    [Fact]
    public async Task CancelRestoreProgressKeepsWriteAliveAndLateReadCannotReplaceItsCompletion()
    {
        var connection = new Connection { WriteResponse = new(), FindResponse = new() }; var store = new Store();
        using var vm = ViewModel(connection, store); await vm.LoginAsync("synthetic");
        vm.RestoreSource = "/srv/exam/backups/snapshot";
        var write = vm.RequestAsync(ManagementOperationKind.Restore); var original = vm.Pending!;
        var query = vm.QueryAsync();
        vm.CancelWaiting(); Assert.False(connection.WriteToken.IsCancellationRequested);
        Assert.Equal("恢复副本准备未确认", vm.RestoreText);
        var completed = Connection.Completed(original).Envelope.Result! with
        {
            Restore = new("synthetic-backup", true, true, true), RecoveryActivation = RecoveryActivationPhase.BusinessReady,
            Checks = [new("recovery-working-copy", true, "synthetic", true)], Status = null
        };
        connection.WriteResponse.SetResult(Connection.Reply(completed)); await write;
        connection.FindResponse.SetResult(Connection.Reply(completed with { Phase = ManagementOperationPhase.Staged,
            Restore = new("synthetic-backup", true, false, false), RecoveryActivation = null }));
        await query;
        Assert.Equal("恢复完毕，服务器已就绪", vm.RestoreText); Assert.Null(vm.Pending);
        Assert.Equal(ManagementOperationPhase.Completed, vm.Operation!.Phase); Assert.Equal(1, connection.Writes);
    }

    private static async Task WaitForRestoreTextAsync(ServiceManagementViewModel vm, string text)
    {
        if (vm.RestoreText == text) return;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        { if (args.PropertyName == nameof(vm.RestoreText) && vm.RestoreText == text) changed.TrySetResult(); }
        vm.PropertyChanged += OnChanged;
        try { await changed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { vm.PropertyChanged -= OnChanged; }
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
        public bool BackupInfoUnavailable { get; set; }
        public int BackupInfoReads { get; private set; }
        public string? BackupDirectory { get; private set; }
        public BackupInfo? BackupMetadata { get; set; }
        public string ReturnedActor { get; set; } = "synthetic-operator";
        public string? LastQuery { get; private set; }
        public string? LastBody { get; private set; }
        public PendingManagementRequest? Find { get; set; }
        public TaskCompletionSource<ManagementReply<ManagementOperation>>? WriteResponse { get; set; }
        public TaskCompletionSource<ManagementReply<ManagementOperation>>? FindResponse { get; set; }
        public ManagementReply<ManagementOperation>? QueryResponse { get; set; }
        public CancellationToken WriteToken { get; private set; }
        public TaskCompletionSource? SessionExchange { get; set; }
        public ManagementStatus Status { get; set; } = new(BusinessServiceStatus.Stopped, BusinessReadiness.Unknown,
            new(ManagementWorkspaceTests.Target.ServerId, ManagementWorkspaceTests.Target.NodeId, "127.0.0.1", 8443, "C:\\synthetic-data", false, 7), []);
        public Task LoginAsync(string username, string password, CancellationToken token) => Task.CompletedTask;
        public Task UseBusinessSessionAsync(Exam.Admin.Services.AdminSessionSnapshot session, CancellationToken token) => SessionExchange?.Task ?? Task.CompletedTask;
        public Task LogoutAsync(CancellationToken token) => Task.CompletedTask;
        public Task<ManagementReply<ManagementStatus>> StatusAsync(CancellationToken token) => Task.FromResult(Reply(Status));
        public Task<ManagementReply<IReadOnlyList<ManagementAuditEntry>>> AuditAsync(int limit, CancellationToken token) => Task.FromResult(Reply<IReadOnlyList<ManagementAuditEntry>>([]));
        public Task<ManagementReply<IReadOnlyList<ProtectedRuntimeLogEntry>>> LogsAsync(int limit, CancellationToken token) => Task.FromResult(Reply<IReadOnlyList<ProtectedRuntimeLogEntry>>([]));
        public Task<ManagementReply<BackupInfo>> BackupInfoAsync(string directory, CancellationToken token)
        {
            BackupInfoReads++; BackupDirectory = directory;
            return Task.FromResult(BackupInfoUnavailable ? Reply<BackupInfo>(null!, HttpStatusCode.NotFound, new(ErrorCodes.NotFound, "synthetic"))
                : Reply(BackupMetadata ?? new BackupInfo("synthetic-backup", directory, DateTimeOffset.UtcNow)));
        }
        public Task<ManagementReply<ManagementOperation>> ExecuteAsync(ManagementOperationKind kind, string body, CancellationToken token)
        {
            Writes++; LastBody = body; WriteToken = token;
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
            return FindResponse?.Task ?? Task.FromResult(QueryResponse ?? (NotFound ? Reply<ManagementOperation>(null!, HttpStatusCode.NotFound, new(ErrorCodes.NotFound, "synthetic")) : Completed(Find!)));
        }
        public static ManagementReply<ManagementOperation> Completed(PendingManagementRequest request) => Reply(new ManagementOperation(
            request.RequestId, ManagementWorkspaceTests.Target.ServerId, ManagementWorkspaceTests.Target.NodeId, request.Actor, request.Kind, request.BodyHash, ManagementOperationPhase.Completed,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            Status: new(BusinessServiceStatus.Stopped, BusinessReadiness.Unknown, null, []), Checks: [new("synthetic-blocking-check", false, "synthetic", true)]));
        public static ManagementReply<T> Reply<T>(T value, HttpStatusCode status = HttpStatusCode.OK, ApiError? error = null) =>
            new(status, new(ManagementWorkspaceTests.Target.ServerId, ManagementWorkspaceTests.Target.NodeId, null, false, null, "synthetic-correlation", value, error));
        public void Dispose() { }
    }
}
