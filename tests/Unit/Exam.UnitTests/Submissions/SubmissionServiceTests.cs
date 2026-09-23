using Exam.Application.Submissions;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Domain.Submissions;

namespace Exam.UnitTests.Submissions;

public sealed class SubmissionServiceTests
{
    [Fact]
    public async Task Progress_NewerVersionWinsAndOldVersionIsRejected()
    {
        await using var fixture = await SubmissionFixture.CreateAsync();
        var first = await fixture.Service.SyncProgressAsync(fixture.Progress("progress-1", 2, "A"));
        var newer = await fixture.Service.SyncProgressAsync(fixture.Progress("progress-2", 3, "B"));
        var old = await fixture.Service.SyncProgressAsync(fixture.Progress("progress-3", 1, "C"));
        var stored = await fixture.Store.FindProgressAsync("participation-1");

        Assert.True(first.IsSuccess);
        Assert.True(newer.IsSuccess);
        Assert.False(old.IsSuccess);
        Assert.Equal(SubmissionErrorCode.VersionConflict, old.ErrorCode);
        Assert.Equal(3, stored!.AnswerVersion);
        Assert.Equal("B", Assert.Single(stored.Answers["q-1"].OptionIds));
    }

    [Fact]
    public async Task Progress_RequestIdReplayReturnsOriginalPersistedResult()
    {
        await using var fixture = await SubmissionFixture.CreateAsync();
        var first = await fixture.Service.SyncProgressAsync(fixture.Progress("same-request", 1, "A"));
        var replay = await fixture.Service.SyncProgressAsync(fixture.Progress("same-request", 99, "D"));
        var stored = await fixture.Store.FindProgressAsync("participation-1");

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.True(replay.IsReplay);
        Assert.Equal(1, replay.Value!.AnswerVersion);
        Assert.Equal(1, stored!.AnswerVersion);
        Assert.Equal("A", Assert.Single(stored.Answers["q-1"].OptionIds));
    }

    [Fact]
    public async Task ProgressRequestId_CannotReplayAnotherParticipationResult()
    {
        await using var fixture = await SubmissionFixture.CreateAsync();
        var original = await fixture.Service.SyncProgressAsync(
            fixture.Progress("shared-request", 1, "A"));
        await fixture.Store.SetAuthorizationStateAsync(new SubmissionAuthorizationState(
            "exam-1",
            "candidate-2",
            "participation-2",
            1,
            "device-2",
            1,
            ParticipationBusinessState.Answering,
            1,
            5));
        var crossIdentity = new SyncProgressRequest(
            "shared-request",
            "exam-1",
            "candidate-2",
            "participation-2",
            1,
            "device-2",
            1,
            1,
            1,
            Answer("B"),
            "q-1");

        var replayAttempt = await fixture.Service.SyncProgressAsync(crossIdentity);

        Assert.True(original.IsSuccess);
        Assert.False(replayAttempt.IsSuccess);
        Assert.Equal(SubmissionErrorCode.AuthorizationRevoked, replayAttempt.ErrorCode);
        Assert.Null(replayAttempt.Value);
        Assert.Null(await fixture.Store.FindProgressAsync("participation-2"));
    }

    [Fact]
    public async Task ConcurrentProgressWrites_KeepHighestAnswerVersion()
    {
        await using var fixture = await SubmissionFixture.CreateAsync();
        var writes = Enumerable.Range(1, 40)
            .Select(version => fixture.Service.SyncProgressAsync(
                fixture.Progress($"progress-{version}", version, version.ToString())).AsTask())
            .ToArray();

        await Task.WhenAll(writes);
        var stored = await fixture.Store.FindProgressAsync("participation-1");

        Assert.NotNull(stored);
        Assert.Equal(40, stored!.AnswerVersion);
        Assert.Equal("40", Assert.Single(stored.Answers["q-1"].OptionIds));
    }

    [Fact]
    public async Task Submission_FreezesAnswersAndPreventsLaterProgressOverwrite()
    {
        await using var fixture = await SubmissionFixture.CreateAsync();
        var mutableOptions = new List<string> { "A" };
        var mutableAnswers = new Dictionary<string, AnswerValue>
        {
            ["q-1"] = new(AnswerKind.Selected, mutableOptions)
        };
        var request = fixture.Submission("submit-request-1", "submission-1", mutableAnswers);

        var submitted = await fixture.Service.SubmitAsync(request);
        mutableOptions[0] = "D";
        mutableAnswers["q-1"] = AnswerValue.Selected("C");
        var laterProgress = await fixture.Service.SyncProgressAsync(fixture.Progress("progress-late", 10, "B"));
        var stored = await fixture.Store.FindSubmissionAsync("exam-1", "candidate-1");

        Assert.True(submitted.IsSuccess);
        Assert.False(laterProgress.IsSuccess);
        Assert.Equal(SubmissionErrorCode.AlreadySubmitted, laterProgress.ErrorCode);
        Assert.Equal("A", Assert.Single(stored!.FrozenAnswers["q-1"].OptionIds));
    }

    [Fact]
    public async Task LostReceipt_RetryAndQueryReturnOriginalWithoutDuplicateGrading()
    {
        await using var fixture = await SubmissionFixture.CreateAsync();
        var first = await fixture.Service.SubmitAsync(
            fixture.Submission("submit-request-1", "submission-1", Answer("A")));
        var replaySameRequest = await fixture.Service.SubmitAsync(
            fixture.Submission("submit-request-1", "submission-1", Answer("D")));
        var replayNewIds = await fixture.Service.SubmitAsync(
            fixture.Submission("submit-request-2", "submission-2", Answer("C")));
        var query = await fixture.Service.FindReceiptAsync("exam-1", "candidate-1");
        var stored = await fixture.Store.FindSubmissionAsync("exam-1", "candidate-1");

        Assert.True(first.IsSuccess);
        Assert.True(replaySameRequest.IsReplay);
        Assert.True(replayNewIds.IsReplay);
        Assert.Equal(first.Value, replaySameRequest.Value);
        Assert.Equal(first.Value, replayNewIds.Value);
        Assert.Equal(first.Value, query.Value);
        Assert.Equal("submission-1", stored!.SubmissionId);
        Assert.Equal("A", Assert.Single(stored.FrozenAnswers["q-1"].OptionIds));
        Assert.Equal(1, fixture.GradingQueue.Count);
    }

    [Fact]
    public async Task SubmissionRequestId_CannotExposeAnotherCandidateReceipt()
    {
        await using var fixture = await SubmissionFixture.CreateAsync();
        var original = await fixture.Service.SubmitAsync(
            fixture.Submission("shared-request", "submission-1", Answer("A")));
        await fixture.Store.SetAuthorizationStateAsync(new SubmissionAuthorizationState(
            "exam-1",
            "candidate-2",
            "participation-2",
            1,
            "device-2",
            1,
            ParticipationBusinessState.Answering,
            1,
            5));
        var crossIdentity = new SubmitFinalAnswersRequest(
            "shared-request",
            "submission-2",
            "exam-1",
            "candidate-2",
            "participation-2",
            1,
            "device-2",
            1,
            1,
            Answer("B"));

        var replayAttempt = await fixture.Service.SubmitAsync(crossIdentity);

        Assert.True(original.IsSuccess);
        Assert.False(replayAttempt.IsSuccess);
        Assert.Equal(SubmissionErrorCode.AuthorizationRevoked, replayAttempt.ErrorCode);
        Assert.Null(replayAttempt.Value);
        Assert.Null(await fixture.Store.FindSubmissionAsync("exam-1", "candidate-2"));
    }

    [Fact]
    public async Task ConcurrentSubmissions_ProduceOneFinalAnswerForExamCandidate()
    {
        await using var fixture = await SubmissionFixture.CreateAsync();
        var attempts = Enumerable.Range(0, 40)
            .Select(index => fixture.Service.SubmitAsync(
                fixture.Submission($"request-{index}", $"submission-{index}", Answer((index % 4).ToString()))).AsTask())
            .ToArray();

        var results = await Task.WhenAll(attempts);
        var stored = await fixture.Store.FindSubmissionAsync("exam-1", "candidate-1");

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Single(results.Select(result => result.Value!.ReceiptId).Distinct(StringComparer.Ordinal));
        Assert.NotNull(stored);
        Assert.Equal(1, fixture.GradingQueue.Count);
    }

    [Fact]
    public async Task SameSubmissionId_CannotBeClaimedByAnotherCandidate()
    {
        await using var fixture = await SubmissionFixture.CreateAsync();
        await fixture.Service.SubmitAsync(
            fixture.Submission("request-1", "shared-submission", Answer("A")));
        await fixture.Store.SetAuthorizationStateAsync(new SubmissionAuthorizationState(
            "exam-1",
            "candidate-2",
            "participation-2",
            1,
            "device-2",
            1,
            ParticipationBusinessState.Answering,
            1,
            1));
        var conflicting = new SubmitFinalAnswersRequest(
            "request-2",
            "shared-submission",
            "exam-1",
            "candidate-2",
            "participation-2",
            1,
            "device-2",
            1,
            1,
            Answer("B"));

        var result = await fixture.Service.SubmitAsync(conflicting);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionErrorCode.SubmissionIdConflict, result.ErrorCode);
    }

    [Fact]
    public async Task RevokedAuthorization_CannotWriteProgressOrSubmission()
    {
        await using var fixture = await SubmissionFixture.CreateAsync();
        await fixture.Store.SetAuthorizationStateAsync(fixture.Authorization with
        {
            AuthorizedDeviceId = "device-2",
            AuthorizationVersion = 2
        });

        var progress = await fixture.Service.SyncProgressAsync(fixture.Progress("progress-1", 1, "A"));
        var submission = await fixture.Service.SubmitAsync(
            fixture.Submission("request-1", "submission-1", Answer("A")));
        await fixture.Store.SetAuthorizationStateAsync(fixture.Authorization);
        var failedRequestReplay = await fixture.Service.SyncProgressAsync(
            fixture.Progress("progress-1", 99, "D"));

        Assert.Equal(SubmissionErrorCode.AuthorizationRevoked, progress.ErrorCode);
        Assert.Equal(SubmissionErrorCode.AuthorizationRevoked, submission.ErrorCode);
        Assert.Equal(SubmissionErrorCode.AuthorizationRevoked, failedRequestReplay.ErrorCode);
    }

    [Fact]
    public async Task SubmissionUsingOlderPaperVersion_IsStillAcceptedAndRecorded()
    {
        await using var fixture = await SubmissionFixture.CreateAsync(currentPaperVersion: 3);
        var request = fixture.Submission("request-1", "submission-1", Answer("A")) with
        {
            PaperVersion = 1
        };

        var result = await fixture.Service.SubmitAsync(request);
        var stored = await fixture.Store.FindSubmissionAsync("exam-1", "candidate-1");

        Assert.True(result.IsSuccess);
        Assert.Equal(1, stored!.PaperVersion);
    }

    [Fact]
    public async Task InvalidAnswerShape_IsRejectedBeforePersistence()
    {
        await using var fixture = await SubmissionFixture.CreateAsync();
        var invalid = fixture.Submission(
            "request-1",
            "submission-1",
            new Dictionary<string, AnswerValue>
            {
                ["q-1"] = new(AnswerKind.Empty, new[] { "A" })
            });

        var result = await fixture.Service.SubmitAsync(invalid);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionErrorCode.InvalidRequest, result.ErrorCode);
        Assert.Null(await fixture.Store.FindSubmissionAsync("exam-1", "candidate-1"));
    }

    [Fact]
    public async Task PersistenceFailure_NeverReturnsSuccessfulReceipt()
    {
        using var service = new SubmissionService(
            new ThrowingSubmissionStore(),
            new InMemoryGradingTaskQueue());
        var request = new SubmitFinalAnswersRequest(
            "request-1",
            "submission-1",
            "exam-1",
            "candidate-1",
            "participation-1",
            1,
            "device-1",
            1,
            1,
            Answer("A"));

        var result = await service.SubmitAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Equal(SubmissionErrorCode.PersistenceFailed, result.ErrorCode);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task WriteConcurrencyIsBounded_InsteadOfGrowingAnUnboundedWaitQueue()
    {
        var inner = new InMemorySubmissionStore();
        var authorization = new SubmissionAuthorizationState(
            "exam-1",
            "candidate-1",
            "participation-1",
            1,
            "device-1",
            1,
            ParticipationBusinessState.Answering,
            1,
            1);
        await inner.SetAuthorizationStateAsync(authorization);
        var blocking = new BlockingSubmissionStore(inner);
        using var service = new SubmissionService(blocking, new InMemoryGradingTaskQueue(), maxConcurrentWrites: 1,
            maxQueuedWritesPerClass: 0);
        var firstRequest = new SubmitFinalAnswersRequest(
            "request-1", "submission-1", "exam-1", "candidate-1", "participation-1",
            1, "device-1", 1, 1, Answer("A"));
        var first = service.SubmitAsync(firstRequest).AsTask();
        await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var rejected = await service.SubmitAsync(firstRequest with
        {
            RequestId = "request-2",
            SubmissionId = "submission-2"
        });
        blocking.Release.TrySetResult(true);
        var completed = await first;

        Assert.False(rejected.IsSuccess);
        Assert.Equal(SubmissionErrorCode.ServiceRecovering, rejected.ErrorCode);
        Assert.True(completed.IsSuccess);
    }

    [Fact]
    public async Task CollectionNotification_FiresOncePerNonEmptyRosterVersion()
    {
        await using var fixture = await SubmissionFixture.CreateAsync();
        var completionStore = new InMemoryCollectionCompletionStore(() => "collection-event");
        var tracker = new CollectionCompletionService(fixture.Store, completionStore);

        Assert.Null(await tracker.EvaluateAsync("exam-1", 1, Array.Empty<string>()));
        Assert.Null(await tracker.EvaluateAsync("exam-1", 1, new[] { "participation-1" }));
        await fixture.Service.SubmitAsync(
            fixture.Submission("request-1", "submission-1", Answer("A")));

        var first = await tracker.EvaluateAsync("exam-1", 1, new[] { "participation-1" });
        var duplicate = await tracker.EvaluateAsync("exam-1", 1, new[] { "participation-1" });
        await fixture.Store.SetAuthorizationStateAsync(new SubmissionAuthorizationState(
            "exam-1",
            "candidate-2",
            "participation-2",
            1,
            "device-2",
            1,
            ParticipationBusinessState.Answering,
            1,
            5));
        var incompleteNextVersion = await tracker.EvaluateAsync(
            "exam-1",
            2,
            new[] { "participation-1", "participation-2" });
        var secondSubmission = new SubmitFinalAnswersRequest(
            "request-2",
            "submission-2",
            "exam-1",
            "candidate-2",
            "participation-2",
            1,
            "device-2",
            1,
            1,
            Answer("B"));
        await fixture.Service.SubmitAsync(secondSubmission);
        var nextVersion = await tracker.EvaluateAsync(
            "exam-1",
            2,
            new[] { "participation-1", "participation-2" });

        Assert.NotNull(first);
        Assert.Equal(1, first!.ParticipantCount);
        Assert.Null(duplicate);
        Assert.Null(incompleteNextVersion);
        Assert.NotNull(nextVersion);
        Assert.Equal(2, nextVersion!.RosterVersion);
        Assert.Equal(2, nextVersion.ParticipantCount);
    }

    private static IReadOnlyDictionary<string, AnswerValue> Answer(string option) =>
        new Dictionary<string, AnswerValue> { ["q-1"] = AnswerValue.Selected(option) };

    private sealed class SubmissionFixture : IAsyncDisposable
    {
        private SubmissionFixture(
            InMemorySubmissionStore store,
            InMemoryGradingTaskQueue gradingQueue,
            SubmissionService service,
            SubmissionAuthorizationState authorization)
        {
            Store = store;
            GradingQueue = gradingQueue;
            Service = service;
            Authorization = authorization;
        }

        public InMemorySubmissionStore Store { get; }
        public InMemoryGradingTaskQueue GradingQueue { get; }
        public SubmissionService Service { get; }
        public SubmissionAuthorizationState Authorization { get; }

        public static async ValueTask<SubmissionFixture> CreateAsync(long currentPaperVersion = 1)
        {
            var store = new InMemorySubmissionStore();
            var queue = new InMemoryGradingTaskQueue();
            var service = new SubmissionService(store, queue, receiptIdFactory: () => "receipt-1");
            var authorization = new SubmissionAuthorizationState(
                "exam-1",
                "candidate-1",
                "participation-1",
                1,
                "device-1",
                1,
                ParticipationBusinessState.Answering,
                currentPaperVersion,
                5);
            await store.SetAuthorizationStateAsync(authorization);
            return new SubmissionFixture(store, queue, service, authorization);
        }

        public SyncProgressRequest Progress(string requestId, long answerVersion, string option) =>
            new(
                requestId,
                "exam-1",
                "candidate-1",
                "participation-1",
                1,
                "device-1",
                1,
                answerVersion,
                1,
                Answer(option),
                "q-1");

        public SubmitFinalAnswersRequest Submission(
            string requestId,
            string submissionId,
            IReadOnlyDictionary<string, AnswerValue> answers) =>
            new(
                requestId,
                submissionId,
                "exam-1",
                "candidate-1",
                "participation-1",
                1,
                "device-1",
                1,
                1,
                answers);

        public ValueTask DisposeAsync()
        {
            Service.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingSubmissionStore : ISubmissionStore
    {
        public ValueTask SetAuthorizationStateAsync(SubmissionAuthorizationState state, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<ProgressPersistenceResult> SaveProgressAsync(SyncProgressRequest request, DateTimeOffset persistedAt, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<ProgressPersistenceResult>(new IOException("disk full"));

        public ValueTask<SubmissionPersistenceResult> SaveSubmissionAsync(SubmitFinalAnswersRequest request, DateTimeOffset persistedAt, string receiptId, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<SubmissionPersistenceResult>(new IOException("disk full"));

        public ValueTask<PersistedProgress?> FindProgressAsync(string participationId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<PersistedProgress?>(null);

        public ValueTask<FinalSubmission?> FindSubmissionAsync(string examId, string candidateId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FinalSubmission?>(null);

        public ValueTask<FinalSubmission?> FindSubmissionByParticipationAsync(string participationId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<FinalSubmission?>(null);
    }

    private sealed class BlockingSubmissionStore(ISubmissionStore inner) : ISubmissionStore
    {
        public TaskCompletionSource<bool> Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask SetAuthorizationStateAsync(SubmissionAuthorizationState state, CancellationToken cancellationToken = default) =>
            inner.SetAuthorizationStateAsync(state, cancellationToken);

        public ValueTask<ProgressPersistenceResult> SaveProgressAsync(SyncProgressRequest request, DateTimeOffset persistedAt, CancellationToken cancellationToken = default) =>
            inner.SaveProgressAsync(request, persistedAt, cancellationToken);

        public async ValueTask<SubmissionPersistenceResult> SaveSubmissionAsync(SubmitFinalAnswersRequest request, DateTimeOffset persistedAt, string receiptId, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return await inner.SaveSubmissionAsync(request, persistedAt, receiptId, cancellationToken);
        }

        public ValueTask<PersistedProgress?> FindProgressAsync(string participationId, CancellationToken cancellationToken = default) =>
            inner.FindProgressAsync(participationId, cancellationToken);

        public ValueTask<FinalSubmission?> FindSubmissionAsync(string examId, string candidateId, CancellationToken cancellationToken = default) =>
            inner.FindSubmissionAsync(examId, candidateId, cancellationToken);

        public ValueTask<FinalSubmission?> FindSubmissionByParticipationAsync(string participationId, CancellationToken cancellationToken = default) =>
            inner.FindSubmissionByParticipationAsync(participationId, cancellationToken);
    }
}
