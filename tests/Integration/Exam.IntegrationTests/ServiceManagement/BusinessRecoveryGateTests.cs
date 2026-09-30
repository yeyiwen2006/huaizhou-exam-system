using Exam.Contracts.ServiceManagement;
using Exam.Server.Runtime;
using Microsoft.Extensions.Hosting;

namespace Exam.IntegrationTests.ServiceManagement;

public sealed class BusinessRecoveryGateTests
{
    [Fact]
    public async Task WorkerHasNoSideEffectsBeforeRecovery_ThenStartsAndStopsWithHost()
    {
        var guard = new BusinessRecoveryGuard();
        using var inner = new ObservedWorker();
        using var gate = new RecoveryGatedWorker<ObservedWorker>(inner, guard);
        await gate.StartAsync(default);
        guard.Observe([new("synthetic-recovery", false, "合成失败", true)], DateTimeOffset.UtcNow);
        await Task.Delay(300);
        Assert.False(inner.Entered.Task.IsCompleted);
        guard.Observe([new("synthetic-recovery", true, "合成通过", true)], DateTimeOffset.UtcNow);
        await inner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await gate.StopAsync(shutdown.Token);
        Assert.True(inner.Exited.Task.IsCompleted);
    }

    [Fact]
    public async Task StoppingBlockedWorkerDoesNotStartIt()
    {
        using var inner = new ObservedWorker();
        using var gate = new RecoveryGatedWorker<ObservedWorker>(inner, new());
        await gate.StartAsync(default);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await gate.StopAsync(shutdown.Token);
        Assert.False(inner.Entered.Task.IsCompleted);
    }

    [Fact]
    public async Task BusinessWorkerFailureIsPropagatedToHostingLayer()
    {
        var guard = new BusinessRecoveryGuard();
        guard.Observe([new("synthetic-recovery", true, "合成通过", true)], DateTimeOffset.UtcNow);
        using var inner = new FailedWorker();
        using var gate = new RecoveryGatedWorker<FailedWorker>(inner, guard);
        await gate.StartAsync(default);
        await Assert.ThrowsAsync<IOException>(() => gate.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class ObservedWorker : BackgroundService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            finally { Exited.SetResult(); }
        }
    }
    private sealed class FailedWorker : BackgroundService
    { protected override Task ExecuteAsync(CancellationToken stoppingToken) => throw new IOException("合成后台故障"); }
}
