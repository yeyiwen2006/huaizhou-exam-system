using System.IO;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Student.Core;
using Microsoft.Data.Sqlite;

namespace Exam.StudentUnitTests;

/// <summary>真实 SQLite 文件测试；重开与事务故障回滚不冒充进程断电测试。</summary>
public sealed class SqliteLocalStateStoreTests : IDisposable
{
    private static readonly LocalStateKey KeyA = new("srv-1", "exam-1", "cand-A");
    private static readonly LocalStateKey KeyB = new("srv-1", "exam-1", "cand-B");
    private static readonly LocalStateKey KeyOtherExam = new("srv-1", "exam-2", "cand-A");
    private static readonly LocalStateKey KeyOtherServer = new("srv-2", "exam-1", "cand-A");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hz-localstate-tests", Guid.NewGuid().ToString("N"));
    private string DatabasePath => Path.Combine(_directory, "state.db");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static AnswerStateRecord State(
        long version = 1,
        IReadOnlyDictionary<string, AnswerValue>? answers = null,
        string? current = "q1",
        IReadOnlySet<string>? marks = null) =>
        new("part-1", 1, 1, 2, version, current, DateTimeOffset.UtcNow,
            answers ?? new Dictionary<string, AnswerValue> { ["q1"] = AnswerValue.Selected("A") },
            marks ?? new HashSet<string> { "q1" }, "device-1");

    private static PendingSnapshot Snapshot(AnswerStateRecord state, LocalStateKey? key = null, string? requestId = null) =>
        new((key ?? KeyA).ExamId, (key ?? KeyA).CandidateId, state.ParticipationId, state.EntryVersion, state.DeviceId,
            state.AuthorizationVersion, state.AnswerVersion, state.PaperVersion, state.Answers, state.CurrentQuestionId,
            state.UpdatedAt, requestId ?? Guid.NewGuid().ToString("N"));

    private static FixedSubmissionRecord Fixed(AnswerStateRecord state, string id = "submission-1") =>
        new(id, state.PaperVersion, state.AnswerVersion, state.Answers, state.UpdatedAt,
            "request-" + id, state.ParticipationId, state.EntryVersion, state.DeviceId, state.AuthorizationVersion);

    private SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private void ExecuteRaw(string sql)
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task SaveAsync(SqliteLocalStateStore store, AnswerStateRecord state, LocalStateKey? key = null)
    {
        var actualKey = key ?? KeyA;
        var result = await store.SaveAnswerStateAsync(actualKey, state, Snapshot(state, actualKey));
        Assert.True(result.Succeeded, result.Error);
    }

    [Fact]
    public async Task Reopen_RestoresCompleteStateAndStableSnapshotIdentity()
    {
        var answers = new Dictionary<string, AnswerValue>
        {
            ["q1"] = AnswerValue.Selected("A", "C"),
            ["q2"] = AnswerValue.Abstain(),
            ["q3"] = AnswerValue.Empty(),
        };
        var latest = State(2, answers, "q3", new HashSet<string> { "q2", "q3" });
        var snapshot = Snapshot(latest);
        using (var store = new SqliteLocalStateStore(DatabasePath))
        {
            await SaveAsync(store, State());
            Assert.True((await store.SaveAnswerStateAsync(KeyA, latest, snapshot)).Succeeded);
        }

        using var recovered = new SqliteLocalStateStore(DatabasePath);
        var state = Assert.IsType<AnswerStateRecord>(await recovered.LoadAnswerStateAsync(KeyA));
        Assert.Equal(2, state.AnswerVersion);
        Assert.Equal("q3", state.CurrentQuestionId);
        Assert.True(state.Marks.SetEquals(new[] { "q2", "q3" }));
        Assert.Equal("part-1", state.ParticipationId);
        Assert.Equal("device-1", state.DeviceId);
        AnswerAssert.Equal(AnswerValue.Selected("A", "C"), state.Answers["q1"]);
        AnswerAssert.Equal(AnswerValue.Abstain(), state.Answers["q2"]);
        AnswerAssert.Equal(AnswerValue.Empty(), state.Answers["q3"]);

        var pending = Assert.IsType<PendingSnapshot>(await recovered.LoadPendingSnapshotAsync(KeyA));
        Assert.Equal(snapshot.RequestId, pending.RequestId);
        Assert.Equal(state.AnswerVersion, pending.AnswerVersion);
        Assert.Equal("q3", pending.CurrentQuestionId);
        Assert.Equal("device-1", pending.DeviceId);
        // 私人标记只在 answer_state 载荷，不进入可发送载荷。
        using var raw = OpenRaw();
        using var command = raw.CreateCommand();
        command.CommandText = "SELECT payload FROM pending_snapshot;";
        Assert.DoesNotContain("\"marks\"", (string)command.ExecuteScalar()!, StringComparison.OrdinalIgnoreCase);
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string)command.ExecuteScalar()!);
    }

    [Fact]
    public async Task Isolation_ByServerExamAndCandidate_IncludesPrivateMarks()
    {
        using var store = new SqliteLocalStateStore(DatabasePath);
        await SaveAsync(store, State(marks: new HashSet<string> { "private-A" }));
        Assert.Null(await store.LoadAnswerStateAsync(KeyB));
        Assert.Null(await store.LoadAnswerStateAsync(KeyOtherExam));
        Assert.Null(await store.LoadAnswerStateAsync(KeyOtherServer));
        await SaveAsync(store, State(marks: new HashSet<string> { "private-B" }), KeyB);
        Assert.Equal(new[] { "private-A" }, (await store.LoadAnswerStateAsync(KeyA))!.Marks);
        Assert.Equal(new[] { "private-B" }, (await store.LoadAnswerStateAsync(KeyB))!.Marks);
    }

    [Fact]
    public async Task FailedSnapshotWrite_RollsBackAnswersMarksCurrentQuestionAndVersion_ThenRetrySucceeds()
    {
        using var store = new SqliteLocalStateStore(DatabasePath);
        var first = State();
        await SaveAsync(store, first);
        var firstSnapshot = (await store.LoadPendingSnapshotAsync(KeyA))!;
        // SQLite 在写入快照时拒绝事务，模拟中途存储操作失败，不声称模拟断电。
        ExecuteRaw("""
            CREATE TRIGGER reject_pending BEFORE UPDATE ON pending_snapshot
            BEGIN SELECT RAISE(ABORT,'simulated pending write failure'); END;
            """);
        var next = State(2, new Dictionary<string, AnswerValue> { ["q2"] = AnswerValue.Selected("B") },
            "q2", new HashSet<string> { "q2" });
        var pending = Snapshot(next);
        var result = await store.SaveAnswerStateAsync(KeyA, next, pending);
        Assert.False(result.Succeeded);
        var reliable = (await store.LoadAnswerStateAsync(KeyA))!;
        Assert.Equal(first.AnswerVersion, reliable.AnswerVersion);
        Assert.Equal("q1", reliable.CurrentQuestionId);
        Assert.Equal(new[] { "q1" }, reliable.Marks);
        AnswerAssert.Equal(AnswerValue.Selected("A"), reliable.Answers["q1"]);
        Assert.Equal(firstSnapshot.RequestId, (await store.LoadPendingSnapshotAsync(KeyA))!.RequestId);
        ExecuteRaw("DROP TRIGGER reject_pending;");
        Assert.True((await store.SaveAnswerStateAsync(KeyA, next, pending)).Succeeded);
        Assert.Equal(pending.RequestId, (await store.LoadPendingSnapshotAsync(KeyA))!.RequestId);
    }

    [Theory]
    [InlineData("answer-version")]
    [InlineData("paper-version")]
    [InlineData("participation")]
    [InlineData("entry")]
    [InlineData("device")]
    [InlineData("authorization")]
    [InlineData("same-version-content")]
    [InlineData("same-version-request")]
    [InlineData("snapshot-device")]
    [InlineData("reused-request")]
    [InlineData("missing-snapshot")]
    public async Task InvalidVersionAuthorizationOrSnapshot_CannotOverwriteReliableState(string failure)
    {
        using var store = new SqliteLocalStateStore(DatabasePath);
        var first = State(2);
        var original = Snapshot(first);
        Assert.True((await store.SaveAnswerStateAsync(KeyA, first, original)).Succeeded);
        var next = first with { AnswerVersion = 3, UpdatedAt = first.UpdatedAt.AddSeconds(1) };
        next = failure switch
        {
            "answer-version" => next with { AnswerVersion = 1 },
            "paper-version" => next with { PaperVersion = 1 },
            "participation" => next with { ParticipationId = "part-2" },
            "entry" => next with { EntryVersion = 2 },
            "device" => next with { DeviceId = "device-2" },
            "authorization" => next with { AuthorizationVersion = 2 },
            "same-version-content" => first with { Marks = new HashSet<string> { "q2" } },
            "same-version-request" => first,
            _ => next,
        };
        PendingSnapshot? snapshot = Snapshot(next);
        if (failure == "snapshot-device") snapshot = snapshot with { DeviceId = "other" };
        if (failure == "reused-request") snapshot = snapshot with { RequestId = original.RequestId };
        if (failure == "missing-snapshot") snapshot = null;
        Assert.False((await store.SaveAnswerStateAsync(KeyA, next, snapshot)).Succeeded);
        Assert.Equal(2, (await store.LoadAnswerStateAsync(KeyA))!.AnswerVersion);
        Assert.Equal(original.RequestId, (await store.LoadPendingSnapshotAsync(KeyA))!.RequestId);
    }

    [Fact]
    public async Task ExactReplay_PreservesRequestAndNoVersionlessMarksCanBypassTransaction()
    {
        using var store = new SqliteLocalStateStore(DatabasePath);
        var state = State();
        var snapshot = Snapshot(state);
        Assert.True((await store.SaveAnswerStateAsync(KeyA, state, snapshot)).Succeeded);
        Assert.True((await store.SaveAnswerStateAsync(KeyA, state, snapshot)).Succeeded);
        Assert.True((await store.SaveAnswerStateAsync(KeyA, state, null)).Succeeded);
        Assert.False((await store.SaveMarksAsync(KeyA, new[] { "unversioned" })).Succeeded);
        Assert.False((await store.SaveMarksAsync(KeyB, new[] { "orphan" })).Succeeded);
        Assert.Equal(new[] { "q1" }, (await store.LoadAnswerStateAsync(KeyA))!.Marks);
        Assert.Null(await store.LoadAnswerStateAsync(KeyB));
    }

    [Fact]
    public async Task FixedSubmission_PersistsOriginalTupleAndIds_RejectsUnsavedOrLaterEdits()
    {
        var state = State(2);
        using (var store = new SqliteLocalStateStore(DatabasePath))
        {
            Assert.False((await store.TryFixSubmissionAsync(KeyA, Fixed(state))).Result.Succeeded);
            await SaveAsync(store, state);
            Assert.False((await store.TryFixSubmissionAsync(KeyA, Fixed(state with { AnswerVersion = 3 }))).Result.Succeeded);
            Assert.False((await store.TryFixSubmissionAsync(KeyA, Fixed(state) with { DeviceId = "other" })).Result.Succeeded);
            Assert.False((await store.TryFixSubmissionAsync(KeyA, Fixed(state) with { RequestId = null })).Result.Succeeded);
            var fixedResult = await store.TryFixSubmissionAsync(KeyA, Fixed(state));
            Assert.True(fixedResult.Result.Succeeded, fixedResult.Result.Error);
            var second = await store.TryFixSubmissionAsync(KeyA, Fixed(State(9), "different-submission"));
            Assert.True(second.Result.Succeeded);
            Assert.Equal("submission-1", second.Record!.SubmissionId);
            Assert.Equal("request-submission-1", second.Record.RequestId);
            var next = state with { AnswerVersion = 3, Marks = new HashSet<string>() };
            Assert.False((await store.SaveAnswerStateAsync(KeyA, next, Snapshot(next))).Succeeded);
            Assert.False((await store.SaveMarksAsync(KeyA, Array.Empty<string>())).Succeeded);
            Assert.False((await store.DeleteAsync(KeyA)).Succeeded);
        }

        using var recovered = new SqliteLocalStateStore(DatabasePath);
        var record = (await recovered.LoadFixedSubmissionAsync(KeyA))!;
        Assert.Equal("submission-1", record.SubmissionId);
        Assert.Equal("request-submission-1", record.RequestId);
        Assert.Equal(state.ParticipationId, record.ParticipationId);
        Assert.Equal(state.EntryVersion, record.EntryVersion);
        Assert.Equal(state.DeviceId, record.DeviceId);
        Assert.Equal(state.AuthorizationVersion, record.AuthorizationVersion);
        Assert.Equal(state.AnswerVersion, record.AnswerVersion);
        AnswerAssert.Equal(state.Answers["q1"], record.FrozenAnswers["q1"]);
    }

    [Fact]
    public async Task FixPendingState_IsOneTransaction_AndFailedFixedInsertCannotAdvanceReliableState()
    {
        var first = State();
        var latest = State(2, new Dictionary<string, AnswerValue> { ["q2"] = AnswerValue.Selected("B") },
            "q2", new HashSet<string> { "q2" });
        var pending = Snapshot(latest);
        var fixedRecord = Fixed(latest);
        using (var store = new SqliteLocalStateStore(DatabasePath))
        {
            await SaveAsync(store, first);
            var reliableSnapshot = (await store.LoadPendingSnapshotAsync(KeyA))!;
            ExecuteRaw("""
                CREATE TRIGGER reject_fixed BEFORE INSERT ON fixed_submission
                BEGIN SELECT RAISE(ABORT,'simulated fixed write failure'); END;
                """);
            var failed = await store.TryFixSubmissionAsync(KeyA, fixedRecord, latest, pending);
            Assert.False(failed.Result.Succeeded);
            Assert.Null(failed.Record);
            Assert.Null(await store.LoadFixedSubmissionAsync(KeyA));
            var reliable = (await store.LoadAnswerStateAsync(KeyA))!;
            Assert.Equal(1, reliable.AnswerVersion);
            Assert.Equal("q1", reliable.CurrentQuestionId);
            Assert.Equal(new[] { "q1" }, reliable.Marks);
            AnswerAssert.Equal(AnswerValue.Selected("A"), reliable.Answers["q1"]);
            Assert.Equal(reliableSnapshot.RequestId, (await store.LoadPendingSnapshotAsync(KeyA))!.RequestId);

            ExecuteRaw("DROP TRIGGER reject_fixed;");
            var retried = await store.TryFixSubmissionAsync(KeyA, fixedRecord, latest, pending);
            Assert.True(retried.Result.Succeeded, retried.Result.Error);
            Assert.Equal(fixedRecord.SubmissionId, retried.Record!.SubmissionId);
        }

        using var recovered = new SqliteLocalStateStore(DatabasePath);
        var saved = (await recovered.LoadAnswerStateAsync(KeyA))!;
        var savedFixed = (await recovered.LoadFixedSubmissionAsync(KeyA))!;
        Assert.Equal(latest.AnswerVersion, saved.AnswerVersion);
        Assert.Equal(saved.AnswerVersion, savedFixed.AnswerVersion);
        Assert.Equal("q2", saved.CurrentQuestionId);
        Assert.Equal(new[] { "q2" }, saved.Marks);
        Assert.Equal(pending.RequestId, (await recovered.LoadPendingSnapshotAsync(KeyA))!.RequestId);
        Assert.Equal(fixedRecord.RequestId, savedFixed.RequestId);
        AnswerAssert.Equal(saved.Answers["q2"], savedFixed.FrozenAnswers["q2"]);
    }

    [Fact]
    public async Task QueuedPayload_IsDetachedFromCallerOwnedMutableCollections()
    {
        using var store = new SqliteLocalStateStore(DatabasePath);
        var options = new List<string> { "A" };
        var answers = new Dictionary<string, AnswerValue> { ["q1"] = new(AnswerKind.Selected, options) };
        var marks = new HashSet<string> { "q1" };
        var state = State(answers: answers, marks: marks);
        var saving = store.SaveAnswerStateAsync(KeyA, state, Snapshot(state));
        options[0] = "B";
        answers.Clear();
        marks.Clear();
        Assert.True((await saving).Succeeded);
        var saved = (await store.LoadAnswerStateAsync(KeyA))!;
        AnswerAssert.Equal(AnswerValue.Selected("A"), saved.Answers["q1"]);
        Assert.Equal(new[] { "q1" }, saved.Marks);
    }

    [Fact]
    public async Task TwoConnections_CannotRollBackOrMixVersions()
    {
        using var first = new SqliteLocalStateStore(DatabasePath);
        using var second = new SqliteLocalStateStore(DatabasePath);
        var v2 = State(2, new Dictionary<string, AnswerValue> { ["q1"] = AnswerValue.Selected("V2") });
        var v3 = State(3, new Dictionary<string, AnswerValue> { ["q1"] = AnswerValue.Selected("V3") },
            "q3", new HashSet<string> { "q3" });
        var results = await Task.WhenAll(
            first.SaveAnswerStateAsync(KeyA, v2, Snapshot(v2)),
            second.SaveAnswerStateAsync(KeyA, v3, Snapshot(v3)));
        Assert.True(results[1].Succeeded, results[1].Error);
        var saved = (await first.LoadAnswerStateAsync(KeyA))!;
        Assert.Equal(3, saved.AnswerVersion);
        AnswerAssert.Equal(AnswerValue.Selected("V3"), saved.Answers["q1"]);
        Assert.Equal("q3", saved.CurrentQuestionId);
        Assert.Equal(new[] { "q3" }, saved.Marks);
        Assert.Equal(saved.AnswerVersion, (await second.LoadPendingSnapshotAsync(KeyA))!.AnswerVersion);
    }

    [Fact]
    public async Task Dispose_DrainsAcceptedWrites_AndCancellationBeforeQueueDoesNotWrite()
    {
        using var store = new SqliteLocalStateStore(DatabasePath);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var state = State();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAnswerStateAsync(KeyA, state, Snapshot(state), cancel.Token));
        Assert.Null(await store.LoadAnswerStateAsync(KeyA));
        var tasks = Enumerable.Range(1, 12).Select(version =>
        {
            var next = State(version);
            return store.SaveAnswerStateAsync(KeyA, next, Snapshot(next));
        }).ToArray();
        await Task.Run(store.Dispose).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(await Task.WhenAll(tasks), result => Assert.True(result.Succeeded, result.Error));
        using var recovered = new SqliteLocalStateStore(DatabasePath);
        Assert.Equal(12, (await recovered.LoadAnswerStateAsync(KeyA))!.AnswerVersion);
    }

    [Fact]
    public async Task InitializationFailure_CompletesPromptlyAndPreservesExistingFile()
    {
        Directory.CreateDirectory(_directory);
        var original = new byte[] { 1, 7, 9, 3 };
        File.WriteAllBytes(DatabasePath, original);
        using var store = new SqliteLocalStateStore(DatabasePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAnswerStateAsync(KeyA))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(original, File.ReadAllBytes(DatabasePath));
        Assert.False(File.Exists(DatabasePath + ".identity"));
    }

    [Theory]
    [InlineData("database")]
    [InlineData("identity")]
    public async Task MissingInitializedFile_DoesNotCreateReplacementDatabase(string missing)
    {
        using (var store = new SqliteLocalStateStore(DatabasePath)) await SaveAsync(store, State());
        var target = missing == "database" ? DatabasePath : DatabasePath + ".identity";
        File.Move(target, target + ".preserved");
        using var recovered = new SqliteLocalStateStore(DatabasePath);
        await Assert.ThrowsAsync<InvalidDataException>(() => recovered.LoadAnswerStateAsync(KeyA))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(target));
        Assert.True(File.Exists(target + ".preserved"));
    }

    [Fact]
    public async Task CorruptDatabase_FailsInitializationWithoutOverwritingOriginal()
    {
        using (var store = new SqliteLocalStateStore(DatabasePath)) await SaveAsync(store, State());
        var corrupt = Enumerable.Repeat((byte)0x7f, 4096).ToArray();
        File.WriteAllBytes(DatabasePath, corrupt);
        using var recovered = new SqliteLocalStateStore(DatabasePath);
        await Assert.ThrowsAsync<SqliteException>(() => recovered.LoadAnswerStateAsync(KeyA))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(corrupt, File.ReadAllBytes(DatabasePath));
    }

    [Fact]
    public async Task MissingSnapshot_IsAnError_NotEmptyRecoveredState()
    {
        using var store = new SqliteLocalStateStore(DatabasePath);
        await SaveAsync(store, State());
        ExecuteRaw("DELETE FROM pending_snapshot;");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAnswerStateAsync(KeyA));
        Assert.False((await store.SaveAnswerStateAsync(KeyA, State(2), Snapshot(State(2)))).Succeeded);
        using var raw = OpenRaw();
        using var command = raw.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM answer_state;";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public async Task MalformedSnapshotPayload_IsAnError_NotNullOrEmptyState()
    {
        using var store = new SqliteLocalStateStore(DatabasePath);
        await SaveAsync(store, State());
        ExecuteRaw("UPDATE pending_snapshot SET payload='null';");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadPendingSnapshotAsync(KeyA));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAnswerStateAsync(KeyA));
    }

    [Fact]
    public async Task PreparationRecord_RoundTripAndRejectsVersionRollback()
    {
        using var store = new SqliteLocalStateStore(DatabasePath);
        var record = new PreparationRecord(3, 2, @"C:\data\3.pkg", "part-9", "ConfirmedWaiting", DateTimeOffset.UtcNow);
        Assert.Null(await store.LoadPreparationAsync(KeyA));
        Assert.True((await store.SavePreparationAsync(KeyA, record)).Succeeded);
        Assert.True((await store.SavePreparationAsync(KeyA, record with { Phase = "Unlocked" })).Succeeded);
        Assert.False((await store.SavePreparationAsync(KeyA, record with { PaperVersion = 2 })).Succeeded);
        Assert.False((await store.SavePreparationAsync(KeyA, record with { InstructionsVersion = 1 })).Succeeded);
        var loaded = (await store.LoadPreparationAsync(KeyA))!;
        Assert.Equal(3, loaded.PaperVersion);
        Assert.Equal("Unlocked", loaded.Phase);
        Assert.True((await store.DeleteAsync(KeyA)).Succeeded);
        Assert.Null(await store.LoadPreparationAsync(KeyA));
    }

    [Fact]
    public async Task UploadOutbox_RoundTripKeepsFirstPayloadAndDeletesOnlyConfirmedItems()
    {
        using var store = new SqliteLocalStateStore(DatabasePath);
        Assert.Empty(await store.LoadUploadsAsync(KeyA, 10));
        Assert.True((await store.EnqueueUploadAsync(KeyA,
            new PendingUploadRecord("focus-1", "focus", """{"eventId":"focus-1"}"""))).Succeeded);
        Assert.True((await store.EnqueueUploadAsync(KeyA,
            new PendingUploadRecord("focus-2", "focus", """{"eventId":"focus-2"}"""))).Succeeded);
        // 同一 ItemId 重复入队（崩溃重放）不覆盖首次载荷。
        Assert.True((await store.EnqueueUploadAsync(KeyA,
            new PendingUploadRecord("focus-1", "focus", """{"eventId":"focus-1-replaced"}"""))).Succeeded);

        var uploads = await store.LoadUploadsAsync(KeyA, 10);
        Assert.Equal(2, uploads.Count);
        Assert.Equal("focus-1", uploads[0].ItemId);
        Assert.Equal("""{"eventId":"focus-1"}""", uploads[0].Payload);
        Assert.Equal("focus", uploads[0].Kind);

        Assert.True((await store.DeleteUploadAsync(KeyA, "focus-1")).Succeeded);
        Assert.Single(await store.LoadUploadsAsync(KeyA, 10));
        // 不存在的标识不能删除：防止把重复确认当作成功。
        Assert.False((await store.DeleteUploadAsync(KeyA, "focus-1")).Succeeded);
        Assert.False((await store.DeleteUploadAsync(KeyA, " ")).Succeeded);

        // 考生/考试/服务器隔离。
        Assert.Empty(await store.LoadUploadsAsync(KeyB, 10));
        Assert.Empty(await store.LoadUploadsAsync(KeyOtherExam, 10));
        Assert.Empty(await store.LoadUploadsAsync(KeyOtherServer, 10));
    }

    [Fact]
    public async Task UploadOutbox_ReopenKeepsPendingItemsAndUpgradesV2Schema()
    {
        using (var store = new SqliteLocalStateStore(DatabasePath))
        {
            Assert.True((await store.EnqueueUploadAsync(KeyA,
                new PendingUploadRecord("focus-1", "focus", "{}"))).Succeeded);
        }

        // 手工降级成 v2 结构（删除发件箱表并回退版本号），模拟旧版客户端升级路径；
        // 身份侧文件同步回退到 v2，与真实 v2 数据库的组合一致。
        ExecuteRaw("DROP TABLE upload_outbox; PRAGMA user_version=2;");
        var identityPath = DatabasePath + ".identity";
        File.WriteAllText(identityPath,
            File.ReadAllText(identityPath).Replace("\"schemaVersion\":3", "\"schemaVersion\":2"));

        using (var store = new SqliteLocalStateStore(DatabasePath))
        {
            // 打开旧库时自动补齐 v3 结构；历史数据保留。
            Assert.Empty(await store.LoadUploadsAsync(KeyA, 10));
            Assert.True((await store.EnqueueUploadAsync(KeyA,
                new PendingUploadRecord("focus-2", "focus", "{}"))).Succeeded);
        }

        using (var store = new SqliteLocalStateStore(DatabasePath))
        {
            var uploads = await store.LoadUploadsAsync(KeyA, 10);
            var item = Assert.Single(uploads);
            Assert.Equal("focus-2", item.ItemId);
        }
    }

    [Fact]
    public async Task UploadOutbox_SidecarAheadOfDatabaseIsRejected()
    {
        using (var store = new SqliteLocalStateStore(DatabasePath))
        {
            Assert.True((await store.EnqueueUploadAsync(KeyA,
                new PendingUploadRecord("focus-1", "focus", "{}"))).Succeeded);
        }

        // 侧文件 v3 + 数据库 v2 是不可能状态（侧文件只在 DDL 提交后升级），必须拒绝。
        ExecuteRaw("DROP TABLE upload_outbox; PRAGMA user_version=2;");

        var reopened = new SqliteLocalStateStore(DatabasePath);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => reopened.LoadUploadsAsync(KeyA, 10));
        }
        finally
        {
            reopened.Dispose();
        }
    }
}
