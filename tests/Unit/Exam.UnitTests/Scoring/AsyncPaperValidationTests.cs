using Exam.Application.Submissions;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Domain.Submissions;

namespace Exam.UnitTests.Scoring;

public sealed class AsyncPaperValidationTests
{
    private static readonly PaperStructure Structure = new([new("q", 1, "s", "g", "single", ["A", "B"])]);
    private static readonly SyncProgressRequest Request = new("request", "exam", "candidate", "participation",
        1, "device", 1, 1, 1, new Dictionary<string, AnswerValue> { ["q"] = AnswerValue.Selected("A") }, "q");

    private static async Task<InMemorySubmissionStore> StoreAsync()
    {
        var store = new InMemorySubmissionStore();
        await store.SetAuthorizationStateAsync(new("exam", "candidate", "participation", 1, "device", 1,
            ParticipationBusinessState.Answering, 1, 1));
        return store;
    }

    [Fact]
    public async Task ReaderIsAwaitedBeforeProgressIsSaved()
    {
        var store = await StoreAsync();
        var ready = new TaskCompletionSource<PaperStructure?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new SubmissionService(store, new InMemoryGradingTaskQueue(),
            paperReader: _ => throw new InvalidOperationException("The synchronous reader must not be used."),
            asyncPaperReader: (_, token) => new(ready.Task.WaitAsync(token)));
        var pending = service.SyncProgressAsync(Request).AsTask();
        Assert.False(pending.IsCompleted);
        Assert.Null(await store.FindProgressAsync("participation"));
        ready.SetResult(Structure);
        Assert.True((await pending).IsSuccess);
        Assert.NotNull(await store.FindProgressAsync("participation"));
    }

    [Fact]
    public async Task CancellationDoesNotWriteAndReleasesAdmissionSlot()
    {
        var store = await StoreAsync();
        var ready = new TaskCompletionSource<PaperStructure?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service = new SubmissionService(store, new InMemoryGradingTaskQueue(), maxConcurrentWrites: 1,
            asyncPaperReader: (_, token) => new(ready.Task.WaitAsync(token)));
        using var cancellation = new CancellationTokenSource();
        var pending = service.SyncProgressAsync(Request, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(await store.FindProgressAsync("participation"));
        ready.SetResult(Structure);
        Assert.True((await service.SyncProgressAsync(Request)).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrFailedPaperReadNeverWritesAnswers(bool failure)
    {
        var store = await StoreAsync();
        using var service = new SubmissionService(store, new InMemoryGradingTaskQueue(),
            asyncPaperReader: (_, _) => failure
                ? ValueTask.FromException<PaperStructure?>(new IOException("Synthetic read failure"))
                : ValueTask.FromResult<PaperStructure?>(null));
        var result = await service.SyncProgressAsync(Request);
        Assert.Equal(failure ? SubmissionErrorCode.PersistenceFailed : SubmissionErrorCode.InvalidRequest, result.ErrorCode);
        Assert.Null(await store.FindProgressAsync("participation"));
    }

    [Fact]
    public async Task AsyncValidationRejectsUnknownOptionsBeforeProgressOrFinalPersistence()
    {
        var store = await StoreAsync();
        using var service = new SubmissionService(store, new InMemoryGradingTaskQueue(),
            asyncPaperReader: (_, _) => ValueTask.FromResult<PaperStructure?>(Structure));
        var invalid = new Dictionary<string, AnswerValue> { ["q"] = AnswerValue.Selected("Z") };
        var progress = await service.SyncProgressAsync(Request with { Answers = invalid });
        var final = await service.SubmitAsync(new("final", "submission", "exam", "candidate", "participation",
            1, "device", 1, 1, invalid));
        Assert.Equal(SubmissionErrorCode.InvalidRequest, progress.ErrorCode);
        Assert.Equal(SubmissionErrorCode.InvalidRequest, final.ErrorCode);
        Assert.Null(await store.FindProgressAsync("participation"));
        Assert.Null(await store.FindSubmissionAsync("exam", "candidate"));
    }

    [Fact]
    public async Task FinalReceiptReplayDoesNotRequireRereadingPaperOrReplaceAnswers()
    {
        var store = await StoreAsync();
        var reads = 0;
        using var service = new SubmissionService(store, new InMemoryGradingTaskQueue(),
            asyncPaperReader: (_, _) => ++reads == 1
                ? ValueTask.FromResult<PaperStructure?>(Structure)
                : ValueTask.FromException<PaperStructure?>(new IOException("Synthetic later outage")));
        var request = new SubmitFinalAnswersRequest("final", "submission", "exam", "candidate", "participation",
            1, "device", 1, 1, Request.Answers);
        var original = await service.SubmitAsync(request);
        var replay = await service.SubmitAsync(request with { FrozenAnswers = new Dictionary<string, AnswerValue>() });
        Assert.True(original.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.True(replay.IsReplay);
        Assert.Equal(original.Value, replay.Value);
        Assert.Equal(1, reads);
        Assert.Single((await store.FindSubmissionAsync("exam", "candidate"))!.FrozenAnswers);
    }
}
