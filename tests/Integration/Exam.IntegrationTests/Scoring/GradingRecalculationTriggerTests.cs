using Exam.Application.Scoring;
using Exam.Contracts.Grading;
using Exam.Contracts.Submissions;
using Exam.Domain.Scoring;
using Exam.Server.Runtime;

using Microsoft.Extensions.Logging.Abstractions;

namespace Exam.IntegrationTests.Scoring;

/// <summary>
/// 规则发布后「及时安排一轮重算」的契约：按**当前已发布规则版本**规划并入队；
/// 没有已发布规则时什么都不做；调度失败**不能**把已经成功的发布变成失败（交给后台循环补偿）。
///
/// 这里用假件替换范围与队列（重算调度本身由 评分处理 已有用例覆盖），不需要数据库。
/// </summary>
public sealed class GradingRecalculationTriggerTests
{
    [Fact]
    public async Task Trigger_EnqueuesAgainstThePublishedRulesVersion()
    {
        var rules = Rules(version: 3);
        var queue = new RecordingQueue();
        var trigger = Trigger(rules, queue);

        var run = await trigger.TryTriggerAsync("synthetic-exam");

        Assert.NotNull(run);
        Assert.Equal(3, run!.TargetVersion);
        Assert.Equal(1, run.Enqueued);
        Assert.Equal([("submission-1", 3L)], queue.Enqueued);
    }

    [Fact]
    public async Task Trigger_WithoutPublishedRules_DoesNothing()
    {
        var queue = new RecordingQueue();
        var trigger = Trigger(rules: null, queue);

        Assert.Null(await trigger.TryTriggerAsync("synthetic-exam"));
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task Trigger_SwallowsSchedulingFailures_SoThePublishStaysSuccessful()
    {
        // 发布已经提交：调度失败只是「重算没被立刻安排」，由后台循环补偿，不能冒泡成发布失败。
        var trigger = new GradingRecalculationTrigger(new FakeInputs(Rules(version: 1)),
            new RecalculationScheduler(new ThrowingScope(), new RecordingQueue()),
            NullLogger<GradingRecalculationTrigger>.Instance);

        Assert.Null(await trigger.TryTriggerAsync("synthetic-exam"));
    }

    [Fact]
    public async Task Trigger_CancellationCannotChangeAnAlreadyCommittedPublicationIntoFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var trigger = Trigger(Rules(version: 1), new RecordingQueue());

        Assert.Null(await trigger.TryTriggerAsync("synthetic-exam", cancellation.Token));
    }

    [Fact]
    public async Task Trigger_HasABoundedSchedulingAttemptEvenWhenTheScopeIsSlow()
    {
        var scope = new SlowScope();
        var trigger = new GradingRecalculationTrigger(new FakeInputs(Rules(1)),
            new RecalculationScheduler(scope, new RecordingQueue()), NullLogger<GradingRecalculationTrigger>.Instance);
        Assert.Null(await trigger.TryTriggerAsync("synthetic-exam").WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(scope.Cancelled);
    }

    private sealed class SlowScope : IGradingScope
    {
        public bool Cancelled { get; private set; }
        public async ValueTask<IReadOnlyList<SubmissionGradingState>> ListStatesAsync(string examId, CancellationToken ct = default)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return []; }
            finally { Cancelled = ct.IsCancellationRequested; }
        }
    }

    private static GradingRecalculationTrigger Trigger(PublishedGradingRules? rules, RecordingQueue queue) =>
        new(new FakeInputs(rules),
            new RecalculationScheduler(new FixedScope([new("submission-1", null)]), queue),
            NullLogger<GradingRecalculationTrigger>.Instance);

    private static PublishedGradingRules Rules(long version) => new(
        ImportedGradingRules.Create(
            """{"schemaVersion":1,"examId":"synthetic-exam","examName":"合成考试","subjects":[{"subjectId":"s","name":"科目","maxScore":"1"}],"groups":[{"groupId":"g","subjectId":"s","name":"单选","type":"single","order":1,"questionCount":1,"maxScore":"1","scoring":{"oneCorrect":{"correct":"1","blank":"0","wrong":"0"}}}],"questions":[{"questionId":"q1","questionNo":1,"subjectId":"s","groupId":"g","source":"questions/合成考试_1.tex","options":["A","B"]}],"resourceRoots":[]}""",
            """{"schemaVersion":1,"examId":"synthetic-exam","answers":[{"questionId":"q1","questionNo":1,"correctOptionIds":["A"]}]}"""),
        version);

    private sealed class FakeInputs(PublishedGradingRules? rules) : IGradingInputs
    {
        public ValueTask<FinalSubmission?> LoadSubmissionAsync(string submissionId, CancellationToken ct = default) =>
            ValueTask.FromResult<FinalSubmission?>(null);

        public ValueTask<PublishedGradingRules?> LoadRulesAsync(string examId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(rules);
        }
    }

    private sealed class FixedScope(IReadOnlyList<SubmissionGradingState> states) : IGradingScope
    {
        public ValueTask<IReadOnlyList<SubmissionGradingState>> ListStatesAsync(string examId,
            CancellationToken ct = default) => ValueTask.FromResult(states);
    }

    private sealed class ThrowingScope : IGradingScope
    {
        public ValueTask<IReadOnlyList<SubmissionGradingState>> ListStatesAsync(string examId,
            CancellationToken ct = default) => throw new InvalidOperationException("合成调度故障");
    }

    private sealed class RecordingQueue : IRecalculationQueue
    {
        public List<(string SubmissionId, long Version)> Enqueued { get; } = [];

        public ValueTask<bool> EnsureQueuedAsync(string examId, string submissionId, long targetVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Enqueued.Add((submissionId, targetVersion));
            return ValueTask.FromResult(true);
        }
    }
}
