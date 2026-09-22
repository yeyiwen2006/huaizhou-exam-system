using System.IO;
using Exam.Contracts.Submissions;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

/// <summary>
/// SqliteLocalStateStore 真实落盘测试：WAL + FULL 同步 + 独立工作线程。
/// 覆盖 B04 判据的存储层行为：崩溃恢复最后成功保存状态、按服务器/考试/考生隔离不串号、
/// 固定提交幂等保留原记录。
/// </summary>
public sealed class SqliteLocalStateStoreTests : IDisposable
{
    private static readonly LocalStateKey KeyA = new("srv-1", "exam-1", "cand-A");
    private static readonly LocalStateKey KeyB = new("srv-1", "exam-1", "cand-B");
    private static readonly LocalStateKey KeyOtherExam = new("srv-1", "exam-2", "cand-A");
    private static readonly LocalStateKey KeyOtherServer = new("srv-2", "exam-1", "cand-A");

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "hz-localstate-tests", Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "state.db");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static AnswerStateRecord State(
        long version,
        IReadOnlyDictionary<string, AnswerValue>? answers = null,
        string? currentQuestionId = null) =>
        new(
            "part-1",
            EntryVersion: 1,
            AuthorizationVersion: 1,
            PaperVersion: 1,
            version,
            currentQuestionId,
            DateTimeOffset.Now,
            answers ?? new Dictionary<string, AnswerValue>(),
            new HashSet<string>());

    private static PendingSnapshot Snapshot(long version, IReadOnlyDictionary<string, AnswerValue> answers) =>
        new(
            "exam-1",
            "cand-A",
            "part-1",
            1,
            "device-1",
            1,
            version,
            1,
            answers,
            null,
            DateTimeOffset.Now);

    [Fact]
    public async Task CrashRecovery_RestoresLastSavedState()
    {
        var answersV1 = new Dictionary<string, AnswerValue> { ["q1"] = AnswerValue.Selected("A") };
        var answersV2 = new Dictionary<string, AnswerValue>
        {
            ["q1"] = AnswerValue.Selected("A", "C"),
            ["q2"] = AnswerValue.Abstain(),
        };

        // 第一次运行：两次保存后"崩溃"（Dispose 即关闭连接）
        using (var store = new SqliteLocalStateStore(DatabasePath))
        {
            await store.SaveAnswerStateAsync(KeyA, State(1, answersV1), Snapshot(1, answersV1));
            await store.SaveAnswerStateAsync(KeyA, State(2, answersV2), Snapshot(2, answersV2));
            await store.SaveMarksAsync(KeyA, new[] { "q3" });
        }

        // 第二次运行（新实例新连接）：恢复最后成功保存状态
        using var recovered = new SqliteLocalStateStore(DatabasePath);
        var state = await recovered.LoadAnswerStateAsync(KeyA);

        Assert.NotNull(state);
        Assert.Equal(2, state!.AnswerVersion);
        AnswerAssert.Equal(AnswerValue.Selected("A", "C"), state.Answers["q1"]);
        AnswerAssert.Equal(AnswerValue.Abstain(), state.Answers["q2"]);
        Assert.Contains("q3", state.Marks);
        Assert.Equal("part-1", state.ParticipationId);
    }

    [Fact]
    public async Task Isolation_ByServerExamAndCandidate()
    {
        using var store = new SqliteLocalStateStore(DatabasePath);
        var answers = new Dictionary<string, AnswerValue> { ["q1"] = AnswerValue.Selected("A") };
        await store.SaveAnswerStateAsync(KeyA, State(2, answers), Snapshot(2, answers));
        await store.SaveMarksAsync(KeyA, new[] { "qA" });

        // 其他考生 / 其他考试 / 其他服务器均读不到（不串号）
        Assert.Null(await store.LoadAnswerStateAsync(KeyB));
        Assert.Null(await store.LoadAnswerStateAsync(KeyOtherExam));
        Assert.Null(await store.LoadAnswerStateAsync(KeyOtherServer));

        // 另一考生的标记不影响 A
        await store.SaveMarksAsync(KeyB, new[] { "qB" });
        var stateA = await store.LoadAnswerStateAsync(KeyA);
        Assert.Equal(new[] { "qA" }, stateA!.Marks);

        var stateB = await store.LoadAnswerStateAsync(KeyB);
        Assert.Equal(new[] { "qB" }, stateB!.Marks);
        Assert.Empty(stateB.Answers);
    }

    [Fact]
    public async Task PendingSnapshot_RoundTrip_WithLatestAnswers()
    {
        using var store = new SqliteLocalStateStore(DatabasePath);
        var answers = new Dictionary<string, AnswerValue>
        {
            ["q1"] = AnswerValue.Selected("A", "C"),
            ["q2"] = AnswerValue.Abstain(),
            ["q3"] = AnswerValue.Empty(),
        };

        await store.SaveAnswerStateAsync(KeyA, State(3, answers), Snapshot(3, answers));

        var snapshot = await store.LoadPendingSnapshotAsync(KeyA);
        Assert.NotNull(snapshot);
        Assert.Equal(3, snapshot!.AnswerVersion);
        Assert.Equal("exam-1", snapshot.ExamId);
        Assert.Equal("cand-A", snapshot.CandidateId);
        Assert.Equal("part-1", snapshot.ParticipationId);
        AnswerAssert.Equal(AnswerValue.Selected("A", "C"), snapshot.Answers["q1"]);
        AnswerAssert.Equal(AnswerValue.Abstain(), snapshot.Answers["q2"]);
        AnswerAssert.Equal(AnswerValue.Empty(), snapshot.Answers["q3"]);
    }

    [Fact]
    public async Task TryFixSubmission_KeepsFirstSubmission()
    {
        using var store = new SqliteLocalStateStore(DatabasePath);
        var answers = new Dictionary<string, AnswerValue> { ["q1"] = AnswerValue.Selected("A") };

        var first = new FixedSubmissionRecord("subm-1", 1, 2, answers, DateTimeOffset.Now);
        var (result1, stored1) = await store.TryFixSubmissionAsync(KeyA, first);
        Assert.True(result1.Succeeded);
        Assert.Equal("subm-1", stored1!.SubmissionId);

        // 换提交标识重复固定：保留原记录（已固定提交不生成新答案）
        var second = new FixedSubmissionRecord("subm-2", 1, 9, new Dictionary<string, AnswerValue>
        {
            ["q1"] = AnswerValue.Selected("B"),
        }, DateTimeOffset.Now);
        var (result2, stored2) = await store.TryFixSubmissionAsync(KeyA, second);

        Assert.True(result2.Succeeded);
        Assert.Equal("subm-1", stored2!.SubmissionId);
        AnswerAssert.Equal(AnswerValue.Selected("A"), stored2.FrozenAnswers["q1"]);

        var loaded = await store.LoadFixedSubmissionAsync(KeyA);
        Assert.Equal("subm-1", loaded!.SubmissionId);
    }

    [Fact]
    public async Task PreparationRecord_RoundTrip()
    {
        using var store = new SqliteLocalStateStore(DatabasePath);
        var record = new PreparationRecord(
            PaperVersion: 3,
            InstructionsVersion: 2,
            PackageFile: @"C:\data\3.pkg",
            ParticipationId: "part-9",
            Phase: "ConfirmedWaiting",
            DateTimeOffset.Now);

        Assert.Null(await store.LoadPreparationAsync(KeyA));
        Assert.True((await store.SavePreparationAsync(KeyA, record)).Succeeded);

        var loaded = await store.LoadPreparationAsync(KeyA);
        Assert.Equal(3, loaded!.PaperVersion);
        Assert.Equal(2, loaded.InstructionsVersion);
        Assert.Equal("part-9", loaded.ParticipationId);
        Assert.Equal("ConfirmedWaiting", loaded.Phase);

        // 更新（同键覆盖）
        Assert.True((await store.SavePreparationAsync(KeyA, record with { Phase = "Unlocked" })).Succeeded);
        Assert.Equal("Unlocked", (await store.LoadPreparationAsync(KeyA))!.Phase);
    }

    [Fact]
    public async Task SaveMarks_ReplacesWholeSet()
    {
        using var store = new SqliteLocalStateStore(DatabasePath);

        await store.SaveMarksAsync(KeyA, new[] { "q1", "q2" });
        await store.SaveMarksAsync(KeyA, new[] { "q2", "q3" }); // 取消 q1、新增 q3

        var state = await store.LoadAnswerStateAsync(KeyA);
        Assert.Equal(new[] { "q2", "q3" }, state!.Marks);
    }

    [Fact]
    public async Task ConcurrentSaves_OnWorkerThread_AreAtomic()
    {
        using var store = new SqliteLocalStateStore(DatabasePath);

        // 并发提交多个版本（每次整体替换答案并写入与版本同构的标记值）
        var tasks = Enumerable.Range(1, 20).Select(v =>
        {
            var answers = new Dictionary<string, AnswerValue>
            {
                ["q0"] = AnswerValue.Selected($"v{v}"),
            };
            return (version: v, task: store.SaveAnswerStateAsync(
                KeyA, State(v, answers), Snapshot(v, answers)));
        }).ToArray();

        foreach (var (_, task) in tasks)
        {
            Assert.True((await task).Succeeded);
        }

        // 最终读取：答案与其版本标记一致（不存在版本/答案交错的半事务）
        var state = await store.LoadAnswerStateAsync(KeyA);
        Assert.NotNull(state);
        var versionMarker = state!.Answers["q0"].OptionIds.Single();
        Assert.Equal($"v{state.AnswerVersion}", versionMarker);

        var snapshot = await store.LoadPendingSnapshotAsync(KeyA);
        Assert.Equal(state.AnswerVersion, snapshot!.AnswerVersion);
        Assert.Equal(versionMarker, snapshot.Answers["q0"].OptionIds.Single());
    }
}
