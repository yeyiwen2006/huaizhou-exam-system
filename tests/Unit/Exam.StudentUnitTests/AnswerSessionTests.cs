using System.IO;
using Exam.Contracts.Common;
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
    private readonly Dictionary<LocalStateKey, PendingSnapshot> _snapshots = new();
    private readonly Dictionary<LocalStateKey, FixedSubmissionRecord> _fixed = new();
    private readonly Dictionary<LocalStateKey, PreparationRecord> _preparations = new();

    /// <summary>下一次写操作（保存答案/标记/固定）返回失败。</summary>
    public bool FailNextWrite { get; set; }
    public bool ThrowOnLoad { get; set; }
    public Func<Task>? BeforeStateWrite { get; set; }
    public PendingSnapshot? LastAttemptedSnapshot { get; private set; }
    public FixedSubmissionRecord? LastAttemptedFixed { get; private set; }

    /// <summary>写操作尝试次数（含失败；被门闸阻止的调用不计入）。</summary>
    public int WriteAttempts { get; private set; }

    public Task<AnswerStateRecord?> LoadAnswerStateAsync(LocalStateKey key, CancellationToken ct = default)
    {
        if (ThrowOnLoad) throw new IOException("注入的恢复读取故障。");
        lock (_gate)
        {
            _states.TryGetValue(key, out var state);
            return Task.FromResult(state is null ? null : state with
            {
                Answers = PersistedProgress.FreezeAnswers(state.Answers),
                Marks = new HashSet<string>(state.Marks),
            });
        }
    }

    public async Task<StoreWriteResult> SaveAnswerStateAsync(
        LocalStateKey key, AnswerStateRecord state, PendingSnapshot? snapshot, CancellationToken ct = default)
    {
        if (BeforeStateWrite is { } wait) await wait();
        lock (_gate)
        {
            WriteAttempts++;
            LastAttemptedSnapshot = snapshot;
            if (FailNextWrite)
            {
                FailNextWrite = false;
                return StoreWriteResult.Fail("注入的磁盘故障。");
            }

            _states[key] = state;
            if (snapshot is not null)
            {
                _snapshots[key] = snapshot;
            }

            return StoreWriteResult.Ok();
        }
    }

    public Task<StoreWriteResult> SaveMarksAsync(LocalStateKey key, IReadOnlyCollection<string> marks, CancellationToken ct = default)
        => throw new InvalidOperationException("标记必须与完整版本状态和快照在同一事务保存。");

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
            LastAttemptedFixed = record;
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
        if (ThrowOnLoad) throw new IOException("注入的恢复读取故障。");
        lock (_gate)
        {
            return Task.FromResult(_fixed.TryGetValue(key, out var fixedRecord)
                ? fixedRecord
                : null);
        }
    }

    public Task<(StoreWriteResult Result, FixedSubmissionRecord? Record)> TryFixSubmissionAsync(
        LocalStateKey key, FixedSubmissionRecord record, AnswerStateRecord pendingState,
        PendingSnapshot pendingSnapshot, CancellationToken ct = default)
    {
        lock (_gate)
        {
            WriteAttempts++;
            LastAttemptedFixed = record;
            LastAttemptedSnapshot = pendingSnapshot;
            if (FailNextWrite)
            {
                FailNextWrite = false;
                return Task.FromResult((StoreWriteResult.Fail("注入的事务故障。"), (FixedSubmissionRecord?)null));
            }
            if (_fixed.TryGetValue(key, out var existing))
                return Task.FromResult((StoreWriteResult.Ok(), (FixedSubmissionRecord?)existing));
            _states[key] = pendingState;
            _snapshots[key] = pendingSnapshot;
            _fixed[key] = record;
            return Task.FromResult((StoreWriteResult.Ok(), (FixedSubmissionRecord?)record));
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
        new(participationId, EntryVersion: 1, AuthorizationVersion: authorizationVersion, PaperVersion: 1,
            DeviceId: "device-1", IsServerAuthorized: true);

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

        // 可靠状态不冒充落盘成功；未保存编辑另外保留用于重试。
        Assert.False(session.Answers.ContainsKey("q1"));
        AnswerAssert.Equal(AnswerValue.Selected("A"), session.PendingAnswers!["q1"]);
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
    public async Task CurrentQuestion_Persisted_WithVersionBumpAndMatchingSnapshot()
    {
        var store = new FakeLocalStateStore();
        var session = new AnswerSession(store);
        await session.StartAsync(KeyA, Context());
        await session.SetAnswerAsync("q1", AnswerValue.Selected("A"));

        var result = await session.SetCurrentQuestionAsync("q5");

        Assert.True(result.Succeeded);
        Assert.Equal(3, session.AnswerVersion);
        var snapshot = await store.LoadPendingSnapshotAsync(KeyA);
        Assert.Equal(3, snapshot!.AnswerVersion);
        Assert.Equal("q5", snapshot.CurrentQuestionId);

        var restored = new AnswerSession(store);
        await restored.StartAsync(KeyA, Context());
        Assert.Equal("q5", restored.CurrentQuestionId);
    }

    [Fact]
    public async Task FailedWrite_RetainsDeepCopiedEdit_AndRetriesExactSnapshot()
    {
        var store = new FakeLocalStateStore();
        var session = new AnswerSession(store);
        await session.StartAsync(KeyA, Context());
        var options = new[] { "A" };
        store.FailNextWrite = true;

        Assert.False((await session.SetAnswerAsync("q1", new AnswerValue(AnswerKind.Selected, options))).Succeeded);
        var attempted = store.LastAttemptedSnapshot;
        options[0] = "B";

        Assert.Equal(1, session.AnswerVersion);
        Assert.True(session.HasUnsavedChanges);
        Assert.Empty(session.Answers);
        AnswerAssert.Equal(AnswerValue.Selected("A"), session.PendingAnswers!["q1"]);
        Assert.False((await session.SetCurrentQuestionAsync("q2")).Succeeded);

        var retried = await session.RetrySaveAsync();

        Assert.True(retried.Succeeded);
        Assert.Equal(2, retried.AnswerVersion);
        Assert.False(session.HasUnsavedChanges);
        Assert.Same(attempted, store.LastAttemptedSnapshot);
        Assert.False(string.IsNullOrWhiteSpace(attempted!.RequestId));
        AnswerAssert.Equal(AnswerValue.Selected("A"), session.Answers["q1"]);
    }

    [Fact]
    public async Task MarkAndNavigation_AreVersionedTransactions_AndFailedNavigationRetainsBothStates()
    {
        var store = new FakeLocalStateStore();
        var session = new AnswerSession(store);
        await session.StartAsync(KeyA, Context());
        await session.ToggleMarkAsync("q1");
        await session.SetCurrentQuestionAsync("q1");
        store.FailNextWrite = true;

        Assert.False((await session.SetCurrentQuestionAsync("q2")).Succeeded);
        Assert.Equal(3, session.AnswerVersion);
        Assert.Equal("q1", session.CurrentQuestionId);
        Assert.Equal("q2", session.PendingCurrentQuestionId);
        Assert.Contains("q1", session.PendingMarks!);
        Assert.True((await session.RetrySaveAsync()).Succeeded);

        var state = await store.LoadAnswerStateAsync(KeyA);
        var snapshot = await store.LoadPendingSnapshotAsync(KeyA);
        Assert.Equal(4, state!.AnswerVersion);
        Assert.Equal(state.AnswerVersion, snapshot!.AnswerVersion);
        Assert.Equal("q2", snapshot.CurrentQuestionId);
        Assert.Contains("q1", state.Marks);
        Assert.Equal(state.UpdatedAt, snapshot.SavedAt);
    }

    [Fact]
    public async Task ConcurrentEdits_AreSerializedWithoutVersionOrAnswerLoss()
    {
        var store = new FakeLocalStateStore();
        var session = new AnswerSession(store);
        await session.StartAsync(KeyA, Context());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeStateWrite = async () => { entered.TrySetResult(); await release.Task; };

        var first = session.SetAnswerAsync("q1", AnswerValue.Selected("A"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = Enumerable.Range(2, 8).Select(index =>
            session.SetAnswerAsync($"q{index}", AnswerValue.Selected("B"))).ToArray();
        release.SetResult();
        var results = await Task.WhenAll(new[] { first }.Concat(queued));

        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal(Enumerable.Range(2, 9).Select(value => (long)value), results.Select(result => result.AnswerVersion).Order());
        Assert.Equal(10, session.AnswerVersion);
        Assert.Equal(9, session.Answers.Count);
        Assert.Equal(10, store.WriteAttempts);
    }

    [Fact]
    public async Task FreezeFailure_LocksImmediately_AndKeepsFirstIdTupleAndSnapshot()
    {
        var store = new FakeLocalStateStore();
        var session = new AnswerSession(store);
        await session.StartAsync(KeyA, Context());
        await session.SetAnswerAsync("q1", AnswerValue.Selected("A"));
        store.FailNextWrite = true;

        var first = await session.FixSubmissionAsync("submission-first");

        Assert.False(first.Succeeded);
        Assert.Equal(AnswerSessionStatus.FixPending, session.Status);
        Assert.True(session.HasPendingSubmission);
        Assert.Null(session.FixedSubmission);
        Assert.False((await session.SetAnswerAsync("q1", AnswerValue.Selected("B"))).Succeeded);
        var retried = await session.FixSubmissionAsync("submission-must-not-replace");

        Assert.True(retried.Succeeded);
        Assert.True(retried.IsReplay);
        Assert.Equal("submission-first", retried.Record!.SubmissionId);
        Assert.Equal(first.Record!.RequestId, retried.Record.RequestId);
        Assert.Equal(first.Record.CreatedAt, retried.Record.CreatedAt);
        Assert.Equal("part-1", retried.Record.ParticipationId);
        Assert.Equal("device-1", retried.Record.DeviceId);
        Assert.Equal(1, retried.Record.EntryVersion);
        Assert.Equal(1, retried.Record.AuthorizationVersion);
        Assert.False(session.HasPendingSubmission);
        AnswerAssert.Equal(AnswerValue.Selected("A"), retried.Record.FrozenAnswers["q1"]);
    }

    [Fact]
    public async Task FreezeIncludesUnsavedEdit_AndRetryAtomicallyPersistsItWithSameFixedSubmission()
    {
        var store = new FakeLocalStateStore();
        var session = new AnswerSession(store);
        await session.StartAsync(KeyA, Context());
        store.FailNextWrite = true;
        await session.SetAnswerAsync("q1", AnswerValue.Abstain());
        var pending = store.LastAttemptedSnapshot;
        store.FailNextWrite = true;

        var failed = await session.FixSubmissionAsync("submission-first");

        Assert.False(failed.Succeeded);
        Assert.Equal(2, failed.Record!.AnswerVersion);
        AnswerAssert.Equal(AnswerValue.Abstain(), failed.Record.FrozenAnswers["q1"]);
        Assert.NotNull(store.LastAttemptedFixed);
        Assert.Equal(1, session.AnswerVersion);
        Assert.Equal(1, (await store.LoadAnswerStateAsync(KeyA))!.AnswerVersion);
        Assert.Null(await store.LoadFixedSubmissionAsync(KeyA));
        Assert.True(session.HasUnsavedChanges);
        Assert.True((await session.RetrySaveAsync()).Succeeded);
        Assert.Same(pending, store.LastAttemptedSnapshot);
        Assert.Equal(AnswerSessionStatus.Fixed, session.Status);
        Assert.Equal(failed.Record.RequestId, session.FixedSubmission!.RequestId);
        Assert.Equal("submission-first", session.FixedSubmission.SubmissionId);
        AnswerAssert.Equal(AnswerValue.Abstain(), session.FixedSubmission.FrozenAnswers["q1"]);
    }

    [Fact]
    public async Task FreezeEntry_BlocksEditsQueuedBehindAnInFlightSave()
    {
        var store = new FakeLocalStateStore();
        var session = new AnswerSession(store);
        await session.StartAsync(KeyA, Context());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeStateWrite = async () => { entered.TrySetResult(); await release.Task; };
        var inFlight = session.SetAnswerAsync("q1", AnswerValue.Selected("A"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var fix = session.FixSubmissionAsync("submission-first");
        var laterEdit = session.SetAnswerAsync("q2", AnswerValue.Selected("B"));
        Assert.Equal(AnswerSessionStatus.FixPending, session.Status);
        release.SetResult();

        Assert.True((await inFlight).Succeeded);
        Assert.True((await fix).Succeeded);
        Assert.False((await laterEdit).Succeeded);
        Assert.Single(session.FixedSubmission!.FrozenAnswers);
        AnswerAssert.Equal(AnswerValue.Selected("A"), session.FixedSubmission.FrozenAnswers["q1"]);
    }

    [Fact]
    public async Task Suspend_RetainsPendingEditAndCanRetry_WithoutCreatingAFalseSubmission()
    {
        var store = new FakeLocalStateStore();
        var session = new AnswerSession(store);
        await session.StartAsync(KeyA, Context());
        store.FailNextWrite = true;
        await session.SetAnswerAsync("q1", AnswerValue.Selected("A"));

        await session.SuspendAsync("服务器已通知考试结束，保留本地记录等待后续收卷功能。");
        Assert.False((await session.ToggleMarkAsync("q1")).Succeeded);
        Assert.True((await session.RetrySaveAsync()).Succeeded);

        Assert.Equal(AnswerSessionStatus.Suspended, session.Status);
        Assert.Null(session.FixedSubmissionId);
        Assert.Null(session.FixedSubmission);
        Assert.False(session.HasPendingSubmission);
        AnswerAssert.Equal(AnswerValue.Selected("A"), session.Answers["q1"]);
    }

    [Fact]
    public async Task AuthorizationLoss_BlocksQueuedChanges_AndSameStartCannotReauthorize()
    {
        var store = new FakeLocalStateStore();
        var session = new AnswerSession(store);
        await session.StartAsync(KeyA, Context());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeStateWrite = async () => { entered.TrySetResult(); await release.Task; };
        var inFlight = session.SetAnswerAsync("q1", AnswerValue.Selected("A"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var invalidated = session.InvalidateAuthorizationAsync();
        Assert.Equal(AnswerSessionStatus.PermissionDenied, session.Status);
        var rejected = session.SetAnswerAsync("q2", AnswerValue.Selected("B"));
        release.SetResult();
        await inFlight;
        await invalidated;

        Assert.False((await rejected).Succeeded);
        Assert.Equal(AnswerSessionStatus.PermissionDenied, (await session.StartAsync(KeyA, Context())).Status);
        Assert.False((await session.RetrySaveAsync()).Succeeded);
        Assert.False((await session.FixSubmissionAsync("must-not-create")).Succeeded);
        Assert.False(session.Answers.ContainsKey("q2"));
    }

    [Fact]
    public async Task StartRequiresLiveAuthorization_AndCannotReplacePendingStateOrCrossCandidate()
    {
        var store = new FakeLocalStateStore();
        var denied = new AnswerSession(store);
        Assert.Equal(AnswerSessionStatus.PermissionDenied,
            (await denied.StartAsync(KeyA, Context() with { IsServerAuthorized = false })).Status);
        Assert.Equal(0, store.WriteAttempts);

        var session = new AnswerSession(store);
        await session.StartAsync(KeyA, Context());
        store.FailNextWrite = true;
        await session.SetAnswerAsync("q1", AnswerValue.Selected("A"));

        Assert.Equal(AnswerSessionStatus.WriteBlocked, (await session.StartAsync(KeyA, Context())).Status);
        Assert.Equal(AnswerSessionStatus.PermissionDenied, (await session.StartAsync(KeyB, Context())).Status);
        Assert.True(session.HasUnsavedChanges);
        Assert.Null(await store.LoadAnswerStateAsync(KeyB));
        Assert.True((await session.RetrySaveAsync()).Succeeded);
        AnswerAssert.Equal(AnswerValue.Selected("A"), session.Answers["q1"]);
    }

    [Fact]
    public async Task RestoreReadFailure_IsReportedAndNeverReplacedWithBlankState()
    {
        var store = new FakeLocalStateStore { ThrowOnLoad = true };
        var session = new AnswerSession(store);

        var result = await session.StartAsync(KeyA, Context());

        Assert.Equal(AnswerSessionStatus.WriteBlocked, result.Status);
        Assert.False(result.IsUsable);
        Assert.NotNull(session.LastError);
        Assert.False((await session.RetrySaveAsync()).Succeeded);
        Assert.False((await session.FixSubmissionAsync("must-not-create")).Succeeded);
        Assert.Equal(0, store.WriteAttempts);
        Assert.False(session.HasUnsavedChanges);
    }

    [Fact]
    public async Task RestoreRejectsAnotherDevice_EvenWithOtherwiseMatchingAuthorization()
    {
        var store = new FakeLocalStateStore();
        var original = new AnswerSession(store);
        await original.StartAsync(KeyA, Context());
        await original.FixSubmissionAsync("original-fixed");
        var attempts = store.WriteAttempts;

        var anotherDevice = new AnswerSession(store);
        var result = await anotherDevice.StartAsync(KeyA, Context() with { DeviceId = "device-2" });

        Assert.Equal(AnswerSessionStatus.PermissionDenied, result.Status);
        Assert.Null(anotherDevice.FixedSubmission);
        Assert.Equal(attempts, store.WriteAttempts);
    }
}
