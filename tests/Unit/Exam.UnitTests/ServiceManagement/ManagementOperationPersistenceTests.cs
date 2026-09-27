using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class ManagementOperationPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "management-journal-" + Guid.NewGuid().ToString("N"));
    public ManagementOperationPersistenceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task RestartReplaysCompletedResult_AndRejectsChangedContentActorKindAndNode()
    {
        var calls = 0;
        var runner = Runner();
        var first = await runner.ExecuteAsync("same/id", "operator", ManagementOperationKind.Stop, "payload", Complete);
        var replay = await Runner().ExecuteAsync("same/id", "operator", ManagementOperationKind.Stop, "payload", Complete);
        Assert.Equal(first, replay);
        Assert.Equal(1, calls);
        await Conflict(Runner(), "operator", ManagementOperationKind.Stop, "different");
        await Conflict(Runner(), "other", ManagementOperationKind.Stop, "payload");
        await Conflict(Runner(), "operator", ManagementOperationKind.Start, "payload");
        await Conflict(new("server", "other-node", Store(), new SystemClock()), "operator", ManagementOperationKind.Stop, "payload");

        Task<ManagementOperation> Complete(ManagementOperation op, CancellationToken token)
        { calls++; return Task.FromResult(op with { Phase = ManagementOperationPhase.Completed }); }
        async Task Conflict(ManagementOperationRunner target, string actor, ManagementOperationKind kind, string content)
        {
            var error = await Assert.ThrowsAsync<DomainRuleException>(() => target.ExecuteAsync("same/id", actor, kind, content, Complete));
            Assert.Equal(ErrorCodes.VersionConflict, error.Code);
        }
    }

    [Fact]
    public async Task CrashAfterAcceptanceNeverRepeatsSideEffect_AndBlocksNewStart()
    {
        var failStore = new FailCompletionStore(Store());
        var runner = new ManagementOperationRunner("server", "node", failStore, new SystemClock());
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() => runner.ExecuteAsync("interrupted", "operator", ManagementOperationKind.Stop, "payload", Complete));
        var replay = await Runner().ExecuteAsync("interrupted", "operator", ManagementOperationKind.Stop, "payload", Complete);
        Assert.Equal(ManagementOperationPhase.NeedsVerification, replay.Phase);
        Assert.Equal(1, calls);
        await Assert.ThrowsAsync<DomainRuleException>(() => Runner().ExecuteAsync("new-start", "operator", ManagementOperationKind.Start, "payload", Complete));
        Assert.Equal(1, calls);
        Task<ManagementOperation> Complete(ManagementOperation op, CancellationToken token)
        { calls++; return Task.FromResult(op with { Phase = ManagementOperationPhase.Completed }); }
    }

    [Fact]
    public async Task AcceptanceFailurePreventsExecution()
    {
        var journal = new FailCompletionStore(Store()) { FailAcceptance = true };
        var runner = new ManagementOperationRunner("server", "node", journal, new SystemClock());
        var executed = false;
        await Assert.ThrowsAsync<IOException>(() => runner.ExecuteAsync("id", "operator", ManagementOperationKind.Stop, "payload", (op, token) =>
        { executed = true; return Task.FromResult(op with { Phase = ManagementOperationPhase.Completed }); }));
        Assert.False(executed);
    }

    [Fact]
    public async Task SeparateRunnersSerializeSameRequest()
    {
        var count = 0;
        var tasks = Enumerable.Range(0, 8).Select(_ => Runner().ExecuteAsync("same", "operator", ManagementOperationKind.Stop, "payload", async (op, token) =>
        {
            Interlocked.Increment(ref count);
            await Task.Delay(25, token);
            return op with { Phase = ManagementOperationPhase.Completed };
        }));
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, count);
        Assert.All(results, result => Assert.Equal(results[0], result));
    }

    [Fact]
    public async Task CorruptJournalFailsClosed()
    {
        await Runner().ExecuteAsync("id", "operator", ManagementOperationKind.Stop, "payload",
            (op, token) => Task.FromResult(op with { Phase = ManagementOperationPhase.Completed }));
        var path = Assert.Single(Directory.GetFiles(_directory, "*.json"));
        await File.WriteAllTextAsync(path, "{}");
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().FindAsync("id", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().HasUnverifiedOperationsAsync(default));
    }

    [Fact]
    public async Task CancellationAfterAcceptanceIsDurableAndNotReexecuted()
    {
        var result = await Runner().ExecuteAsync("cancelled", "operator", ManagementOperationKind.Restore, "payload",
            (op, token) => throw new OperationCanceledException());
        Assert.Equal(ManagementOperationPhase.NeedsVerification, result.Phase);
        var replay = await Runner().FindAsync("cancelled", "operator", default);
        Assert.Equal(result, replay);
    }

    [Fact]
    public async Task QueryDistinguishesExecutingRequestFromInterruptedAcceptance()
    {
        var runner = Runner();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executing = runner.ExecuteAsync("active", "operator", ManagementOperationKind.Stop, "payload", async (op, token) =>
        {
            entered.SetResult();
            await release.Task;
            return op with { Phase = ManagementOperationPhase.Completed };
        });
        await entered.Task;
        try { Assert.Equal(ManagementOperationPhase.Accepted, (await runner.FindAsync("active", "operator", default)).Phase); }
        finally { release.SetResult(); }
        Assert.Equal(ManagementOperationPhase.Completed, (await executing).Phase);
    }

    [Fact]
    public async Task ValidatedInterruptedRestoreStaysStagedAndKeepsNewStartBlocked()
    {
        await Runner().ExecuteAsync("restore", "operator", ManagementOperationKind.Restore, "payload",
            (operation, token) => throw new IOException("synthetic interrupted result"));
        var restarted = new ManagementOperationRunner("server", "node", Store(), new SystemClock(),
            (operation, token) => Task.FromResult<ManagementOperation?>(operation with { Phase = ManagementOperationPhase.Staged,
                Error = null, Restore = new("synthetic-backup", true, false, false) }));
        var result = await restarted.FindAsync("restore", "operator", default);
        Assert.Equal(ManagementOperationPhase.Staged, result.Phase);
        Assert.Equal(result, await Runner().FindAsync("restore", "operator", default));
        await Assert.ThrowsAsync<DomainRuleException>(() => Runner().ExecuteAsync("start", "operator", ManagementOperationKind.Start,
            "payload", (op, token) => throw new InvalidOperationException("must not start")));
    }

    private FileManagementOperationStore Store() => new(_directory);

    [Fact]
    public async Task OversizedJournalBlocksMutationAndPreservesOriginalFile()
    {
        var path = Path.Combine(_directory, "oversized.json");
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) file.SetLength(16 * 1024 * 1024);
        var store = Store();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.HasUnverifiedOperationsAsync(default));
        var effects = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => Runner().ExecuteAsync("blocked-start", "operator", ManagementOperationKind.Start,
            "payload", (operation, token) => { effects++; return Task.FromResult(operation with { Phase = ManagementOperationPhase.Completed }); }));
        Assert.Equal(0, effects);
        Assert.Equal(16 * 1024 * 1024, new FileInfo(path).Length);
        Assert.Single(Directory.GetFiles(_directory, "*.json"));
    }

    [Fact]
    public async Task InterruptedOperationCanBeReconciledAndPersistedWithoutRepeatingEffect()
    {
        var effects = 0;
        var initial = new ManagementOperationRunner("server", "node", new FailCompletionStore(Store()), new SystemClock());
        await Assert.ThrowsAsync<IOException>(() => initial.ExecuteAsync("interrupted", "operator", ManagementOperationKind.Stop, "payload",
            (operation, token) => { effects++; return Task.FromResult(operation with { Phase = ManagementOperationPhase.Completed }); }));
        var observed = 0;
        var restarted = new ManagementOperationRunner("server", "node", Store(), new SystemClock(), (operation, token) =>
        { observed++; return Task.FromResult<ManagementOperation?>(operation with { Phase = ManagementOperationPhase.Completed }); });
        var result = await restarted.FindAsync("interrupted", "operator", default);
        Assert.Equal(ManagementOperationPhase.Completed, result.Phase);
        Assert.Equal(result, await Runner().FindAsync("interrupted", "operator", default));
        Assert.Equal(1, effects);
        Assert.Equal(1, observed);
        await restarted.ExecuteAsync("interrupted", "operator", ManagementOperationKind.Stop, "payload",
            (operation, token) => throw new InvalidOperationException("must not repeat"));
        Assert.Equal(1, observed);
    }

    [Fact]
    public async Task UnavailableObservationKeepsPending_AndChangedRequestBindingIsRejected()
    {
        await Runner().ExecuteAsync("pending", "operator", ManagementOperationKind.Start, "payload",
            (op, token) => throw new IOException("synthetic interruption"));
        var unknown = new ManagementOperationRunner("server", "node", Store(), new SystemClock(),
            (op, token) => throw new IOException("synthetic unavailable observation"));
        Assert.Equal(ManagementOperationPhase.NeedsVerification, (await unknown.FindAsync("pending", "operator", default)).Phase);
        var invalid = new ManagementOperationRunner("server", "node", Store(), new SystemClock(),
            (op, token) => Task.FromResult<ManagementOperation?>(op with { Phase = ManagementOperationPhase.Completed, ActorId = "forged", Error = null }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => invalid.FindAsync("pending", "operator", default));
        Assert.Equal(ManagementOperationPhase.NeedsVerification, (await Runner().FindAsync("pending", "operator", default)).Phase);
    }

    [Fact]
    public async Task LegacyJournalWithoutOriginalRequestStillLoads_AndChangedOriginalContentFailsClosed()
    {
        await Runner().ExecuteAsync("legacy", "operator", ManagementOperationKind.Stop, "payload",
            (op, token) => Task.FromResult(op with { Phase = ManagementOperationPhase.Completed }));
        var record = (await Store().FindAsync("legacy", default))!;
        await Store().SaveAsync(record with { RequestJson = null }, default);
        Assert.Null((await Runner().FindAsync("legacy", "operator", default)).RequestJson);
        await Assert.ThrowsAsync<InvalidDataException>(() => Store().SaveAsync(record with { RequestJson = "forged" }, default));
    }

    private ManagementOperationRunner Runner() => new("server", "node", Store(), new SystemClock());
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class FailCompletionStore(IManagementOperationStore inner) : IManagementOperationStore
    {
        public bool FailAcceptance { get; init; }
        public Task<IAsyncDisposable> AcquireAsync(CancellationToken token) => inner.AcquireAsync(token);
        public Task<ManagementOperation?> FindAsync(string id, CancellationToken token) => inner.FindAsync(id, token);
        public Task<bool> HasUnverifiedOperationsAsync(CancellationToken token) => inner.HasUnverifiedOperationsAsync(token);
        public Task SaveAsync(ManagementOperation operation, CancellationToken token) =>
            FailAcceptance || operation.Phase != ManagementOperationPhase.Accepted
                ? throw new IOException("合成落盘故障") : inner.SaveAsync(operation, token);
    }
}
