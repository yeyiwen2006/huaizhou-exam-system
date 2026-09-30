using Exam.Application.ServiceManagement;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class RecoveryActivationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "recovery-activation-" + Guid.NewGuid().ToString("N"));
    public RecoveryActivationTests() => Directory.CreateDirectory(_directory);
    private FileManagementOperationStore Journal() => new(_directory);
    private FileRecoveryDeploymentSelection Selection() => new(Path.Combine(_directory, "selection"));
    private ManagementOperation Operation() => new("request", "server", "node", "operator", ManagementOperationKind.Restore,
        new string('A', 64), ManagementOperationPhase.Staged, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        Restore: new("backup", true, true, false), RecoveryWorkingDirectory: _directory,
        Checks: [new("recovery-confirmed-state", true, "合成最新状态证明", true)]);

    [Fact]
    public async Task SelectionSurvivesRestart_BindsIdentityAndRejectsOverwriteOrWrongRollback()
    {
        var operation = Operation();
        var deployment = new FileRecoveryDeploymentSwitch(Selection(), 3);
        await deployment.SelectAsync(operation, default);
        Assert.True(new FileRecoveryDeploymentSwitch(Selection(), 3).IsSelected(operation));
        await Assert.ThrowsAsync<InvalidOperationException>(() => deployment.SelectAsync(operation with { RequestId = "other" }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => deployment.RollbackAsync(operation with { ActorId = "other" }, default));
        Assert.Equal("ExamRecovery", Selection().Read()!.ConnectionReference);
        await deployment.RollbackAsync(operation, default);
        await deployment.RollbackAsync(operation, default);
        Assert.Equal("Exam", Selection().Read()!.ConnectionReference);
        await Assert.ThrowsAsync<InvalidOperationException>(() => deployment.RollbackAsync(operation with { RequestId = "other" }, default));
    }

    [Fact]
    public async Task SelectedConnectionNeverFallsBack_WhenMissingCorruptOrIdentityMismatch()
    {
        await new FileRecoveryDeploymentSwitch(Selection(), 3).SelectAsync(Operation(), default);
        var configuration = new Dictionary<string, string?> { ["Recovery:SelectionPath"] = Path.Combine(_directory, "selection"),
            ["ConnectionStrings:Exam"] = "synthetic-original", ["ConnectionStrings:ExamRecovery"] = "synthetic-recovery" };
        string? Get(string key) => configuration.GetValueOrDefault(key);
        Assert.Equal("synthetic-recovery", FileRecoveryDeploymentSelection.ResolveConnection(Get, "server", "node", 3));
        Assert.Throws<InvalidDataException>(() => FileRecoveryDeploymentSelection.ResolveConnection(Get, "server", "other", 3));
        configuration.Remove("ConnectionStrings:ExamRecovery");
        Assert.Throws<InvalidOperationException>(() => FileRecoveryDeploymentSelection.ResolveConnection(Get, "server", "node", 3));
        await File.WriteAllTextAsync(Path.Combine(_directory, "selection"), "{}");
        Assert.ThrowsAny<Exception>(() => FileRecoveryDeploymentSelection.ResolveConnection(Get, "server", "node", 3));
    }

    [Fact]
    public async Task CancelledSelectionPreservesPreviousDeploymentAndCleansTemporaryFile()
    {
        var deployment = new FileRecoveryDeploymentSwitch(Selection(), 3);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => deployment.SelectAsync(Operation(), new(true)));
        Assert.Null(Selection().Read());
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task ReadyResultRequiresDurableIntentBeforeEachSideEffect()
    {
        var operation = Operation();
        await Journal().SaveAsync(operation, default);
        var deployment = new CheckingSwitch(Journal(), new FileRecoveryDeploymentSwitch(Selection(), 3));
        var host = new Host
        {
            BeforeStart = async () =>
            {
                var checkpoint = await Journal().FindAsync("request", default);
                Assert.Equal(RecoveryActivationPhase.StartingBusiness, checkpoint!.RecoveryActivation);
                Assert.Equal(ManagementOperationPhase.NeedsVerification, checkpoint.Phase);
                Assert.Equal("ExamRecovery", Selection().Read()!.ConnectionReference);
            }
        };
        var result = await new RecoveryActivationRunner(host, () => host, deployment, Journal()).ActivateAsync(operation, default);
        Assert.Equal(ManagementOperationPhase.Completed, result.Phase);
        Assert.Equal(RecoveryActivationPhase.BusinessReady, result.RecoveryActivation);
        Assert.True(result.Restore!.BusinessReady);
        Assert.Equal(1, host.Starts);
        Assert.Equal(0, host.Stops);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledReadinessStopsBeforeRollback_AndNeverStartsOriginal(bool cancel)
    {
        var operation = Operation();
        var host = new Host { StartFailure = cancel ? new OperationCanceledException() : new IOException("synthetic") };
        var deployment = new CheckingSwitch(Journal(), new FileRecoveryDeploymentSwitch(Selection(), 3), host);
        var result = await new RecoveryActivationRunner(host, () => host, deployment, Journal()).ActivateAsync(operation, default);
        Assert.Equal(ManagementOperationPhase.Staged, result.Phase);
        Assert.Equal(RecoveryActivationPhase.RolledBack, result.RecoveryActivation);
        Assert.False(result.Restore!.BusinessReady);
        Assert.NotNull(result.Error);
        Assert.Equal("Exam", Selection().Read()!.ConnectionReference);
        Assert.Equal(1, host.Starts);
        Assert.Equal(1, host.Stops);
    }

    [Fact]
    public async Task UnconfirmedStopPreservesSelectedIntent_AndBlocksNewStartAfterRestart()
    {
        var host = new Host { StartFailure = new IOException("synthetic"), StopFailure = true };
        var journal = Journal();
        var operations = new ManagementOperationRunner("server", "node", journal, new SystemClock());
        var result = await operations.ExecuteAsync("request", "operator", ManagementOperationKind.Restore, "payload", async (accepted, token) =>
        {
            var staged = accepted with { Phase = ManagementOperationPhase.Staged, Restore = new("backup", true, true, false),
                RecoveryWorkingDirectory = _directory,
                Checks = [new("recovery-confirmed-state", true, "合成最新状态证明", true)] };
            await journal.SaveAsync(staged, token);
            return await new RecoveryActivationRunner(host, () => host, new FileRecoveryDeploymentSwitch(Selection(), 3), journal).ActivateAsync(staged, token);
        });
        Assert.Equal(ManagementOperationPhase.NeedsVerification, result.Phase);
        Assert.Equal(RecoveryActivationPhase.StartingBusiness, result.RecoveryActivation);
        Assert.Equal("ExamRecovery", Selection().Read()!.ConnectionReference);
        var restarted = new ManagementOperationRunner("server", "node", Journal(), new SystemClock());
        await Assert.ThrowsAsync<DomainRuleException>(() => restarted.ExecuteAsync("new-start", "operator", ManagementOperationKind.Start,
            "payload", (op, _) => Task.FromResult(op with { Phase = ManagementOperationPhase.Completed })));
        Assert.Equal(1, host.Starts);
    }

    [Fact]
    public async Task PossibleNewWritesPreventRollback_AndKeepSelectedRecoveryDatabase()
    {
        var operation = Operation();
        var host = new Host { StartFailure = new IOException("synthetic") };
        var deployment = new FileRecoveryDeploymentSwitch(Selection(), 3, (_, _) => Task.FromResult(false));
        await Assert.ThrowsAsync<IOException>(() => new RecoveryActivationRunner(host, () => host, deployment, Journal())
            .ActivateAsync(operation, default));
        Assert.Equal(BusinessServiceStatus.Stopped, host.Status);
        Assert.Equal("ExamRecovery", Selection().Read()!.ConnectionReference);
        Assert.Equal(RecoveryActivationPhase.RollbackPending, (await Journal().FindAsync("request", default))!.RecoveryActivation);
    }

    [Fact]
    public async Task UnverifiedRestoreCannotSelectOrStart()
    {
        var operation = Operation() with { Restore = new("backup", true, false, false) };
        var host = new Host();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RecoveryActivationRunner(host, () => host,
            new FileRecoveryDeploymentSwitch(Selection(), 3), Journal()).ActivateAsync(operation, default));
        Assert.Null(Selection().Read());
        Assert.Equal(0, host.Starts);
    }

    [Fact]
    public async Task MissingConfirmedStateCannotSelectOrStartEvenIfRestoreFlagIsTrue()
    {
        var operation = Operation() with { Checks = [] };
        var host = new Host();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RecoveryActivationRunner(host, () => host,
            new FileRecoveryDeploymentSwitch(Selection(), 3), Journal()).ActivateAsync(operation, default));
        Assert.Null(Selection().Read());
        Assert.Equal(0, host.Starts);
    }

    private sealed class CheckingSwitch(IManagementOperationStore journal, IRecoveryDeploymentSwitch inner, Host? host = null) : IRecoveryDeploymentSwitch
    {
        public async Task SelectAsync(ManagementOperation operation, CancellationToken token)
        {
            Assert.Equal(RecoveryActivationPhase.SelectionPending, (await journal.FindAsync(operation.RequestId, token))!.RecoveryActivation);
            await inner.SelectAsync(operation, token);
        }
        public async Task RollbackAsync(ManagementOperation operation, CancellationToken token)
        {
            Assert.Equal(BusinessServiceStatus.Stopped, host!.Status);
            Assert.Equal(RecoveryActivationPhase.RollbackPending, (await journal.FindAsync(operation.RequestId, token))!.RecoveryActivation);
            await inner.RollbackAsync(operation, token);
        }
    }
    private sealed class Host : IManagedBusinessHost
    {
        public BusinessServiceStatus Status { get; private set; } = BusinessServiceStatus.Stopped;
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public Exception? StartFailure { get; init; }
        public bool StopFailure { get; init; }
        public Func<Task>? BeforeStart { get; init; }
        public async Task StartAsync(CancellationToken token)
        {
            Starts++;
            if (BeforeStart is not null) await BeforeStart();
            Status = BusinessServiceStatus.Running;
            if (StartFailure is not null) throw StartFailure;
        }
        public Task StopAsync(CancellationToken token)
        { Stops++; if (!StopFailure) Status = BusinessServiceStatus.Stopped; return Task.CompletedTask; }
    }
    public void Dispose() => Directory.Delete(_directory, true);
}
