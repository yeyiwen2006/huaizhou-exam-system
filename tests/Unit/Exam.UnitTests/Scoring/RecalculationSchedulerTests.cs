using Exam.Application.Scoring;
using Exam.Contracts.Grading;
using Exam.Domain.Scoring;

namespace Exam.UnitTests.Scoring;

/// <summary>
/// 重算调度的用例。依据 requirements.md：规则更新后对全部已收卷答卷重算、补收也使用该版本、
/// 同表不得混用版本。用内存范围 + 记录型队列驱动，不需要 PostgreSQL。
/// </summary>
public sealed partial class RecalculationSchedulerTests
{
    private const string ExamId = "recalc-exam";

    [Fact]
    public async Task RunOnceAsync_EnqueuesOnlySubmissionsThatNeedRegrade()
    {
        var (scheduler, queue) = Create(
            new SubmissionGradingState("submission-never", null),
            new SubmissionGradingState("submission-stale", 2),
            new SubmissionGradingState("submission-failed", 2, GradingFailed: true),
            new SubmissionGradingState("submission-current", 3));

        var run = await scheduler.RunOnceAsync(ExamId, 3);

        Assert.Equal(4, run.SubmissionCount);
        Assert.Equal(3, run.Enqueued);

        // 已对齐目标版本的答卷不入队，避免规则没变时重复计分。
        Assert.DoesNotContain(queue.Enqueued, item => item.SubmissionId == "submission-current");
        Assert.Contains(queue.Enqueued, item => item.SubmissionId == "submission-never");
        Assert.Contains(queue.Enqueued, item => item.SubmissionId == "submission-stale");
        Assert.Contains(queue.Enqueued, item => item.SubmissionId == "submission-failed");
    }

    [Fact]
    public async Task RunOnceAsync_EnqueuesWithTargetVersionNotSubmissionVersion()
    {
        // 需求：补收也使用该版本 —— 旧版本答卷要按目标版本重算，而不是按它原来的版本。
        var (scheduler, queue) = Create(new SubmissionGradingState("submission-stale", 2));

        await scheduler.RunOnceAsync(ExamId, 5);

        var item = Assert.Single(queue.Enqueued);
        Assert.Equal(5, item.GradingVersion);
    }

    [Fact]
    public async Task RunOnceAsync_PendingSubmissions_BlockReadiness()
    {
        var (scheduler, _) = Create(
            new SubmissionGradingState("submission-current", 3),
            new SubmissionGradingState("submission-stale", 2));

        var run = await scheduler.RunOnceAsync(ExamId, 3);

        Assert.False(run.Readiness.RecalculationReady);
        Assert.Contains(run.Readiness.Blockers, text => text.Contains("旧规则版本", StringComparison.Ordinal));
        Assert.Equal("submission-stale", Assert.Single(run.Readiness.Outstanding).SubmissionId);
    }

    [Fact]
    public async Task RunOnceAsync_AllAligned_IsReadyAndEnqueuesNothing()
    {
        var (scheduler, queue) = Create(
            new SubmissionGradingState("submission-1", 3),
            new SubmissionGradingState("submission-2", 3));

        var run = await scheduler.RunOnceAsync(ExamId, 3);

        Assert.True(run.Readiness.RecalculationReady);
        Assert.Equal(0, run.Enqueued);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task RunOnceAsync_RepeatedRuns_DoNotEnqueueDuplicates()
    {
        var (scheduler, queue) = Create(new SubmissionGradingState("submission-stale", 1));

        var first = await scheduler.RunOnceAsync(ExamId, 2);
        var second = await scheduler.RunOnceAsync(ExamId, 2);

        Assert.Equal(1, first.Enqueued);
        // 入队按（答卷，规则版本）去重：重复调度不会产生第二个任务。
        Assert.Equal(0, second.Enqueued);
        Assert.Single(queue.Enqueued);
    }

    [Fact]
    public async Task RunOnceAsync_FailedSubmission_BlocksReadinessWithReason()
    {
        var (scheduler, _) = Create(new SubmissionGradingState("submission-failed", 3, GradingFailed: true));

        var run = await scheduler.RunOnceAsync(ExamId, 3);

        Assert.False(run.Readiness.RecalculationReady);
        Assert.Contains(run.Readiness.Blockers, text => text.Contains("计分失败", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunOnceAsync_DuplicateSubmissionInScope_BlocksReadiness()
    {
        var (scheduler, queue) = Create(
            new SubmissionGradingState("submission-1", 1),
            new SubmissionGradingState("submission-1", 1));

        var run = await scheduler.RunOnceAsync(ExamId, 2);

        Assert.False(run.Readiness.RecalculationReady);
        Assert.Contains(run.Readiness.Blockers, text => text.Contains("重复的答卷记录", StringComparison.Ordinal));
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task RunOnceAsync_EmptyScope_IsReadyAndEnqueuesNothing()
    {
        var (scheduler, queue) = Create();

        var run = await scheduler.RunOnceAsync(ExamId, 1);

        Assert.True(run.Readiness.RecalculationReady);
        Assert.Equal(0, run.SubmissionCount);
        Assert.Equal(0, run.Enqueued);
        Assert.Empty(queue.Enqueued);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task RunOnceAsync_InvalidTargetVersion_IsRejected(long targetVersion)
    {
        var (scheduler, _) = Create();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => scheduler.RunOnceAsync(ExamId, targetVersion).AsTask());
    }

    private static (RecalculationScheduler Scheduler, RecordingGradingQueue Queue) Create(
        params SubmissionGradingState[] states)
    {
        var scope = new InMemoryGradingScope();
        scope.SetScope(ExamId, states);
        var queue = new RecordingGradingQueue();
        return (new RecalculationScheduler(scope, queue), queue);
    }

    /// <summary>记录入队的假队列：与生产 outbox 一样按（答卷，规则版本）去重。</summary>
    private sealed class RecordingGradingQueue : IRecalculationQueue
    {
        private readonly List<(string SubmissionId, long GradingVersion)> _enqueued = [];

        public IReadOnlyList<(string SubmissionId, long GradingVersion)> Enqueued => _enqueued;

        public ValueTask<bool> EnsureQueuedAsync(
            string examId,
            string submissionId,
            long gradingVersion,
            CancellationToken cancellationToken = default)
        {
            if (_enqueued.Any(item => item.SubmissionId == submissionId && item.GradingVersion == gradingVersion))
            {
                return ValueTask.FromResult(false);
            }

            _enqueued.Add((submissionId, gradingVersion));
            return ValueTask.FromResult(true);
        }
    }
}
