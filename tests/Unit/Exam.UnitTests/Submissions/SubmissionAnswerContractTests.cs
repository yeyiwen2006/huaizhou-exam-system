using System.Text.Json;
using Exam.Application.Submissions;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Domain.Submissions;

namespace Exam.UnitTests.Submissions;

public sealed class SubmissionAnswerContractTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(999)]
    public async Task UndefinedWireAnswerKindIsRejectedBeforeProgressOrFinalPersistence(int kind)
    {
        // JsonStringEnumConverter also accepts numeric tokens; validate the actual public contract.
        var value = JsonSerializer.Deserialize<AnswerValue>($"{{\"kind\":{kind},\"optionIds\":[]}}", JsonDefaults.Create())!;
        var store = new InMemorySubmissionStore();
        var queue = new InMemoryGradingTaskQueue();
        await store.SetAuthorizationStateAsync(new("exam", "candidate", "participation", 1, "device", 1,
            ParticipationBusinessState.Answering, 1, 1));
        using var service = new SubmissionService(store, queue);
        var answers = new Dictionary<string, AnswerValue> { ["q1"] = value };
        var progress = await service.SyncProgressAsync(new("progress-request", "exam", "candidate", "participation",
            1, "device", 1, 1, 1, answers, "q1"));
        var final = await service.SubmitAsync(new("submit-request", "submission", "exam", "candidate", "participation",
            1, "device", 1, 1, answers));
        Assert.False(progress.IsSuccess);
        Assert.Equal(SubmissionErrorCode.InvalidRequest, progress.ErrorCode);
        Assert.False(final.IsSuccess);
        Assert.Equal(SubmissionErrorCode.InvalidRequest, final.ErrorCode);
        Assert.Null(await store.FindProgressAsync("participation"));
        Assert.Null(await store.FindSubmissionAsync("exam", "candidate"));
    }

    [Theory]
    [InlineData(AnswerKind.Empty)]
    [InlineData(AnswerKind.Abstain)]
    [InlineData(AnswerKind.Selected)]
    public void DeclaredAnswerKindsRetainTheirExistingWireShape(AnswerKind kind)
    {
        var value = new AnswerValue(kind, kind == AnswerKind.Selected ? ["A"] : []);
        var json = JsonSerializer.Serialize(value, JsonDefaults.Create());
        var restored = JsonSerializer.Deserialize<AnswerValue>(json, JsonDefaults.Create())!;
        Assert.Equal(value.Kind, restored.Kind);
        Assert.Equal(value.OptionIds, restored.OptionIds);
        Assert.Null(SubmissionPolicy.ValidateAnswers(new Dictionary<string, AnswerValue> { ["q1"] = restored }));
    }
}
