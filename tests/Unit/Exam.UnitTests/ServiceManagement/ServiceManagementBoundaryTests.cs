using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class ServiceManagementBoundaryTests
{
    private static ServiceConfiguration Config => new("server", "node", "127.0.0.1", 443, Path.GetTempPath(), false, 1);
    private static readonly StartupCheckResult Ready = new("test", true, "ok", true);

    [Theory]
    [InlineData(BusinessServiceStatus.Running)]
    [InlineData(BusinessServiceStatus.Starting)]
    [InlineData(BusinessServiceStatus.Stopping)]
    [InlineData(BusinessServiceStatus.Faulted)]
    public async Task RestoreRechecksActualHostBeforeCallingCoordinator(BusinessServiceStatus actual)
    {
        var host = new Host(); var backups = new Backups();
        var service = Service(host, backups);
        host.Status = actual;
        await Assert.ThrowsAsync<DomainRuleException>(() => service.RestoreAsync(new("restore", "backup", "source", "actor")));
        Assert.Equal(0, backups.Restores);
    }

    [Fact]
    public async Task UnknownHostStateCannotAuthorizeConfigurationChange()
    {
        var host = new Host(); var store = new Store();
        var service = Service(host, new Backups(), store: store);
        host.Status = BusinessServiceStatus.Faulted;
        await Assert.ThrowsAsync<DomainRuleException>(() => service.ConfigureAsync(Config with { HttpsPort = 7443 }, 1));
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task CancelledProbeCannotStartEvenWhenItIgnoresCancellation()
    {
        using var cancel = new CancellationTokenSource();
        var host = new Host();
        var service = Service(host, new Backups(), new Probe(() => { cancel.Cancel(); return [Ready]; }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartAsync(cancel.Token));
        Assert.Equal(0, host.Starts);
        Assert.NotEqual(BusinessServiceStatus.Running, service.GetStatus().Status);
    }

    [Fact]
    public async Task RestoreRechecksHostAfterCoordinatorReturns()
    {
        var host = new Host(); var backups = new Backups { OnRestore = () => host.Status = BusinessServiceStatus.Running };
        var service = Service(host, backups);
        await Assert.ThrowsAsync<DomainRuleException>(() => service.RestoreAsync(new("restore", "backup", "source", "actor")));
        Assert.Equal(BusinessServiceStatus.Faulted, service.GetStatus().Status);
    }

    [Fact]
    public async Task BackupReplayCannotChangeDestinationOrActor()
    {
        var backups = new Backups(); var service = Service(new Host(), backups);
        var request = new BackupRequest("same", "destination", "actor");
        await service.CreateBackupAsync(request);
        await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBackupAsync(request with { ActorId = "other" }));
        await Assert.ThrowsAsync<DomainRuleException>(() => service.CreateBackupAsync(request with { DestinationDirectory = "other" }));
        Assert.Equal(1, backups.Creates);
    }

    [Fact]
    public async Task RestoreReplayCannotChangeBackupOrActor()
    {
        var backups = new Backups(); var service = Service(new Host(), backups);
        var request = new RestoreRequest("same", "backup", "source", "actor");
        await service.RestoreAsync(request);
        await Assert.ThrowsAsync<DomainRuleException>(() => service.RestoreAsync(request with { BackupId = "other" }));
        await Assert.ThrowsAsync<DomainRuleException>(() => service.RestoreAsync(request with { ActorId = "other" }));
        Assert.Equal(1, backups.Restores);
    }

    [Fact]
    public async Task FailedRestoreDoesNotLeaveStoppedSuccessStatus()
    {
        var backups = new Backups { OnRestore = () => throw new IOException("synthetic") };
        var service = Service(new Host(), backups);
        await Assert.ThrowsAsync<DomainRuleException>(() => service.RestoreAsync(new("id", "backup", "source", "actor")));
        Assert.Equal(BusinessServiceStatus.Faulted, service.GetStatus().Status);
    }

    [Fact]
    public async Task NullReadinessEntryCannotAuthorizeStart()
    {
        var host = new Host(); var service = Service(host, new Backups(), new Probe(() => [null!]));
        await Assert.ThrowsAsync<DomainRuleException>(() => service.StartAsync());
        Assert.Equal(0, host.Starts);
    }

    [Fact]
    public async Task HostChangingDuringReadinessCannotBeStartedAgain()
    {
        var host = new Host();
        var service = Service(host, new Backups(), new Probe(() => { host.Status = BusinessServiceStatus.Running; return [Ready]; }));
        await Assert.ThrowsAsync<DomainRuleException>(() => service.StartAsync());
        Assert.Equal(0, host.Starts);
    }

    [Fact]
    public async Task StartChecksLatestDurableConfigurationInsteadOfStaleCache()
    {
        var host = new Host(); var store = new Store(); var probe = new CaptureProbe();
        var service = Service(host, new Backups(), probe, store);
        store.Current = Config with { HttpsPort = 7443, Version = 2 };
        var started = await service.StartAsync();
        Assert.Equal(store.Current, probe.Seen);
        Assert.Equal(store.Current, started.Configuration);
    }

    private sealed class CaptureProbe : IStartupReadinessProbe
    {
        public ServiceConfiguration? Seen;
        public Task<IReadOnlyList<StartupCheckResult>> RunAsync(ServiceConfiguration c, CancellationToken ct)
        { Seen = c; return Task.FromResult<IReadOnlyList<StartupCheckResult>>([Ready]); }
    }

    [Fact]
    public async Task BackupAcceptsCanonicalDestinationFromTheStorageAdapter()
    {
        var canonical = Path.Combine(Path.GetTempPath(), "synthetic-backup");
        var backups = new Backups { Destination = canonical };
        var service = Service(new Host(), backups);
        var request = new BackupRequest("canonical", canonical + Path.DirectorySeparatorChar + ".", "actor");
        var result = await service.CreateBackupAsync(request);
        Assert.Equal(canonical, result.DestinationDirectory);
        Assert.Same(result, await service.CreateBackupAsync(request));
        Assert.Equal(1, backups.Creates);
    }

    private static ServiceManagementService Service(Host host, Backups backups, IStartupReadinessProbe? probe = null, Store? store = null) =>
        new(Config, host, probe ?? new Probe(() => [Ready]), backups, new Clock(), store ?? new Store());
    private sealed class Host : IManagedBusinessHost
    {
        public BusinessServiceStatus Status { get; set; } = BusinessServiceStatus.Stopped;
        public int Starts;
        public Task StartAsync(CancellationToken ct) { Starts++; Status = BusinessServiceStatus.Running; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) { Status = BusinessServiceStatus.Stopped; return Task.CompletedTask; }
    }
    private sealed class Probe(Func<IReadOnlyList<StartupCheckResult>> run) : IStartupReadinessProbe
    {
        public Task<IReadOnlyList<StartupCheckResult>> RunAsync(ServiceConfiguration c, CancellationToken ct) => Task.FromResult(run());
    }
    private sealed class Backups : IBackupCoordinator
    {
        public int Creates; public int Restores; public Action? OnRestore; public string? Destination;
        public Task<BackupResult> CreateAsync(BackupRequest r, CancellationToken ct)
        { Creates++; return Task.FromResult(new BackupResult("backup", Destination ?? r.DestinationDirectory, DateTimeOffset.UtcNow, true, true, true)); }
        public Task RestoreAsync(RestoreRequest r, CancellationToken ct) { Restores++; OnRestore?.Invoke(); return Task.CompletedTask; }
    }
    private sealed class Store : IServiceConfigurationStore
    {
        public int Saves;
        public ServiceConfiguration Current = Config;
        public Task<ServiceConfiguration> LoadAsync(CancellationToken ct = default) => Task.FromResult(Current);
        public Task<ServiceConfiguration> SaveAsync(ServiceConfiguration c, long version, CancellationToken ct = default)
        { Saves++; return Task.FromResult(Current = c with { Version = version + 1 }); }
    }
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
}
