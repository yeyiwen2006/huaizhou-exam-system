using Exam.Application.Scoring;
using Exam.Contracts.Grading;
using Exam.Domain.Scoring;

namespace Exam.UnitTests.Scoring;

public sealed class GradingLeaseBoundaryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task ReclaimedLeaseRejectsOldCompletionAndFailure()
    {
        var source = new InMemoryGradingTaskSource();
        source.Enqueue("s", 1);
        var old = Assert.Single(await source.ClaimAsync(1, Now, Lease));
        var current = Assert.Single(await source.ClaimAsync(1, Now + Lease, Lease));
        Assert.False(await source.CompleteAsync(old));
        Assert.False(await source.FailAsync(old, "stale failure"));
        Assert.Empty(await source.ClaimAsync(1, Now + Lease + TimeSpan.FromSeconds(1), Lease));
        Assert.Null(source.LastErrorOf("s"));
        Assert.True(await source.CompleteAsync(current));
        Assert.False(await source.FailAsync(current, "late failure"));
        Assert.False(await source.CompleteAsync(current));
        Assert.Null(source.LastErrorOf("s"));
    }

    [Fact]
    public async Task LaterConsumerCannotShortenExistingLease()
    {
        var source = new InMemoryGradingTaskSource();
        source.Enqueue("s", 1);
        Assert.Single(await source.ClaimAsync(1, Now, TimeSpan.FromMinutes(5)));
        Assert.Empty(await source.ClaimAsync(1, Now.AddMinutes(1), TimeSpan.FromSeconds(1)));
        Assert.Single(await source.ClaimAsync(1, Now.AddMinutes(5), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task ReleasedLeaseCannotCompleteWithoutReclaiming()
    {
        var source = new InMemoryGradingTaskSource();
        source.Enqueue("s", 1);
        var claim = Assert.Single(await source.ClaimAsync(1, Now, Lease));
        Assert.True(await source.FailAsync(claim, "retry"));
        Assert.False(await source.CompleteAsync(claim));
        Assert.Single(await source.ClaimAsync(1, Now, Lease));
    }

    [Fact]
    public async Task RepeatedlyFailingHeadDoesNotStarveUnattemptedTasks()
    {
        var source = new InMemoryGradingTaskSource();
        source.Enqueue("bad", 1);
        source.Enqueue("good", 1);
        var first = Assert.Single(await source.ClaimAsync(1, Now, Lease));
        Assert.Equal("bad", first.SubmissionId);
        Assert.True(await source.FailAsync(first, "not ready"));
        Assert.Equal("good", Assert.Single(await source.ClaimAsync(1, Now, Lease)).SubmissionId);
    }

    [Fact]
    public async Task ConcurrentConsumersDoNotShareAnUnexpiredLease()
    {
        var source = new InMemoryGradingTaskSource();
        for (var i = 0; i < 32; i++) source.Enqueue($"s-{i}", 1);
        var batches = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => Task.Run(async () => await source.ClaimAsync(4, Now, Lease))));
        var claims = batches.SelectMany(batch => batch).ToArray();
        Assert.Equal(32, claims.Length);
        Assert.Equal(32, claims.Select(task => task.SubmissionId).Distinct().Count());
        Assert.All(claims, task => Assert.Equal(1, task.AttemptCount));
    }

    [Fact]
    public async Task CancellationDoesNotConsumeOrCompleteTask()
    {
        var source = new InMemoryGradingTaskSource();
        source.Enqueue("s", 1);
        var canceled = new CancellationToken(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ClaimAsync(1, Now, Lease, canceled).AsTask());
        var claim = Assert.Single(await source.ClaimAsync(1, Now, Lease));
        Assert.Equal(1, claim.AttemptCount);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.CompleteAsync(claim, canceled).AsTask());
        Assert.True(await source.CompleteAsync(claim));
    }

    [Fact]
    public async Task UnclaimedTaskCannotBeAcknowledged()
    {
        var source = new InMemoryGradingTaskSource();
        source.Enqueue("s", 1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.CompleteAsync(new("s", 1, 0)).AsTask());
        Assert.False(await source.CompleteAsync(new("s", 1, 1)));
        Assert.Single(await source.ClaimAsync(1, Now, Lease));
    }

    [Fact]
    public async Task ErrorLengthDoesNotSplitUnicodeSurrogatePair()
    {
        var source = new InMemoryGradingTaskSource();
        source.Enqueue("s", 1);
        var claim = Assert.Single(await source.ClaimAsync(1, Now, Lease));
        await source.FailAsync(claim, new string('x', 1999) + "\U0001F642suffix");
        Assert.Equal(new string('x', 1999), source.LastErrorOf("s"));
    }

    [Fact]
    public async Task SameVersionRetryIsIdempotentButDifferentContentIsRejected()
    {
        var store = new InMemoryGradingResultStore();
        var result = new StoredGradingResult(new SubmissionGrade("s", "e", "c", 1,
            [new("q", 1, "subject", AnswerOutcome.Correct, "3")],
            [new("subject", "3", "3", false)], "3"), Now);
        await store.SaveAsync(result);
        await store.SaveAsync(result with { GradedAt = Now.AddHours(1) });
        Assert.Equal(Now, (await store.FindAsync("s"))!.GradedAt);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(result with
        {
            Grade = result.Grade with { ExactTotalScore = "4" }
        }).AsTask());
        Assert.Equal("3", (await store.FindAsync("s"))!.Grade.ExactTotalScore);
    }
}
