using Exam.Application.Scoring;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Domain.Scoring;

namespace Exam.UnitTests.Scoring;

public sealed partial class GradingCoordinatorTests
{
    [Fact]
    public async Task InputExceptionIsIsolatedAndDoesNotExposeDependencySecrets()
    {
        var (_, tasks, results, inputs) = Create();
        inputs.SetSubmission(Submission("good"));
        inputs.SetRules(ExamId, Rules());
        tasks.Enqueue("bad", 1);
        tasks.Enqueue("good", 1);
        var wrapped = new TestInputs(inputs, submission: (id, ct) => id == "bad"
            ? ValueTask.FromException<FinalSubmission?>(new IOException("PRIVATE-ANSWER-PASSWORD"))
            : inputs.LoadSubmissionAsync(id, ct));
        var report = await new GradingCoordinator(tasks, results, wrapped, timeProvider: new FixedTimeProvider(Now)).RunOnceAsync();
        Assert.Equal(1, report.Graded);
        Assert.Equal(1, report.Failed);
        Assert.NotNull(await results.FindAsync("good"));
        Assert.DoesNotContain("PRIVATE-ANSWER-PASSWORD", Assert.Single(report.FailureMessages));
        Assert.DoesNotContain("PRIVATE-ANSWER-PASSWORD", tasks.LastErrorOf("bad")!);
    }

    [Fact]
    public async Task ResultWriteFailureDoesNotCompleteTaskOrBlockNextSubmission()
    {
        var (_, tasks, results, inputs) = Create();
        inputs.SetSubmission(Submission("bad"));
        inputs.SetSubmission(Submission("good"));
        inputs.SetRules(ExamId, Rules());
        tasks.Enqueue("bad", 1);
        tasks.Enqueue("good", 1);
        var wrapped = new TestResults(results, (result, ct) => result.SubmissionId == "bad"
            ? ValueTask.FromException(new IOException("synthetic write failure")) : results.SaveAsync(result, ct));
        var report = await new GradingCoordinator(tasks, wrapped, inputs, timeProvider: new FixedTimeProvider(Now)).RunOnceAsync();
        Assert.Equal(1, report.Graded);
        Assert.Equal(1, report.Failed);
        Assert.Null(await results.FindAsync("bad"));
        Assert.NotNull(await results.FindAsync("good"));
        Assert.Equal("bad", Assert.Single(await tasks.ClaimAsync(2, Now.AddMinutes(10), TimeSpan.FromMinutes(1))).SubmissionId);
    }

    [Fact]
    public async Task CompletionAndFailureWriteExceptionsLeavePersistedResultRecoverable()
    {
        var (_, tasks, results, inputs) = Create();
        inputs.SetSubmission(Submission());
        inputs.SetRules(ExamId, Rules());
        tasks.Enqueue(SubmissionId, 1);
        var coordinator = new GradingCoordinator(new FailingAcknowledgements(tasks), results, inputs,
            timeProvider: new FixedTimeProvider(Now));
        var failed = await coordinator.RunOnceAsync();
        Assert.Equal(0, failed.Graded);
        Assert.Equal(1, failed.Failed);
        Assert.Contains("未能持久化", Assert.Single(failed.FailureMessages));
        Assert.NotNull(await results.FindAsync(SubmissionId));
        var restarted = new GradingCoordinator(tasks, results, inputs,
            timeProvider: new FixedTimeProvider(Now.AddMinutes(10)));
        Assert.Equal(1, (await restarted.RunOnceAsync()).Graded);
        Assert.Single(await results.ListAsync(ExamId, 1));
    }

    [Fact]
    public async Task LostClaimIsNeverReportedAsCompleted()
    {
        var (_, tasks, results, inputs) = Create();
        inputs.SetSubmission(Submission());
        inputs.SetRules(ExamId, Rules());
        tasks.Enqueue(SubmissionId, 1);
        PendingGradingTask? otherOwner = null;
        var wrapped = new TestResults(results, async (result, ct) =>
        {
            await results.SaveAsync(result, ct);
            otherOwner = Assert.Single(await tasks.ClaimAsync(1, Now.AddMinutes(2), TimeSpan.FromMinutes(1), ct));
        });
        var report = await new GradingCoordinator(tasks, wrapped, inputs, lease: TimeSpan.FromMinutes(1),
            timeProvider: new FixedTimeProvider(Now)).RunOnceAsync();
        Assert.Equal(0, report.Graded);
        Assert.Equal(1, report.Failed);
        Assert.NotNull(otherOwner);
        Assert.True(await tasks.CompleteAsync(otherOwner!));
    }

    [Fact]
    public async Task FutureTaskVersionIsNotCompletedUsingOlderPublishedRules()
    {
        var (coordinator, tasks, results, inputs) = Create();
        inputs.SetSubmission(Submission());
        inputs.SetRules(ExamId, Rules(gradingVersion: 1));
        tasks.Enqueue(SubmissionId, 2);
        var report = await coordinator.RunOnceAsync();
        Assert.Equal(0, report.Graded);
        Assert.Equal(1, report.Failed);
        Assert.Null(await results.FindAsync(SubmissionId));
    }

    [Fact]
    public async Task WrongSubmissionReturnedByAdapterCannotCompleteRequestedTask()
    {
        var (_, tasks, results, inputs) = Create();
        inputs.SetRules(ExamId, Rules());
        tasks.Enqueue("requested", 1);
        var wrapped = new TestInputs(inputs, submission: (_, _) => ValueTask.FromResult<FinalSubmission?>(Submission("different")));
        var report = await new GradingCoordinator(tasks, results, wrapped).RunOnceAsync();
        Assert.Equal(0, report.Graded);
        Assert.Equal(1, report.Failed);
        Assert.Null(await results.FindAsync("different"));
    }

    [Fact]
    public async Task CancellationDuringInputReadIsPropagatedAndTaskCanBeReclaimed()
    {
        var (_, tasks, results, inputs) = Create();
        tasks.Enqueue(SubmissionId, 1);
        using var cancellation = new CancellationTokenSource();
        var wrapped = new TestInputs(inputs, submission: (_, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled<FinalSubmission?>(cancellation.Token);
        });
        var coordinator = new GradingCoordinator(tasks, results, wrapped, timeProvider: new FixedTimeProvider(Now));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RunOnceAsync(cancellation.Token).AsTask());
        Assert.Null(tasks.LastErrorOf(SubmissionId));
        Assert.Null(await results.FindAsync(SubmissionId));
        Assert.Single(await tasks.ClaimAsync(1, Now.AddMinutes(10), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task InMemoryFrozenAnswersDoNotAliasCallerCollections()
    {
        var inputs = new InMemoryGradingInputs();
        var options = new List<string> { "A" };
        var answers = new Dictionary<string, AnswerValue> { ["q-001"] = new(AnswerKind.Selected, options) };
        inputs.SetSubmission(Submission() with { FrozenAnswers = answers });
        answers.Clear();
        options.Clear();
        var saved = (await inputs.LoadSubmissionAsync(SubmissionId))!;
        Assert.Equal("A", Assert.Single(saved.FrozenAnswers["q-001"].OptionIds));
    }

    private sealed class TestInputs(IGradingInputs inner,
        Func<string, CancellationToken, ValueTask<FinalSubmission?>>? submission = null,
        Func<string, CancellationToken, ValueTask<PublishedGradingRules?>>? rules = null) : IGradingInputs
    {
        public ValueTask<FinalSubmission?> LoadSubmissionAsync(string id, CancellationToken cancellationToken = default) =>
            submission?.Invoke(id, cancellationToken) ?? inner.LoadSubmissionAsync(id, cancellationToken);
        public ValueTask<PublishedGradingRules?> LoadRulesAsync(string id, CancellationToken cancellationToken = default) =>
            rules?.Invoke(id, cancellationToken) ?? inner.LoadRulesAsync(id, cancellationToken);
    }

    private sealed class TestResults(IGradingResultStore inner,
        Func<StoredGradingResult, CancellationToken, ValueTask> save) : IGradingResultStore
    {
        public ValueTask SaveAsync(StoredGradingResult result, CancellationToken cancellationToken = default) => save(result, cancellationToken);
        public ValueTask<StoredGradingResult?> FindAsync(string id, CancellationToken cancellationToken = default) => inner.FindAsync(id, cancellationToken);
        public ValueTask<IReadOnlyList<StoredGradingResult>> ListAsync(string id, long version, CancellationToken cancellationToken = default) => inner.ListAsync(id, version, cancellationToken);
    }

    private sealed class FailingAcknowledgements(IGradingTaskSource inner) : IGradingTaskSource
    {
        public ValueTask<IReadOnlyList<PendingGradingTask>> ClaimAsync(int maxCount, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken = default) =>
            inner.ClaimAsync(maxCount, now, lease, cancellationToken);
        public ValueTask<bool> CompleteAsync(PendingGradingTask task, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<bool>(new IOException("synthetic acknowledgement failure"));
        public ValueTask<bool> FailAsync(PendingGradingTask task, string error, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<bool>(new IOException("synthetic failure persistence error"));
    }
}
