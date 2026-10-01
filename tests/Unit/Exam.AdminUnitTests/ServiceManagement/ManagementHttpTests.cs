using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Exam.Admin.ServiceManagement;
using Exam.Application.Security;
using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;
using Exam.Infrastructure.ServiceManagement;
using Exam.Management;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Exam.AdminUnitTests.ServiceManagement;

/// <summary>Real management API, session authentication and persistent journal; platform side effects use explicit synthetic doubles.</summary>
public sealed class ManagementHttpTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "f04-http-" + Guid.NewGuid().ToString("N"));
    private readonly Host _host = new();
    private readonly Backups _backups = new();
    private WebApplication _app = null!;
    private bool _lost;
    private ManagementTarget Target => new("https://localhost:9443/", "synthetic-server", "synthetic-node");
    public async Task InitializeAsync() { Directory.CreateDirectory(Path.Combine(_root, "journal")); await StartAsync(); }
    private async Task StartAsync(bool missingConfirmation = false)
    {
        var config = new FileServiceConfigurationStore(Path.Combine(_root, "config.json"));
        if (!File.Exists(Path.Combine(_root, "config.json"))) await config.SaveAsync(new(Target.ServerId, Target.NodeId, "127.0.0.1", 8443, _root, false, 1), 0);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new ManagementOperatorSessions(() => null, Path.Combine(_root, "synthetic-operators.cache"),
            loadFromDatabase: _ => Task.FromResult<IReadOnlyList<Exam.Infrastructure.Postgres.Operators.CachedOperatorCredential>>(
                [new("synthetic-operator", OperatorPasswordHasher.Hash("synthetic-password"), true)])));
        builder.Services.AddSingleton(new ManagementRuntime(Target.ServerId, Target.NodeId, config, _host, _ => _host,
            new Probe(), () => _backups, new FileManagementOperationStore(Path.Combine(_root, "journal")), new SystemClock(),
            observeBusinessReady: (_, _) => Task.FromResult(false),
            prepareRecoveryWorkingCopy: (_, _) => Task.CompletedTask,
            verifyRecoveryTarget: missingConfirmation ? (_, _, _) => Task.FromResult<IReadOnlyList<StartupCheckResult>>(
                [new("recovery-confirmed-state", false, "synthetic missing confirmation", true)]) : null,
            readAudit: (limit, token) => new FileManagementOperationStore(Path.Combine(_root, "journal")).ReadAuditAsync(limit, token),
            readRuntimeLogs: (_, _) => Task.FromResult<IReadOnlyList<ProtectedRuntimeLogEntry>>(
                [new(DateTimeOffset.UtcNow, "business", "Warning", "Exam.Synthetic", 42)])));
        _app = builder.Build(); _app.MapManagementApi(); await _app.StartAsync();
    }
    private ManagementConnection Connection(bool loseResponse = false, bool wrongIdentity = false) => new(wrongIdentity ? Target with { NodeId = "different" } : Target,
        () => loseResponse ? new DropOnce(_app.GetTestServer().CreateHandler(), () => !_lost, () => _lost = true) : _app.GetTestServer().CreateHandler());
    private ServiceManagementViewModel Workspace(ManagementConnection connection) => new(_ => connection,
        new ProtectedPendingManagementStore(Path.Combine(_root, "pending")))
    { Address = Target.Address, ServerId = Target.ServerId, NodeId = Target.NodeId, Username = "synthetic-operator", ConfirmAsync = _ => Task.FromResult(true) };

    [Fact]
    public async Task GuiLoginAndStatusWorkWithoutBusinessOrDatabaseAndLogoutRevokesToken()
    {
        using var connection = Connection(); await connection.LoginAsync("synthetic-operator", "synthetic-password", default);
        var status = await connection.StatusAsync(default);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode); Assert.False(status.Envelope.EpochVerified); Assert.Null(status.Envelope.ServerEpoch);
        Assert.Equal(BusinessServiceStatus.Stopped, status.Envelope.Result!.ProcessStatus);
        await connection.LogoutAsync(default); Assert.Null(connection.Actor);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => connection.StatusAsync(default));
    }
    [Fact]
    public async Task WrongManagementIdentityAndWrongPasswordFailClosed()
    {
        using var connection = Connection(wrongIdentity: true);
        await Assert.ThrowsAsync<HttpRequestException>(() => connection.LoginAsync("synthetic-operator", "synthetic-password", default));
        using var valid = Connection(); await Assert.ThrowsAsync<UnauthorizedAccessException>(() => valid.LoginAsync("synthetic-operator", "wrong", default));
        Assert.Null(connection.Actor); Assert.Null(valid.Actor);
    }
    [Fact]
    public async Task GuiLostPostResponseQueriesOriginalDurableOperationAcrossHostAndGuiRestart()
    {
        string id;
        using (var vm = Workspace(Connection(loseResponse: true)))
        {
            await vm.LoginAsync("synthetic-password"); await vm.RequestAsync(ManagementOperationKind.Stop);
            id = vm.Pending!.RequestId; Assert.Equal(1, _host.Stops);
        }
        await _app.StopAsync(); await _app.DisposeAsync(); await StartAsync();
        using var resumed = Workspace(Connection()); await resumed.LoginAsync("synthetic-password"); await resumed.QueryAsync();
        Assert.Equal(id, resumed.Operation!.RequestId); Assert.Null(resumed.Pending); Assert.Equal(1, _host.Stops);
    }
    [Fact]
    public async Task GuiCompletedChecksShowBlockingFailuresAndBoundedMetadataOnlyLogs()
    {
        using var vm = Workspace(Connection()); await vm.LoginAsync("synthetic-password"); await vm.RequestAsync(ManagementOperationKind.Checks);
        Assert.Contains("阻止业务写入", vm.Message); Assert.Contains(vm.Checks, c => c.WriteGate == "阻止写入");
        await vm.RefreshAsync(); Assert.InRange(vm.Logs.Count, 1, 100); Assert.Contains(vm.Logs, row => row.Code == "42");
        Assert.DoesNotContain("synthetic-private-detail", JsonSerializer.Serialize(vm.Logs));
    }
    [Fact]
    public async Task GuiConfigurationConflictPreservesUnsavedFormAndCanExplicitlyReloadThenSave()
    {
        using var vm = Workspace(Connection()); await vm.LoginAsync("synthetic-password");
        var store = new FileServiceConfigurationStore(Path.Combine(_root, "config.json"));
        var current = await store.LoadAsync(); await store.SaveAsync(current with { HttpsPort = 8555, Version = 2 }, 1);
        vm.HttpsPort = "8666"; await vm.RequestAsync(ManagementOperationKind.Configure);
        Assert.Equal("8666", vm.HttpsPort); Assert.Contains("冲突", vm.Message);
        Assert.Null(vm.Pending); // The failed request is durably recorded, rather than an unbound HTTP conflict.
        await vm.ReloadConfigurationAsync(); Assert.Equal("8555", vm.HttpsPort);
        vm.HttpsPort = "8666"; await vm.RequestAsync(ManagementOperationKind.Configure);
        Assert.Equal(3, vm.Operation!.Configuration!.Version); Assert.Equal(8666, (await store.LoadAsync()).HttpsPort);
    }
    [Fact]
    public async Task GuiBackupRequiresAllThreeIntegrityFlagsAndRestoreNeverConfusesStagingWithReady()
    {
        using var vm = Workspace(Connection()); await vm.LoginAsync("synthetic-password");
        vm.BackupDestination = Path.Combine(_root, "synthetic-backup"); await vm.RequestAsync(ManagementOperationKind.Backup);
        Assert.Contains("数据库、受保护配置和版本资源", vm.Message); Assert.Equal(1, _backups.BackupCalls);
        Assert.True(vm.Operation!.Backup!.IncludesDatabase && vm.Operation.Backup.IncludesProtectedConfiguration && vm.Operation.Backup.IncludesVersionedResources);
        vm.RestoreBackupId = "synthetic-backup"; vm.RestoreSource = vm.BackupDestination;
        await vm.RequestAsync(ManagementOperationKind.Restore);
        Assert.NotNull(vm.Pending); Assert.Equal(ManagementOperationPhase.Staged, vm.Operation!.Phase);
        Assert.Contains("业务就绪：未完成", vm.RestoreText); Assert.Equal(1, _backups.RestoreCalls);
        await vm.QueryAsync(); Assert.NotNull(vm.Pending); Assert.Equal(1, _backups.RestoreCalls);
    }
    [Fact]
    public async Task GuiMissingLatestConfirmationKeepsOriginalRecoveryBlockedAcrossRestart()
    {
        await _app.StopAsync(); await _app.DisposeAsync(); await StartAsync(missingConfirmation: true);
        string id;
        using (var vm = Workspace(Connection()))
        {
            await vm.LoginAsync("synthetic-password"); vm.RestoreBackupId = "synthetic-backup"; vm.RestoreSource = Path.Combine(_root, "synthetic-backup");
            await vm.RequestAsync(ManagementOperationKind.Restore); id = vm.Pending!.RequestId;
            Assert.Contains(vm.Checks, c => c.Name == "recovery-confirmed-state" && c.WriteGate == "阻止写入");
        }
        await _app.StopAsync(); await _app.DisposeAsync(); await StartAsync(missingConfirmation: true);
        using var resumed = Workspace(Connection()); await resumed.LoginAsync("synthetic-password"); await resumed.QueryAsync();
        Assert.Equal(id, resumed.Pending!.RequestId); Assert.Contains("业务就绪：未完成", resumed.RestoreText);
        await resumed.ResumeRestoreAsync(); Assert.Equal(id, resumed.Pending!.RequestId); Assert.Equal(1, _backups.RestoreCalls);
    }
    [Fact]
    public async Task RestartedManagementSessionReturnsToLoginWithoutDroppingOriginalPending()
    {
        using var vm = Workspace(Connection(loseResponse: true)); await vm.LoginAsync("synthetic-password"); await vm.RequestAsync(ManagementOperationKind.Stop);
        var pending = vm.Pending!;
        await _app.StopAsync(); await _app.DisposeAsync(); await StartAsync();
        // Transport still points at the old test server, mirroring a lost management connection.
        await vm.QueryAsync(); Assert.NotNull(new ProtectedPendingManagementStore(Path.Combine(_root, "pending")).Load(pending.Target));
    }
    private sealed class DropOnce(HttpMessageHandler inner, Func<bool> shouldDrop, Action mark) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/management/v1/service/stop" && shouldDrop())
            { mark(); response.Dispose(); throw new HttpRequestException("synthetic final-response loss"); }
            return response;
        }
    }
    private sealed class Host : IManagedBusinessHost
    {
        public BusinessServiceStatus Status { get; private set; } = BusinessServiceStatus.Stopped;
        public int Stops { get; private set; }
        public Task StartAsync(CancellationToken token) { Status = BusinessServiceStatus.Running; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken token) { Stops++; Status = BusinessServiceStatus.Stopped; return Task.CompletedTask; }
    }
    private sealed class Probe : IStartupReadinessProbe
    {
        public Task<IReadOnlyList<StartupCheckResult>> RunAsync(ServiceConfiguration configuration, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<StartupCheckResult>>([new("synthetic-blocking-check", false, "synthetic-private-detail", true)]);
    }
    private sealed class Backups : IBackupCoordinator, IBackupOperationEvidence
    {
        public int BackupCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public Task<BackupResult> CreateAsync(BackupRequest request, CancellationToken token)
        { BackupCalls++; return Task.FromResult(new BackupResult("synthetic-backup", request.DestinationDirectory, DateTimeOffset.UtcNow, true, true, true)); }
        public Task RestoreAsync(RestoreRequest request, CancellationToken token) { RestoreCalls++; return Task.CompletedTask; }
        public Task<BackupResult?> FindCompletedBackupAsync(BackupRequest request, CancellationToken token) => Task.FromResult<BackupResult?>(null);
        public Task<bool> FindStagedRestoreAsync(RestoreRequest request, CancellationToken token) => Task.FromResult(true);
    }
    public async Task DisposeAsync()
    { await _app.StopAsync(); await _app.DisposeAsync(); Directory.Delete(_root, true); }
}
