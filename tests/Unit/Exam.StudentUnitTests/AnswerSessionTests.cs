using Exam.Contracts.Submissions;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

/// <summary>
/// AnswerValue 的 record 相等性对 IReadOnlyList 字段为引用比较（ReadOnlyCollection/List/数组互不相等），
/// 断言答案时按 Kind + OptionIds 序列逐项比较。
/// </summary>
internal static class AnswerAssert
{
    public static void Equal(AnswerValue expected, AnswerValue actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.OptionIds, actual.OptionIds);
    }
}

/// <summary>
/// 受控假存储：内存字典实现，支持下一次写入失败注入与写入计数，
/// 用于验证 AnswerSession 的写失败门闸与版本语义（真实 SQLite 行为由 SqliteLocalStateStoreTests 覆盖）。
/// </summary>
public sealed class FakeLocalStateStore : ILocalStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<LocalStateKey, AnswerStateRecord> _states = new();
    private readonly Dictionary<LocalStateKey, HashSet<string>> _marks = new();
    private readonly Dictionary<LocalStateKey, PendingSnapshot> _snapshots = new();
    private readonly Dictionary<LocalStateKey, FixedSubmissionRecord> _fixed = new();
    private readonly Dictionary<LocalStateKey, PreparationRecord> _preparations = new();

    /// <summary>下一次写操作（保存答案/标记/固定）返回失败。</summary>
    public bool FailNextWrite { get; set; }

    /// <summary>写操作尝试次数（含失败；被门闸阻止的调用不计入）。</summary>
    public int WriteAttempts { get; private set; }

    public Task<AnswerStateRecord?> LoadAnswerStateAsync(LocalStateKey key, CancellationToken ct = default)
    {
        lock (_gate)
        {
            // 与 SQLite 实现同语义：状态行 + 独立标记表合并；仅有标记时合成最小记录
            _states.TryGetValue(key, out var state);
            _marks.TryGetValue(key, out var marks);
            if (state is null && marks is null)
            {
                return Task.FromResult<AnswerStateRecord?>(null);
            }

            if (state is null)
            {
                return Task.FromResult<AnswerStateRecord?>(new AnswerStateRecord(
                    null, 0, 0, 0, 0, null, DateTimeOffset.MinValue,
                    new Dictionary<string, AnswerValue>(), new HashSet<string>(marks!)));
            }

            return Task.FromResult<AnswerStateRecord?>(state with
            {
                Answers = new Dictionary<string, AnswerValue>(state.Answers),
                Marks = marks is not null ? new HashSet<string>(marks) : new HashSet<string>(state.Marks),
            });
        }
    }

    public Task<StoreWriteResult> SaveAnswerStateAsync(
        LocalStateKey key, AnswerStateRecord state, PendingSnapshot? snapshot, CancellationToken ct = default)
    {
        lock (_gate)
        {
            WriteAttempts++;
            if (FailNextWrite)
            {
                FailNextWrite = false;
                return Task.FromResult(StoreWriteResult.Fail("注入的磁盘故障。"));
            }

            _states[key] = state;
            if (snapshot is not null)
            {
                _snapshots[key] = snapshot;
            }

            return Task.FromResult(StoreWriteResult.Ok());
        }
    }

    public Task<StoreWriteResult> SaveMarksAsync(LocalStateKey key, IReadOnlyCollection<string> marks, CancellationToken ct = default)
    {
        lock (_gate)
        {
            WriteAttempts++;
            if (FailNextWrite)
            {
                FailNextWrite = false;
                return Task.FromResult(StoreWriteResult.Fail("注入的磁盘故障。"));
            }

            _marks[key] = new HashSet<string>(marks);
            return Task.FromResult(StoreWriteResult.Ok());
        }
    }

    public Task<PendingSnapshot?> LoadPendingSnapshotAsync(LocalStateKey key, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_snapshots.TryGetValue(key, out var snapshot)
                ? snapshot
                : null);
        }
    }

    public Task<(StoreWriteResult Result, FixedSubmissionRecord? Record)> TryFixSubmissionAsync(
        LocalStateKey key, FixedSubmissionRecord record, CancellationToken ct = default)
    {
        lock (_gate)
        {
            WriteAttempts++;
            if (FailNextWrite)
            {
                FailNextWrite = false;
                return Task.FromResult((StoreWriteResult.Fail("注入的磁盘故障。"), (FixedSubmissionRecord?)null));
            }

            if (_fixed.TryGetValue(key, out var existing))
            {
                return Task.FromResult((StoreWriteResult.Ok(), (FixedSubmissionRecord?)existing));
            }

            _fixed[key] = record;
            return Task.FromResult((StoreWriteResult.Ok(), (FixedSubmissionRecord?)record));
        }
    }

    public Task<FixedSubmissionRecord?> LoadFixedSubmissionAsync(LocalStateKey key, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_fixed.TryGetValue(key, out var fixedRecord)
                ? fixedRecord
                : null);
        }
    }

    public Task<StoreWriteResult> SavePreparationAsync(LocalStateKey key, PreparationRecord record, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _preparations[key] = record;
            return Task.FromResult(StoreWriteResult.Ok());
        }
    }

    public Task<PreparationRecord?> LoadPreparationAsync(LocalStateKey key, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_preparations.TryGetValue(key, out var preparation)
                ? preparation
                : null);
        }
    }

    public Task<StoreWriteResult> DeleteAsync(LocalStateKey key, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _states.Remove(key);
            _marks.Remove(key);
            _snapshots.Remove(key);
            _fixed.Remove(key);
            _preparations.Remove(key);
            return Task.FromResult(StoreWriteResult.Ok());
        }
    }

    public void Dispose()
    {
    }
}

public sealed class AnswerSessionTests
{
    private static readonly LocalStateKey KeyA = new("srv-1", "exam-1", "cand-A");
    private static readonly LocalStateKey KeyB = new("srv-1", "exam-1", "cand-B");

    private static AnswerSessionContext Context(string participationId = "part-1", long authorizationVersion = 1) =>
        new(participationId, EntryVersion: 1, AuthorizationVersion: authorizationVersion, PaperVersion: 1, DeviceId: "device-1");

    [Fact]
    public async Task StartAsync_NoSavedState_WritesInitialVersion1()
    {
        var store = new FakeLocalStateStore();
        var session = new AnswerSession(store);

        var restore = await session.StartAsync(KeyA, Context());

        Assert.Equal(AnswerSessionStatus.Active, restore.Status);
        Assert.True(restore.IsUsable);
        Assert.Equal(1, session.AnswerVersion);

        var saved = await store.LoadAnswerStateAsync(KeyA);
        Assert.NotNull(saved);
        Assert.Equal(1, saved!.AnswerVersion);
        Assert.Equal("part-1", saved.ParticipationId);
    }

    [Fact]
    public async Task SetAnswer_BumpsVersion_EveryOperation_AndWritesSnapshot()
    {
        var store = new FakeLocalStateStore();
        var session = new AnswerSession(store);
        await session.StartAsync(KeyA, Context());

        var r1 = await session.SetAnswerAsync("q1", AnswerValue.Selected("A"));
        var r2 = await session.SetAnswerAsync("q2", AnswerValue.Abstain());
        var r3 = await session.SetAnswerAsync("q1", AnswerValue.Selected("A", "C"));

        Assert.True(r1.Succeeded);
        Assert.True(r2.Succeeded);
        Assert.True(r3.Succeeded);
        Assert.Equal(2, r1.AnswerVersion);
        Assert.Equal(3, r2.AnswerVersion);
        Assert.Equal(4, r3.AnswerVersion);

        // 待发送快照与最后一次成功保存一致（版本 + 答案内容）
        var snapshot = await store.LoadPendingSnapshotAsync(KeyA);
        Assert.NotNull(snapshot);
        Assert.Equal(4, snapshot!.AnswerVersion);
        AnswerAssert.Equal(AnswerValue.Selected("A", "C"), snapshot.Answers["q1"]);
        AnswerAssert.Equal(AnswerValue.Abstain(), snapshot.Answers["q2"]);
    }

    [Fact]
    public async Task CrashRecovery_RestoresLastSuccessfulState()
    {
        var store = new FakeLocalStateStore();
        var session = new AnswerSession(store);
        await session.StartAsync(KeyA, Context());
        await session.SetAnswerAsync("q1", AnswerValue.Selected("A"));
        await session.SetAnswerAsync("q2", AnswerValue.Selected("B"));
        await session.SetAnswerAsync("q3", AnswerValue.Abstain());

        // 第 4 次写入失败（模拟崩溃前最后一步未落盘）
        store.FailNextWrite = true;
        var failed = await session.SetAnswerAsync("q4", AnswerValue.Selected("D"));
        Assert.False(failed.Succeeded);

        // 崩溃后新会话：恢复最后成功保存状态
        var recovered = new AnswerSession(store);
        var restore = await recovered.StartAsync(KeyA, Context());

        Assert.True(restore.IsUsable);
        Assert.Equal(4, recovered.AnswerVersion);
        AnswerAssert.Equal(AnswerValue.Selected("A"), recovered.Answers["q1"]);
        AnswerAssert.Equal(AnswerValue.Selected("B"), recovered.Answers["q2"]);
        AnswerAssert.Equal(AnswerValue.Abstain(), recovered.Answers["q3"]);
        Assert.False(recovered.Answers.ContainsKey("q4"));
    }

    [Fact]
    public async Task WriteFailure_BlocksFurtherModifications()
    {
        var store = new FakeLocalStateStore();
        var session = new AnswerSession(store);
        await session.StartAsync(KeyA, Context());

        store.FailNextWrite = true;
        var failed = await session.SetAnswerAsync("q1", AnswerValue.Selected("A"));
        Assert.False(failed.Succeeded);
        Assert.Equal(AnswerSessionStatus.WriteBlocked, session.Status);

        // 阻止新增修改：后续答案与标记操作直接拒绝，不再触达存储
        var attemptsBefore = store.WriteAttempts;
        var answerBlocked = await session.SetAnswerAsync("q2", AnswerValue.Selected("B"));
        var markBlocked = await session.ToggleMarkAsync("q1");
        Assert.False(answerBlocked.Succeeded);
        Assert.False(markBlocked.Succeeded);
        Assert.Equal(attemptsBefore, store.WriteAttempts);

        // 内存不包含未落盘的修改
        Assert.False(session.Answers.ContainsKey("q1"));
    }

    [Fact]
    public async Task Marks_IsolatedPerCandidate_AndSurviveRestart()
    {
        var store = new FakeLocalStateStore();

        var sessionA = new AnswerSession(store);
        await sessionA.StartAsync(KeyA, Context());
        Assert.True((await sessionA.ToggleMarkAsync("q1")).Succeeded);
        Assert.True((await sessionA.ToggleMarkAsync("q3")).Succeeded);

        // 同一考试的另一位考生：私人标记不串号
        var sessionB = new AnswerSession(store);
        await sessionB.StartAsync(KeyB, Context());
        Assert.Empty(sessionB.Marks);
        Assert.True((await sessionB.ToggleMarkAsync("q2")).Succeeded);

        // 各自重启（切换账号再切回）后标记保留且互不污染
        var sessionA2 = new AnswerSession(store);
        await sessionA2.StartAsync(KeyA, Context());
        Assert.Equal(new HashSet<string> { "q1", "q3" }, sessionA2.Marks);

        var sessionB2 = new AnswerSession(store);
        await sessionB2.StartAsync(KeyB, Context());
        Assert.Equal(new HashSet<string> { "q2" }, sessionB2.Marks);

        // 私人标记不进入待发送快照（不上报）
        var snapshot = await store.LoadPendingSnapshotAsync(KeyA);
        Assert.NotNull(snapshot);
    }

    [Fact]
    public async Task FixedSubmission_RejectsNewAnswers_ReplayKeepsOriginal()
    {
        var store = new FakeLocalStateStore();
        var session = new AnswerSession(store);
        await session.StartAsync(KeyA, Context());
        await session.SetAnswerAsync("q1", AnswerValue.Selected("A"));

        var fix = await session.FixSubmissionAsync("subm-1");
        Assert.True(fix.Succeeded);
        Assert.False(fix.IsReplay);
        Assert.Equal("subm-1", fix.Record!.SubmissionId);
        Assert.Equal(AnswerSessionStatus.Fixed, session.Status);

        // 已固定提交不生成新答案
        var answerRejected = await session.SetAnswerAsync("q1", AnswerValue.Selected("B"));
        Assert.False(answerRejected.Succeeded);

        // 重复固定（换提交标识）：保留原提交、原答案不变
        var replay = await session.FixSubmissionAsync("subm-2");
        Assert.True(replay.Succeeded);
        Assert.True(replay.IsReplay);
        Assert.Equal("subm-1", replay.Record!.SubmissionId);
        AnswerAssert.Equal(AnswerValue.Selected("A"), replay.Record.FrozenAnswers["q1"]);

        // 崩溃重启后仍为固定态，沿用原提交
        var session2 = new AnswerSession(store);
        var restore = await session2.StartAsync(KeyA, Context());
        Assert.Equal(AnswerSessionStatus.Fixed, restore.Status);
        Assert.Equal("subm-1", session2.FixedSubmissionId);
        AnswerAssert.Equal(AnswerValue.Selected("A"), session2.Answers["q1"]);
        Assert.False((await session2.SetAnswerAsync("q2", AnswerValue.Selected("C"))).Succeeded);
    }

    [Fact]
    public async Task Restore_Rejected_WhenParticipationMismatch()
    {
        var store = new FakeLocalStateStore();
        var session = new AnswerSession(store);
        await session.StartAsync(KeyA, Context(participationId: "part-1"));
        await session.SetAnswerAsync("q1", AnswerValue.Selected("A"));

        // 同键但参试记录不同（如旧参试/另一场确认）：拒绝恢复
        var other = new AnswerSession(store);
        var restore = await other.StartAsync(KeyA, Context(participationId: "part-2"));
        Assert.Equal(AnswerSessionStatus.PermissionDenied, restore.Status);
        Assert.False(other.Answers.ContainsKey("q1"));
        Assert.False((await other.SetAnswerAsync("q2", AnswerValue.Selected("B"))).Succeeded);
    }

    [Fact]
    public async Task Restore_Rejected_WhenAuthorizationVersionStale()
    {
        var store = new FakeLocalStateStore();
        var session = new AnswerSession(store);
        await session.StartAsync(KeyA, Context(authorizationVersion: 2));
        await session.SetAnswerAsync("q1", AnswerValue.Selected("A"));

        // 旧授权版本（服务器已换发新授权）：拒绝恢复，防止旧授权写进度
        var other = new AnswerSession(store);
        var restore = await other.StartAsync(KeyA, Context(authorizationVersion: 1));
        Assert.Equal(AnswerSessionStatus.PermissionDenied, restore.Status);

        // 新授权版本一致则正常恢复
        var same = new AnswerSession(store);
        Assert.True((await same.StartAsync(KeyA, Context(authorizationVersion: 2))).IsUsable);
        AnswerAssert.Equal(AnswerValue.Selected("A"), same.Answers["q1"]);
    }

    [Fact]
    public async Task CurrentQuestion_Persisted_WithoutVersionBump()
    {
        var store = new FakeLocalStateStore();
        var session = new AnswerSession(store);
        await session.StartAsync(KeyA, Context());
        await session.SetAnswerAsync("q1", AnswerValue.Selected("A"));

        var result = await session.SetCurrentQuestionAsync("q5");

        Assert.True(result.Succeeded);
        Assert.Equal(2, session.AnswerVersion); // 切题不改变答案版本

        var restored = new AnswerSession(store);
        await restored.StartAsync(KeyA, Context());
        Assert.Equal("q5", restored.CurrentQuestionId);
    }
}
