using Exam.Application.Scoring;
using Exam.Contracts.Grading;
using Exam.Domain.Scoring;

namespace Exam.UnitTests.Scoring;

public sealed partial class RecalculationSchedulerTests
{
    [Fact]
    public async Task FutureResultBlocksAllSchedulingInsteadOfDowngradingAnySubmission()
    {
        var (scheduler, queue) = Create(new("newer", 4), new("older", 1));
        var run = await scheduler.RunOnceAsync(ExamId, 2);
        Assert.Empty(queue.Enqueued);
        Assert.Equal(0, run.Enqueued);
        Assert.False(run.Plan.IsComplete);
        Assert.False(run.Readiness.RecalculationReady);
        Assert.Contains(run.Plan.Pending, item => item.Reason == RegradeReason.VersionAhead);
    }

    [Fact]
    public async Task DuplicateCurrentStatesCannotReturnCompleteOrEnqueueWork()
    {
        var (scheduler, queue) = Create(new("s", 3), new("s", 3), new("s", 3));
        var run = await scheduler.RunOnceAsync(ExamId, 3);
        Assert.False(run.Plan.IsComplete);
        Assert.False(run.Readiness.RecalculationReady);
        Assert.Single(run.Plan.DuplicateSubmissionIds);
        Assert.Equal(1, run.SubmissionCount);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task MutatingSourceDuringQueueWriteCannotChangeReadinessSnapshot()
    {
        var states = new List<SubmissionGradingState> { new("s", null) };
        var scheduler = new RecalculationScheduler(new MutableScope(states), new CallbackQueue(() => states.Clear()));
        var run = await scheduler.RunOnceAsync(ExamId, 3);
        Assert.Empty(states);
        Assert.Equal(1, run.SubmissionCount);
        Assert.Single(run.Readiness.Outstanding);
        Assert.False(run.Readiness.RecalculationReady);
    }

    [Fact]
    public async Task InvalidLaterStateIsRejectedBeforeAnyQueueWrite()
    {
        var queue = new RecordingGradingQueue();
        var scheduler = new RecalculationScheduler(new MutableScope([new("s", null), null!]), queue);
        await Assert.ThrowsAsync<ArgumentNullException>(() => scheduler.RunOnceAsync(ExamId, 3).AsTask());
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task CanceledScopeReadAndScheduleDoNotEnqueue()
    {
        var scope = new InMemoryGradingScope();
        scope.SetScope(ExamId, [new("s", null)]);
        var queue = new RecordingGradingQueue();
        var canceled = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ListStatesAsync(ExamId, canceled).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RecalculationScheduler(scope, queue).RunOnceAsync(ExamId, 3, canceled).AsTask());
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task PartialQueueFailureCanBeRetriedWithoutDuplicateWorkOrFalseReadiness()
    {
        var scope = new InMemoryGradingScope();
        scope.SetScope(ExamId, [new("a", null), new("b", null)]);
        var queue = new RecoveringQueue();
        var scheduler = new RecalculationScheduler(scope, queue);
        await Assert.ThrowsAsync<IOException>(() => scheduler.RunOnceAsync(ExamId, 3).AsTask());
        var recovered = await scheduler.RunOnceAsync(ExamId, 3);
        Assert.Equal(1, recovered.Enqueued);
        Assert.Equal(2, queue.Queued.Count);
        Assert.False(recovered.Readiness.RecalculationReady);
    }

    private sealed class MutableScope(List<SubmissionGradingState> states) : IGradingScope
    {
        public ValueTask<IReadOnlyList<SubmissionGradingState>> ListStatesAsync(string examId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<SubmissionGradingState>>(states);
    }

    private sealed class CallbackQueue(Action callback) : IRecalculationQueue
    {
        public ValueTask<bool> EnsureQueuedAsync(string examId, string submissionId, long targetVersion, CancellationToken cancellationToken = default)
        {
            callback();
            return ValueTask.FromResult(true);
        }
    }

    private sealed class RecoveringQueue : IRecalculationQueue
    {
        private bool _fail = true;
        public HashSet<(string Exam, string Submission, long Version)> Queued { get; } = [];
        public ValueTask<bool> EnsureQueuedAsync(string examId, string submissionId, long targetVersion, CancellationToken cancellationToken = default)
        {
            if (_fail && submissionId == "b") { _fail = false; throw new IOException("synthetic queue failure"); }
            return ValueTask.FromResult(Queued.Add((examId, submissionId, targetVersion)));
        }
    }
}
