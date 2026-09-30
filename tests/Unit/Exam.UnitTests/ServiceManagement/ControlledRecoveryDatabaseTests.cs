using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class ControlledRecoveryDatabaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "controlled-recovery-" + Guid.NewGuid().ToString("N"));
    private string Working => Path.Combine(_root, "working");
    private string Configuration => Path.Combine(_root, "recovery.conf");
    private readonly ServiceControl _service = new();
    private readonly RestoreRequest _request = new("restore", "backup", "synthetic-source", "operator");
    private int _copies;
    public ControlledRecoveryDatabaseTests()
    {
        Directory.CreateDirectory(Working);
        File.WriteAllText(Configuration, "# synthetic protected deployment configuration\n");
        _service.Command = ControlledRecoveryDatabase.BuildServiceCommand("RecoveryPG", _root, Working, Configuration, 55432);
    }
    private ControlledRecoveryDatabase Controller() => new("RecoveryPG", "OriginalPG", "Business", "Management", _root,
        Working, Configuration, "Host=127.0.0.1;Port=5432;Database=exam", "Host=127.0.0.1;Port=55432;Database=exam", _service, _service);
    private RecoveryWorkingCopy Copy() => new(_request.RequestId, _request.ActorId, _request.BackupId,
        _request.SourceDirectory, Path.Combine(Working, "slot"), DateTimeOffset.UtcNow);
    private Task<RecoveryWorkingCopy> Prepare(CancellationToken token)
    {
        _copies++;
        Directory.CreateDirectory(Path.Combine(Working, "slot", "database"));
        return Task.FromResult(Copy());
    }
    private Task<RecoveryWorkingCopy?> Observe(CancellationToken token) => Task.FromResult<RecoveryWorkingCopy?>(Copy());

    [Fact]
    public async Task StartsOnlyBoundFixedService_AndRunningReplayNeverCopiesAgain()
    {
        await Controller().PrepareAndStartAsync(_request, Prepare, Observe, default);
        Assert.Equal(1, _copies);
        Assert.Equal(1, _service.Starts);
        Assert.Equal("RecoveryPG", _service.StartedName);
        await Controller().PrepareAndStartAsync(_request, _ => throw new IOException("不能重新复制"), Observe, default);
        Assert.Equal(1, _service.Starts);
        Assert.Equal(0, _service.Stops);
    }

    [Theory]
    [InlineData("original-command")]
    [InlineData("extra-option")]
    [InlineData("transition")]
    [InlineData("faulted")]
    [InlineData("missing-config")]
    public async Task InvalidDeploymentOrUnknownStatusCannotCopyOrStart(string scenario)
    {
        if (scenario == "original-command") _service.Command = _service.Command.Replace("slot", "original", StringComparison.Ordinal);
        if (scenario == "extra-option") _service.Command += " -o unsafe";
        if (scenario == "transition") _service.State = ManagedWindowsServiceState.StartPending;
        if (scenario == "faulted") _service.State = ManagedWindowsServiceState.Faulted;
        if (scenario == "missing-config") File.Delete(Configuration);
        await Assert.ThrowsAnyAsync<IOException>(() => Controller().PrepareAndStartAsync(_request, Prepare, Observe, default));
        Assert.Equal(0, _copies);
        Assert.Equal(0, _service.Starts);
    }

    [Theory]
    [InlineData("postgresql.auto.conf", "primary_conninfo = 'synthetic-original'\n")]
    [InlineData("postgresql.auto.conf", "include = 'unsafe.conf'\n")]
    [InlineData("standby.signal", "")]
    [InlineData("recovery.signal", "")]
    [InlineData("postmaster.pid", "1234\n")]
    public async Task InheritedSettingsAndSignalsArePreservedAndBlockStart(string name, string content)
    {
        await Prepare(default);
        var path = Path.Combine(Working, "slot", "database", name);
        await File.WriteAllTextAsync(path, content);
        await Assert.ThrowsAnyAsync<IOException>(() => Controller().PrepareAndStartAsync(_request, _ => Task.FromResult(Copy()), Observe, default));
        Assert.Equal(content, await File.ReadAllTextAsync(path));
        Assert.Equal(0, _service.Starts);
    }

    [Fact]
    public async Task CommentsOnlyAutomaticConfigurationDoesNotBlockStart()
    {
        await Prepare(default);
        await File.WriteAllTextAsync(Path.Combine(Working, "slot", "database", "postgresql.auto.conf"), "# synthetic empty configuration\n\n");
        await Controller().PrepareAndStartAsync(_request, _ => Task.FromResult(Copy()), Observe, default);
        Assert.Equal(1, _service.Starts);
    }

    [Fact]
    public async Task RunningServiceWithoutMatchingEvidenceNeverRepairsOrStartsAnotherCopy()
    {
        _service.State = ManagedWindowsServiceState.Running;
        await Assert.ThrowsAsync<IOException>(() => Controller().PrepareAndStartAsync(_request, Prepare,
            _ => Task.FromResult<RecoveryWorkingCopy?>(null), default));
        await Assert.ThrowsAsync<IOException>(() => Controller().PrepareAndStartAsync(_request, Prepare,
            _ => Task.FromResult<RecoveryWorkingCopy?>(Copy() with { ActorId = "different" }), default));
        Assert.Equal(0, _copies);
        Assert.Equal(0, _service.Starts);
        Assert.Equal(0, _service.Stops);
    }

    [Fact]
    public async Task CancellationAfterStartKeepsUnknownOutcomeForObservation_WithoutStoppingOrRecopying()
    {
        using var cancellation = new CancellationTokenSource();
        _service.AfterStart = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Controller().PrepareAndStartAsync(_request, Prepare, Observe, cancellation.Token));
        Assert.Equal(ManagedWindowsServiceState.Running, _service.State);
        Assert.Equal(0, _service.Stops);
        await Controller().PrepareAndStartAsync(_request, _ => throw new IOException("不能重新复制"), Observe, default);
        Assert.Equal(1, _service.Starts);
    }

    [Fact]
    public void ServiceOptionsQuotePathsWithSpacesForBothWindowsArgumentLayers()
    {
        var command = ControlledRecoveryDatabase.BuildServiceCommand("RecoveryPG", Path.Combine(_root, "Program Files"),
            Path.Combine(_root, "working space"), Path.Combine(_root, "protected space", "recovery.conf"), 55432);
        Assert.Contains("-o \"-c config_file=\\\"" + Path.Combine(_root, "protected space", "recovery.conf").Replace('\\', '/') + "\\\"", command);
        Assert.Contains("-c shared_preload_libraries=\\\"\\\"", command);
        Assert.DoesNotContain("config_file='", command);
    }

    [Fact]
    public void RefusesSharedServiceIdentityAndUnsafeConfigurationPath()
    {
        Assert.Throws<ArgumentException>(() => new ControlledRecoveryDatabase("Business", "OriginalPG", "business", "Management", _root,
            Working, Configuration, "Host=127.0.0.1;Database=exam", "Host=127.0.0.1;Port=55432;Database=exam", _service, _service));
        Assert.Throws<ArgumentException>(() => ControlledRecoveryDatabase.BuildServiceCommand("RecoveryPG", _root, Working, Configuration + "' -c port=5432", 55432));
    }

    [Theory]
    [InlineData("wal-recovery.json")]
    [InlineData("wal-recovery-plan.json")]
    public async Task RemovingArchiveConfigurationCannotChangeBoundRunningRecovery(string marker)
    {
        await Prepare(default);
        await File.WriteAllTextAsync(Path.Combine(Working, "slot", marker), "synthetic-bound-record");
        _service.State = ManagedWindowsServiceState.Running;
        await Assert.ThrowsAsync<IOException>(() => Controller().PrepareAndStartAsync(_request, _ => throw new IOException("不能复制"), Observe, default));
        Assert.Equal(0, _service.Starts);
    }

    [Fact]
    public void ArchiveCommandHasFixedLocalReplayAndNoInheritedRestoreCommands()
    {
        var options = new PostgresArchiveRecoveryOptions(Path.Combine(_root, "archive"), 1234, 1, "0/300028");
        var command = ControlledRecoveryDatabase.BuildServiceCommand("RecoveryPG", _root, Working, Configuration, 55432, options);
        Assert.Contains("-c restore_command=\\\"exit 1\\\"", command);
        Assert.Contains("-c recovery_target_lsn=0/300028", command);
        Assert.Contains("-c recovery_target_timeline=1", command);
        Assert.Contains("-c primary_conninfo=\\\"\\\"", command);
        Assert.DoesNotContain("latest", command);
        Assert.Throws<ArgumentException>(() => ControlledRecoveryDatabase.BuildServiceCommand("RecoveryPG", _root, Working, Configuration, 55432, options with { Timeline = 2 }));
    }

    private sealed class ServiceControl : IWindowsServiceControl, IWindowsServiceDeployment
    {
        public string Command { get; set; } = "";
        public ManagedWindowsServiceState State { get; set; } = ManagedWindowsServiceState.Stopped;
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public string? StartedName { get; private set; }
        public Action? AfterStart { get; set; }
        public string GetBinaryPath(string name) => Command;
        public ManagedWindowsServiceState GetState(string name) => State;
        public void Start(string name) { Starts++; StartedName = name; State = ManagedWindowsServiceState.Running; AfterStart?.Invoke(); }
        public void Stop(string name) { Stops++; State = ManagedWindowsServiceState.Stopped; }
    }
    public void Dispose() => Directory.Delete(_root, true);
}
