using Exam.Application.Scoring;
using Exam.Contracts.Grading;
using Exam.Domain.Scoring;

namespace Exam.UnitTests.Scoring;

/// <summary>
/// 计分持久化内存实现的行为用例。语义与 PostgreSQL 实现一一对应：
/// 结果按答卷去重、任务租约可过期重领、失败不删除任务。
/// </summary>
public sealed class InMemoryGradingStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ResultStore_SaveTwiceForSameSubmission_KeepsSingleRow()
    {
        // 重算时更新同一行，而不是追加：成绩不会被重复累计。
        var store = new InMemoryGradingResultStore();

        await store.SaveAsync(Result("submission-1", "exam-1", "candidate-1", version: 1, total: "3", at: Now));
        await store.SaveAsync(Result("submission-1", "exam-1", "candidate-1", version: 2, total: "5", at: Now));

        var stored = await store.FindAsync("submission-1");
        Assert.NotNull(stored);
        Assert.Equal(2, stored!.GradingVersion);
        Assert.Equal("5", stored.Grade.ExactTotalScore);

        // 旧版本不再可见：同一答卷只有一份结果。
        Assert.Empty(await store.ListAsync("exam-1", 1));
        Assert.Single(await store.ListAsync("exam-1", 2));
    }

    [Fact]
    public async Task ResultStore_ListFiltersByExamAndVersionInDeterministicOrder()
    {
        var store = new InMemoryGradingResultStore();
        await store.SaveAsync(Result("submission-b", "exam-1", "candidate-2", 3, "1", Now));
        await store.SaveAsync(Result("submission-a", "exam-1", "candidate-1", 3, "1", Now));
        await store.SaveAsync(Result("submission-c", "exam-2", "candidate-1", 3, "1", Now));
        await store.SaveAsync(Result("submission-d", "exam-1", "candidate-3", 4, "1", Now));

        var results = await store.ListAsync("exam-1", 3);

        Assert.Equal(
            ["submission-a", "submission-b"],
            results.Select(item => item.SubmissionId));
    }

    [Fact]
    public async Task ResultStore_FindUnknownSubmission_ReturnsNull()
    {
        var store = new InMemoryGradingResultStore();

        Assert.Null(await store.FindAsync("submission-missing"));
    }

    [Fact]
    public async Task TaskSource_ClaimMarksLeaseSoSecondClaimReturnsNothing()
    {
        var source = new InMemoryGradingTaskSource();
        source.Enqueue("submission-1", 1);

        var first = await source.ClaimAsync(10, Now, TimeSpan.FromMinutes(1));

        Assert.Single(first);
        Assert.Equal(1, first[0].AttemptCount);
        Assert.Empty(await source.ClaimAsync(10, Now.AddSeconds(30), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task TaskSource_LeaseExpiryAllowsReclaimAfterConsumerCrash()
    {
        // 消费者崩溃后租约过期，任务必须能被再次领取，否则会永久卡住。
        var source = new InMemoryGradingTaskSource();
        source.Enqueue("submission-1", 1);
        await source.ClaimAsync(10, Now, TimeSpan.FromMinutes(1));

        var reclaimed = await source.ClaimAsync(10, Now.AddMinutes(2), TimeSpan.FromMinutes(1));

        var task = Assert.Single(reclaimed);
        Assert.Equal(2, task.AttemptCount);
    }

    [Fact]
    public async Task TaskSource_CompleteRemovesTaskFromPending()
    {
        var source = new InMemoryGradingTaskSource();
        source.Enqueue("submission-1", 1);
        await source.ClaimAsync(10, Now, TimeSpan.FromMinutes(1));

        await source.CompleteAsync("submission-1", 1);

        Assert.Empty(await source.ClaimAsync(10, Now.AddMinutes(5), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task TaskSource_FailureKeepsTaskForRetryAndRecordsError()
    {
        // 需求：持久待发任务不得因重试次数被删除。
        var source = new InMemoryGradingTaskSource();
        source.Enqueue("submission-1", 1);
        await source.ClaimAsync(10, Now, TimeSpan.FromMinutes(1));

        await source.FailAsync("submission-1", 1, "规则尚未发布");

        Assert.Single(source.Snapshot());
        Assert.Equal("规则尚未发布", source.LastErrorOf("submission-1"));

        // 失败后租约立即释放，可以马上重试。
        var retried = await source.ClaimAsync(10, Now.AddSeconds(1), TimeSpan.FromMinutes(1));
        Assert.Single(retried);
    }

    [Fact]
    public async Task TaskSource_CompleteAfterFailureClearsPreviousError()
    {
        var source = new InMemoryGradingTaskSource();
        source.Enqueue("submission-1", 1);
        await source.ClaimAsync(10, Now, TimeSpan.FromMinutes(1));
        await source.FailAsync("submission-1", 1, "临时失败");

        await source.ClaimAsync(10, Now.AddSeconds(1), TimeSpan.FromMinutes(1));
        await source.CompleteAsync("submission-1", 1);

        Assert.Null(source.LastErrorOf("submission-1"));
    }

    [Fact]
    public async Task TaskSource_ClaimRespectsBatchBound()
    {
        var source = new InMemoryGradingTaskSource();
        for (var index = 0; index < 5; index++)
        {
            source.Enqueue($"submission-{index}", 1);
        }

        var claimed = await source.ClaimAsync(2, Now, TimeSpan.FromMinutes(1));

        Assert.Equal(2, claimed.Count);
        Assert.Equal(3, source.Snapshot().Count(task => task.AttemptCount == 0));
    }

    [Fact]
    public async Task TaskSource_DuplicateEnqueueIsIgnored()
    {
        var source = new InMemoryGradingTaskSource();
        source.Enqueue("submission-1", 1);
        source.Enqueue("submission-1", 1);

        Assert.Single(await source.ClaimAsync(10, Now, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Inputs_ReturnNullWhenSubmissionOrRulesAreAbsent()
    {
        var inputs = new InMemoryGradingInputs();

        Assert.Null(await inputs.LoadSubmissionAsync("submission-missing"));
        Assert.Null(await inputs.LoadRulesAsync("exam-missing"));
    }

    private static StoredGradingResult Result(
        string submissionId,
        string examId,
        string candidateId,
        long version,
        string total,
        DateTimeOffset at) =>
        new(
            new SubmissionGrade(
                submissionId,
                examId,
                candidateId,
                version,
                [new QuestionGrade("q-001", 1, "subject-1", AnswerOutcome.Correct, total)],
                [new SubjectGrade("subject-1", total, total, false)],
                total),
            at);
}
