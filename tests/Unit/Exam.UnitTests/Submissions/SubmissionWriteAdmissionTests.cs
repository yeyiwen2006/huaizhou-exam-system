using Exam.Application.Submissions;

namespace Exam.UnitTests.Submissions;

public sealed class SubmissionWriteAdmissionTests
{
    [Fact]
    public async Task FinalQueueHasReservedSpaceAndPrecedesQueuedProgress()
    {
        using var gate = new SubmissionWriteAdmission(1, 1, TimeSpan.FromSeconds(5));
        Assert.True(await gate.EnterAsync(false, default));
        var progress = gate.EnterAsync(false, default).AsTask();
        Assert.False(await gate.EnterAsync(false, default));
        var final = gate.EnterAsync(true, default).AsTask();
        gate.Release();
        Assert.True(await final);
        Assert.False(progress.IsCompleted);
        gate.Release();
        Assert.True(await progress);
        gate.Release();
    }

    [Fact]
    public async Task CancelledWaiterDoesNotLeakCapacityOrBlockFollowingWrite()
    {
        using var gate = new SubmissionWriteAdmission(1, 1, TimeSpan.FromSeconds(5));
        Assert.True(await gate.EnterAsync(false, default));
        using var cancellation = new CancellationTokenSource();
        var cancelled = gate.EnterAsync(true, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var next = gate.EnterAsync(true, default).AsTask();
        gate.Release();
        Assert.True(await next);
        gate.Release();
        Assert.True(await gate.EnterAsync(false, default));
        gate.Release();
    }

    [Fact]
    public async Task TimeoutAndDisposeUnblockWaitersWithoutInventingSlots()
    {
        var gate = new SubmissionWriteAdmission(1, 1, TimeSpan.FromMilliseconds(20));
        Assert.True(await gate.EnterAsync(false, default));
        Assert.False(await gate.EnterAsync(false, default));
        var pending = gate.EnterAsync(true, default).AsTask();
        gate.Dispose();
        Assert.False(await pending);
        gate.Release();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => gate.EnterAsync(false, default).AsTask());
    }

    [Fact]
    public async Task ConcurrentCancellationAndReleaseRetainAllSlots()
    {
        using var gate = new SubmissionWriteAdmission(1, 1, TimeSpan.FromSeconds(2));
        for (var index = 0; index < 30; index++)
        {
            Assert.True(await gate.EnterAsync(false, default));
            using var cancellation = new CancellationTokenSource();
            var pending = gate.EnterAsync(false, cancellation.Token).AsTask();
            await Task.WhenAll(Task.Run(cancellation.Cancel), Task.Run(gate.Release));
            try { if (await pending) gate.Release(); }
            catch (OperationCanceledException) { }
        }
        Assert.True(await gate.EnterAsync(true, default));
        gate.Release();
    }
}
