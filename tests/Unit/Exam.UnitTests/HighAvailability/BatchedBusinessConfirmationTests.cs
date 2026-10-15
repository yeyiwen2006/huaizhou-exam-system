using System.Threading.Channels;
using Exam.Application.HighAvailability;

namespace Exam.UnitTests.HighAvailability;

public sealed class BatchedBusinessConfirmationTests
{
    [Fact]
    public async Task WritesArrivingDuringAFenceWaitForTheNextFenceTogether()
    {
        var inner = new ControlledConfirmation();
        var confirmation = new BatchedBusinessConfirmation(inner);
        var first = confirmation.ConfirmAsync(default);
        var firstFence = await inner.NextAsync();
        var waiting = Enumerable.Range(0, 20).Select(_ => confirmation.ConfirmAsync(default)).ToArray();
        firstFence.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        var nextFence = await inner.NextAsync();
        Assert.All(waiting, task => Assert.False(task.IsCompleted));
        nextFence.TrySetResult();
        await Task.WhenAll(waiting).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task CancelledCallerDoesNotCancelOtherCommittedRequests()
    {
        var inner = new ControlledConfirmation();
        var confirmation = new BatchedBusinessConfirmation(inner);
        var first = confirmation.ConfirmAsync(default);
        var firstFence = await inner.NextAsync();
        using var caller = new CancellationTokenSource();
        var abandoned = confirmation.ConfirmAsync(caller.Token);
        var retained = confirmation.ConfirmAsync(default);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        firstFence.TrySetResult();
        await first;
        var nextFence = await inner.NextAsync();
        Assert.False(retained.IsCompleted);
        nextFence.TrySetResult();
        await retained.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task FailedFenceNeverReleasesSuccessAndLaterRequestsRetry()
    {
        var inner = new ControlledConfirmation();
        var confirmation = new BatchedBusinessConfirmation(inner);
        var first = confirmation.ConfirmAsync(default);
        var firstFence = await inner.NextAsync();
        var waiting = Enumerable.Range(0, 3).Select(_ => confirmation.ConfirmAsync(default)).ToArray();
        firstFence.TrySetResult();
        await first;
        var failedFence = await inner.NextAsync();
        failedFence.TrySetException(new IOException("Synthetic peer failure"));
        foreach (var task in waiting) await Assert.ThrowsAsync<IOException>(() => task);
        var retry = confirmation.ConfirmAsync(default);
        (await inner.NextAsync()).TrySetResult();
        await retry.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task ServerShutdownCancelsTheSharedConfirmation()
    {
        var inner = new ControlledConfirmation();
        using var shutdown = new CancellationTokenSource();
        var confirmation = new BatchedBusinessConfirmation(inner, shutdown.Token);
        var pending = confirmation.ConfirmAsync(default);
        await inner.NextAsync();
        shutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => confirmation.ConfirmAsync(default));
    }

    private sealed class ControlledConfirmation : IBusinessConfirmation
    {
        private readonly Channel<TaskCompletionSource> _calls = Channel.CreateUnbounded<TaskCompletionSource>();
        public int Calls;
        public async Task ConfirmAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await _calls.Writer.WriteAsync(completion, cancellationToken);
            await completion.Task.WaitAsync(cancellationToken);
        }
        public Task<TaskCompletionSource> NextAsync() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
