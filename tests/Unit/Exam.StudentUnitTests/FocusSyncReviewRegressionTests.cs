using Exam.Application.Submissions;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Domain.Submissions;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

public sealed partial class SubmissionSyncServiceTests
{
    [Fact]
    public async Task Review_RefocusDoesNotBreakTheRealSubmissionServiceVersionContract()
    {
        await using var fixture = await StartedAsync();
        var store = new InMemorySubmissionStore();
        await store.SetAuthorizationStateAsync(new SubmissionAuthorizationState("exam", "exam-candidate", "participation",
            1, "device", 1, ParticipationBusinessState.Answering, 1, 1));
        using var service = new SubmissionService(store, new InMemoryGradingTaskQueue());
        fixture.SubmissionApi.ProgressHandler = request =>
        {
            var result = service.SyncProgressAsync(request).AsTask().GetAwaiter().GetResult();
            return new("server", "node", 1, "correlation", result.Value,
                result.IsSuccess ? null : new ApiError(result.ErrorCode == SubmissionErrorCode.VersionConflict
                    ? ErrorCodes.VersionConflict : ErrorCodes.ServiceRecovering, result.Message));
        };
        var focus = new FocusTracker();
        await using var sync = fixture.CreateSync(focus);
        await sync.KickAsync();
        focus.OnFocusLost();
        focus.OnFocusRegained();
        await sync.KickAsync();
        Assert.Equal(SubmissionSyncPhase.Progressing, sync.Phase);
        Assert.Equal(AnswerSessionStatus.Active, fixture.Answers!.Status);
        var sent = fixture.SubmissionApi.ProgressRequests;
        Assert.Equal(2, sent.Count);
        Assert.Equal(sent[0].RequestId, sent[1].RequestId);
        Assert.True((await fixture.Answers.SetAnswerAsync("q1", AnswerValue.Selected("B"))).Succeeded);
        await sync.KickAsync();
        Assert.Equal(SubmissionSyncPhase.Progressing, sync.Phase);
        Assert.Equal(fixture.Answers.AnswerVersion, (await store.FindProgressAsync("participation"))!.AnswerVersion);
    }
}
