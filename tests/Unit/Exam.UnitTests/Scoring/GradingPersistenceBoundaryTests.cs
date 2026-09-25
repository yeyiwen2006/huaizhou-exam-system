using Exam.Application.Scoring;
using Exam.Contracts.Grading;
using Exam.Domain.Scoring;

namespace Exam.UnitTests.Scoring;

public sealed class GradingPersistenceBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task OlderWorkerCannotOverwriteNewerResult()
    {
        var store = new InMemoryGradingResultStore();
        await store.SaveAsync(Result(2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(Result(1)).AsTask());
        Assert.Equal(2, (await store.FindAsync("submission"))!.GradingVersion);
    }

    [Fact]
    public async Task SubmissionIdentityCannotBeReassigned()
    {
        var store = new InMemoryGradingResultStore();
        var original = Result(1);
        await store.SaveAsync(original);
        var invalid = original with { Grade = original.Grade with { CandidateId = "other", GradingVersion = 2 } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(invalid).AsTask());
        Assert.Equal("candidate", (await store.FindAsync("submission"))!.Grade.CandidateId);
    }

    [Fact]
    public async Task SavedResultDoesNotAliasCallerOwnedCollections()
    {
        var store = new InMemoryGradingResultStore();
        var result = Result(1);
        var questions = result.Grade.Questions.ToList();
        await store.SaveAsync(result with { Grade = result.Grade with { Questions = questions } });
        questions.Clear();
        Assert.Single((await store.FindAsync("submission"))!.Grade.Questions);
    }

    private static StoredGradingResult Result(long version) => new(
        new SubmissionGrade("submission", "exam", "candidate", version,
            [new("question", 1, "subject", AnswerOutcome.Correct, "3")],
            [new("subject", "3", "3", false)], "3"), Now);
}
