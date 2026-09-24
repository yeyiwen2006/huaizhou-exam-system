using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;

namespace Exam.UnitTests.ServiceManagement;

public sealed class ServiceManagementServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Start_BlocksWrites_WhenReadinessCheckFails()
    {
        var host = new FakeHost();
        var service = CreateService(
            host,
            [new StartupCheckResult("database", false, "数据库不可读", true)]);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => service.StartAsync());

        Assert.Equal(ErrorCodes.ServiceRecovering, error.Code);
        Assert.Equal(BusinessServiceStatus.Stopped, host.Status);
        Assert.Equal(BusinessServiceStatus.Recovering, service.GetStatus().Status);
        Assert.Contains("数据库不可读", service.GetStatus().LastError);
    }

    [Fact]
    public async Task StartAndStop_KeepManagementObjectAvailable()
    {
        var host = new FakeHost();
        var service = CreateService(
            host,
            [new StartupCheckResult("database", true, "ok", true)]);

        var running = await service.StartAsync();
        var stopped = await service.StopAsync();

        Assert.Equal(BusinessServiceStatus.Running, running.Status);
        Assert.Equal(BusinessServiceStatus.Stopped, stopped.Status);
        Assert.Equal("node-a", stopped.Configuration.NodeId);
    }

    [Fact]
    public async Task Start_RejectsMissingChecks_AndAllowsConfigurationRepair()
    {
        var host = new FakeHost();
        var service = CreateService(host, []);

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => service.StartAsync());
        Assert.Equal(ErrorCodes.ServiceRecovering, error.Code);
        Assert.Equal(BusinessServiceStatus.Stopped, host.Status);

        var changed = await service.ConfigureAsync(
            service.GetStatus().Configuration with { HttpsPort = 7443 }, expectedVersion: 1);
        Assert.Equal(2, changed.Version);
    }

    [Fact]
    public async Task Start_ProbeFailureDoesNotReportRunning()
    {
        var host = new FakeHost();
        var service = new ServiceManagementService(
            Configuration(), host, new ThrowingProbe(), new FakeBackupCoordinator(),
            new FakeClock(Now), new FakeConfigurationStore(Configuration()));

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => service.StartAsync());

        Assert.Equal(ErrorCodes.ServiceRecovering, error.Code);
        Assert.Equal(BusinessServiceStatus.Faulted, service.GetStatus().Status);
        Assert.Equal(BusinessServiceStatus.Stopped, host.Status);
    }

    [Fact]
    public async Task Backup_IsIdempotent_AndRejectsIncompleteArtifact()
    {
        var coordinator = new FakeBackupCoordinator();
        var service = CreateService(new FakeHost(), [], coordinator);
        var request = new BackupRequest("request-1", "D:\\safe-backup", "admin");

        var first = await service.CreateBackupAsync(request);
        var replay = await service.CreateBackupAsync(request);

        Assert.Same(first, replay);
        Assert.Equal(1, coordinator.CreateCount);

        coordinator.Complete = false;
        var error = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.CreateBackupAsync(request with { RequestId = "request-2" }));
        Assert.Equal(ErrorCodes.PersistenceFailed, error.Code);
    }

    [Fact]
    public async Task Configure_RequiresStoppedHostAndExpectedVersion()
    {
        var host = new FakeHost();
        var service = CreateService(host, [new StartupCheckResult("database", true, "ok", true)]);
        await service.StartAsync();

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            service.ConfigureAsync(service.GetStatus().Configuration, expectedVersion: 1));

        await service.StopAsync();
        var changed = await service.ConfigureAsync(
            service.GetStatus().Configuration with { HttpsPort = 7443 },
            expectedVersion: 1);
        Assert.Equal(2, changed.Version);
        Assert.Equal(7443, changed.HttpsPort);
    }

    [Fact]
    public async Task Configure_PublishesVersionOnlyAfterStoreSucceeds()
    {
        var host = new FakeHost();
        var store = new FakeConfigurationStore { FailSave = true };
        var service = CreateService(host, [], store: store);
        var original = service.GetStatus().Configuration;

        await Assert.ThrowsAsync<IOException>(() => service.ConfigureAsync(
            original with { HttpsPort = 7443 }, expectedVersion: 1));
        Assert.Equal(original, service.GetStatus().Configuration);

        store.FailSave = false;
        var updated = await service.ConfigureAsync(
            original with { HttpsPort = 7443 }, expectedVersion: 1);
        Assert.Equal(2, updated.Version);
        Assert.Equal(updated, service.GetStatus().Configuration);
    }

    [Fact]
    public async Task Status_ReportsUnexpectedHostExit()
    {
        var host = new FakeHost();
        var service = CreateService(host, [new StartupCheckResult("database", true, "ok", true)]);
        await service.StartAsync();

        host.Status = BusinessServiceStatus.Stopped;

        Assert.Equal(BusinessServiceStatus.Faulted, service.GetStatus().Status);
        Assert.NotNull(service.GetStatus().LastError);
    }

    [Fact]
    public async Task Start_RejectsUnmanagedRunningHost()
    {
        var host = new FakeHost();
        var service = CreateService(host, [new StartupCheckResult("database", true, "ok", true)]);
        host.Status = BusinessServiceStatus.Running;

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => service.StartAsync());

        Assert.Equal(ErrorCodes.VersionConflict, error.Code);
        Assert.Equal(BusinessServiceStatus.Running, host.Status);
    }

    [Fact]
    public async Task Load_UsesPersistedConfigurationAfterRecreatingManagementService()
    {
        var original = Configuration();
        var store = new FakeConfigurationStore(original);
        var first = CreateService(new FakeHost(), [], store: store);
        var updated = await first.ConfigureAsync(original with { HttpsPort = 7443 }, 1);

        var reopened = await ServiceManagementService.LoadAsync(
            store, new FakeHost(), new FakeProbe([]), new FakeBackupCoordinator(), new FakeClock(Now));

        Assert.Equal(updated, reopened.GetStatus().Configuration);
    }

    [Fact]
    public async Task RequestId_CannotCrossBackupAndRestoreOperations()
    {
        var service = CreateService(new FakeHost(), [new StartupCheckResult("database", true, "ok", true)]);
        await service.CreateBackupAsync(new BackupRequest("same-request", "D:\\safe-backup", "admin"));

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => service.RestoreAsync(
            new RestoreRequest("same-request", "backup-1", "D:\\safe-backup", "admin")));

        Assert.Equal(ErrorCodes.InvalidInput, error.Code);
    }

    private static ServiceManagementService CreateService(
        FakeHost host,
        IReadOnlyList<StartupCheckResult> checks,
        FakeBackupCoordinator? backup = null,
        IServiceConfigurationStore? store = null) =>
        new(
            Configuration(),
            host,
            new FakeProbe(checks),
            backup ?? new FakeBackupCoordinator(),
            new FakeClock(Now),
            store ?? new FakeConfigurationStore(Configuration()));

    private static ServiceConfiguration Configuration() =>
        new("server-1", "node-a", "0.0.0.0", 443, "D:\\exam-data", true, 1);

    private sealed class FakeHost : IManagedBusinessHost
    {
        public BusinessServiceStatus Status { get; set; } = BusinessServiceStatus.Stopped;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Status = BusinessServiceStatus.Running;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Status = BusinessServiceStatus.Stopped;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProbe(IReadOnlyList<StartupCheckResult> checks) : IStartupReadinessProbe
    {
        public Task<IReadOnlyList<StartupCheckResult>> RunAsync(
            ServiceConfiguration configuration,
            CancellationToken cancellationToken) => Task.FromResult(checks);
    }

    private sealed class ThrowingProbe : IStartupReadinessProbe
    {
        public Task<IReadOnlyList<StartupCheckResult>> RunAsync(
            ServiceConfiguration configuration,
            CancellationToken cancellationToken) => throw new IOException("synthetic probe failure");
    }

    private sealed class FakeBackupCoordinator : IBackupCoordinator
    {
        public int CreateCount { get; private set; }

        public bool Complete { get; set; } = true;

        public Task<BackupResult> CreateAsync(BackupRequest request, CancellationToken cancellationToken)
        {
            CreateCount++;
            return Task.FromResult(new BackupResult(
                $"backup-{CreateCount}",
                request.DestinationDirectory,
                Now,
                Complete,
                Complete,
                Complete));
        }

        public Task RestoreAsync(RestoreRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeConfigurationStore(ServiceConfiguration? initial = null) : IServiceConfigurationStore
    {
        public bool FailSave { get; set; }

        private ServiceConfiguration? _current = initial;

        public Task<ServiceConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_current ?? throw new FileNotFoundException());

        public Task<ServiceConfiguration> SaveAsync(
            ServiceConfiguration requested,
            long expectedVersion,
            CancellationToken cancellationToken = default) =>
            FailSave
                ? throw new IOException("synthetic disk failure")
                : Task.FromResult(_current = requested with { Version = expectedVersion + 1 });
    }

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}
