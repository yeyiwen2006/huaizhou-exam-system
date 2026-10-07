using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Resources;
using Exam.Contracts.Student;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Security;
using Exam.Student.Core;
using Microsoft.Data.Sqlite;

namespace Exam.StudentUnitTests;

/// <summary>
/// 失焦记录 现场异常与在线更新验收判据：
/// 1) 新题包原子切换——下载/确认/解锁失败时旧版明文与状态保持可用，成功后一次性切换并结转答案标记；
/// 2) 更新结果按 Applied（实际版本）/ResourceUpdateFailed（原因）补报；
/// 3) 被撤销设备不能写有效答案（DeviceDecided → 会话禁写）；
/// 4) 修题提示与复核记录——同一版本首次确认后保持原记录；
/// 5) 失焦检测补报与霸屏按键判定。
/// </summary>
public sealed partial class PaperUpdateTests
{
    // ---------- 失焦记录-1 霸屏按键判定（纯函数） ----------

    [Fact]
    public void KioskBlocksExitShortcutsButAllowsNormalKeys()
    {
        const int alt = 0x20;
        const int vkTab = 0x09, vkEsc = 0x1B, vkF4 = 0x73, vkLwin = 0x5B, vkRwin = 0x5C, vkA = 0x41;

        Assert.Equal(KioskKeyDecision.SuppressAndRefocus, KioskGuard.Decide(vkLwin, 0, isSystemKey: false));
        Assert.Equal(KioskKeyDecision.SuppressAndRefocus, KioskGuard.Decide(vkRwin, 0, isSystemKey: false));
        Assert.Equal(KioskKeyDecision.SuppressAndRefocus, KioskGuard.Decide(vkTab, alt, isSystemKey: true));
        Assert.Equal(KioskKeyDecision.Suppress, KioskGuard.Decide(vkEsc, alt, isSystemKey: true));
        Assert.Equal(KioskKeyDecision.Suppress, KioskGuard.Decide(vkEsc, 0, isSystemKey: false, controlDown: true));
        Assert.Equal(KioskKeyDecision.Suppress, KioskGuard.Decide(vkF4, alt, isSystemKey: true));
        // 普通按键与不带 Alt 的 F4（应用内自定义场景）放行。
        Assert.Equal(KioskKeyDecision.Allow, KioskGuard.Decide(vkA, 0, isSystemKey: false));
        Assert.Equal(KioskKeyDecision.Allow, KioskGuard.Decide(vkF4, 0, isSystemKey: false));
        Assert.Equal(KioskKeyDecision.Allow, KioskGuard.Decide(vkTab, 0, isSystemKey: false));
    }

    // ---------- 失焦记录-2 失焦检测 ----------

    [Fact]
    public async Task FocusTrackerCountsLossesAndDurations()
    {
        var time = new FakeTimeProvider();
        var tracker = new FocusTracker(time);

        Assert.False(tracker.OnFocusRegained()); // 未失焦时复焦无效
        Assert.Equal(0, tracker.FocusLostCount);

        Assert.True(tracker.OnFocusLost());
        Assert.False(tracker.OnFocusLost()); // 重复失焦不重复计数
        Assert.True(tracker.IsUnfocused);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.True(tracker.OnFocusRegained());

        time.Advance(TimeSpan.FromSeconds(3));
        Assert.True(tracker.OnFocusLost());
        time.Advance(TimeSpan.FromSeconds(7));
        tracker.Close(); // 页面卸载时收尾进行中的失焦

        Assert.Equal(2, tracker.FocusLostCount);
        Assert.False(tracker.IsUnfocused);
        Assert.Equal(TimeSpan.FromSeconds(12), tracker.TotalUnfocusedDuration);
        var events = tracker.Snapshot();
        Assert.Equal(TimeSpan.FromSeconds(5), events[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(7), events[1].Duration);
        Assert.Equal(events[1].LostAt + events[1].Duration, tracker.LastFocusRegainedAt);
        await Task.CompletedTask;
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 28, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan span) { _now += span; _timestamp += span.Ticks; }
    }

    // ---------- 失焦记录-3 答案会话版本切换 ----------

    [Fact]
    public async Task SwitchPaperVersionCarriesAnswersMarksAndCurrentQuestion()
    {
        var store = new FakeLocalStateStore();
        var paper = TestPaper();
        var session = new AnswerSession(store, paper: paper);
        Assert.True((await session.StartAsync(Key, Ctx(paperVersion: 1))).IsUsable);
        Assert.True((await session.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
        Assert.True((await session.ToggleMarkAsync("q2")).Succeeded);
        Assert.True((await session.SetCurrentQuestionAsync("q2")).Succeeded);
        var versionBefore = session.AnswerVersion;

        var result = await session.SwitchPaperVersionAsync(2, TestPaper());

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(AnswerSessionStatus.Active, session.Status);
        Assert.Equal(versionBefore + 1, session.AnswerVersion);
        AnswerAssert.Equal(AnswerValue.Selected("A"), session.Answers["q1"]);
        Assert.Contains("q2", session.Marks);
        Assert.Equal("q2", session.CurrentQuestionId);

        var saved = await store.LoadAnswerStateAsync(Key);
        Assert.NotNull(saved);
        Assert.Equal(2, saved!.PaperVersion);
        Assert.Equal(versionBefore + 1, saved.AnswerVersion);
        var snapshot = await store.LoadPendingSnapshotAsync(Key);
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.PaperVersion);
    }

    [Fact]
    public async Task SwitchPaperVersionRejectsRegressionAndInvalidStructureAndNonActiveSessions()
    {
        var store = new FakeLocalStateStore();
        var session = new AnswerSession(store, paper: TestPaper());
        Assert.True((await session.StartAsync(Key, Ctx(paperVersion: 2))).IsUsable);
        Assert.True((await session.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);

        // 版本不前进：拒绝。
        Assert.False((await session.SwitchPaperVersionAsync(2, TestPaper())).Succeeded);
        Assert.False((await session.SwitchPaperVersionAsync(1, TestPaper())).Succeeded);

        // 新结构缺少已作答题目：拒绝且原状态不变。
        var missingQuestion = new PaperStructure([TestPaper().Questions[0]]);
        Assert.False((await session.SwitchPaperVersionAsync(3, missingQuestion)).Succeeded);
        Assert.Equal(AnswerSessionStatus.Active, session.Status);
        Assert.Equal(2, session.AnswerVersion);
        AnswerAssert.Equal(AnswerValue.Selected("A"), (await store.LoadAnswerStateAsync(Key))!.Answers["q1"]);

        // 固定后：拒绝。
        Assert.True((await session.FixSubmissionAsync("submission-1")).Succeeded);
        Assert.False((await session.SwitchPaperVersionAsync(3, TestPaper())).Succeeded);
        Assert.Equal(AnswerSessionStatus.Fixed, session.Status);
    }

    private static readonly LocalStateKey Key = new("srv-1", "exam-1", "cand-1");
    private static AnswerSessionContext Ctx(long paperVersion) =>
        new("part-1", EntryVersion: 1, AuthorizationVersion: 1, PaperVersion: paperVersion,
            DeviceId: "device-1", IsServerAuthorized: true);
    private static PaperStructure TestPaper() => new(
    [
        new PaperQuestionStructure("q1", 1, "s1", "g1", "single", ["A", "B"]),
        new PaperQuestionStructure("q2", 2, "s1", "g1", "single", ["A", "B"]),
    ]);

    // ---------- 失焦记录-4 管理器原子切换与失败回退 ----------

    [Fact]
    public async Task ManagerUpdateFailureKeepsOldPackageAndSucceedsAfterRetry()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        using var manager = fixture.Manager();
        await manager.PrepareAndDownloadAsync();
        await manager.ConfirmParticipationAsync();
        Assert.True(await manager.HandleCommandAsync(fixture.Api.Start()));
        var oldPayload = manager.UnlockedPayload;
        Assert.NotNull(oldPayload);
        Assert.Equal(1, manager.Descriptor!.PaperVersion);

        // 服务器发布 v2；事件先按 Received 确认。
        fixture.Api.CurrentPaperVersion = 2;
        var update = fixture.Api.Delivery(CommandEventType.PaperUpdated, eventId: "update-1", resourceVersion: 2, stateVersion: 2);
        Assert.True(await manager.HandleCommandAsync(update));
        Assert.Equal(2, manager.NewestPaperVersion);

        // 下载失败：旧版明文与描述符原样保留，补报 ResourceUpdateFailed。
        fixture.Api.TruncateDownload = true;
        var failed = await manager.ApplyPaperUpdateAsync();
        Assert.False(failed.Succeeded);
        Assert.Same(oldPayload, manager.UnlockedPayload);
        Assert.Equal(1, manager.Descriptor!.PaperVersion);
        Assert.True(await manager.ReportUpdateOutcomeAsync("update-1", applied: false, 0, failed.Error));
        var failedAck = fixture.Api.Acks.Single(ack => ack.EventId == "update-1" && ack.ProcessingResult == CommandProcessingResult.ResourceUpdateFailed);
        Assert.Equal(failed.Error, failedAck.FailureReason);

        // 恢复网络：完整下载并切换成功，旧明文被替换；从新包读出的题目 PDF 带 v2 标记。
        fixture.Api.TruncateDownload = false;
        var succeeded = await manager.ApplyPaperUpdateAsync();
        Assert.True(succeeded.Succeeded, succeeded.Error);
        Assert.Equal(2, succeeded.InstalledPaperVersion);
        Assert.Equal(2, manager.Descriptor!.PaperVersion);
        Assert.NotSame(oldPayload, manager.UnlockedPayload);
        var manifestV2 = StudentPaperPackage.ReadManifest(manager.UnlockedPayload!, "exam", 2);
        Assert.Contains("synthetic-v2-q1", Encoding.UTF8.GetString(
            StudentPaperPackage.ReadQuestionPdf(manager.UnlockedPayload!, manifestV2, "q1")));
        Assert.Equal(ExamPackageManager.Phase.Unlocked, manager.CurrentPhase);

        // 成功后补报 Applied + 实际版本。
        Assert.True(await manager.ReportUpdateOutcomeAsync("update-1", applied: true, 2, null));
        var appliedAck = fixture.Api.Acks.Single(ack => ack.EventId == "update-1" && ack.ProcessingResult == CommandProcessingResult.Applied);
        Assert.Equal(2, appliedAck.ActualPaperVersion);
    }

    [Fact]
    public async Task ManagerUpdateUnlockFailureKeepsOldPackage()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        using var manager = fixture.Manager();
        await manager.PrepareAndDownloadAsync();
        await manager.ConfirmParticipationAsync();
        Assert.True(await manager.HandleCommandAsync(fixture.Api.Start()));
        var oldPayload = manager.UnlockedPayload;

        fixture.Api.CurrentPaperVersion = 2;
        fixture.Api.DenyUnlock = true;
        fixture.Api.UnlockErrorCode = ErrorCodes.ServiceRecovering; // 只有临时失败保留作答；撤权另有拒绝用例。
        var update = fixture.Api.Delivery(CommandEventType.PaperUpdated, eventId: "update-2", resourceVersion: 2, stateVersion: 2);
        await manager.HandleCommandAsync(update);

        var failed = await manager.ApplyPaperUpdateAsync();
        Assert.False(failed.Succeeded);
        Assert.Same(oldPayload, manager.UnlockedPayload);
        Assert.Equal(1, manager.Descriptor!.PaperVersion);
        Assert.Equal(ExamPackageManager.Phase.Unlocked, manager.CurrentPhase);
    }

    // ---------- 失焦记录-5 协调器端到端：切换结转答案 + 复核记录 ----------

    [Fact]
    public async Task CoordinatorAppliesUpdate_PreservesAnswersAndRecordsReview()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        await using var runtime = fixture.Runtime();
        await runtime.InitializeLocalRecoveryAsync();
        await runtime.Manager.PrepareAndDownloadAsync();
        await runtime.Manager.ConfirmParticipationAsync();
        Assert.True(await runtime.Manager.HandleCommandAsync(fixture.Api.Start()));
        await runtime.SynchronizeLocalAnswersAsync();
        var answers = Assert.IsType<AnswerSession>(runtime.Answers);
        Assert.Equal(AnswerSessionStatus.Active, answers.Status);
        Assert.True((await answers.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
        Assert.True((await answers.ToggleMarkAsync("q1")).Succeeded);
        var versionBefore = answers.AnswerVersion;

        fixture.Api.CurrentPaperVersion = 2;
        var update = fixture.Api.Delivery(CommandEventType.PaperUpdated, eventId: "coord-update", resourceVersion: 2, stateVersion: 2);
        Assert.True(await runtime.Manager.HandleCommandAsync(update));
        runtime.PaperUpdate.Notify(update);

        await WaitUntilAsync(() => runtime.PaperUpdate.Phase == PaperUpdatePhase.Applied, TimeSpan.FromSeconds(15));

        // 答案、标记与当前题全部结转，会话保持可作答；纸面为 v2。
        Assert.Equal(AnswerSessionStatus.Active, answers.Status);
        AnswerAssert.Equal(AnswerValue.Selected("A"), answers.Answers["q1"]);
        Assert.Contains("q1", answers.Marks);
        Assert.Equal(versionBefore + 1, answers.AnswerVersion);
        Assert.Equal(2, runtime.Paper!.PaperVersion);
        Assert.Equal(2, runtime.Manager.Descriptor!.PaperVersion);
        Assert.Contains("synthetic-v2-q1", Encoding.UTF8.GetString(runtime.ReadQuestionPdf("q1")));

        // 成功结果补报 Applied + 实际版本（含事件确认）。
        await WaitUntilAsync(() => fixture.Api.Acks.Any(ack =>
            ack.EventId == "coord-update" && ack.ProcessingResult == CommandProcessingResult.Applied), TimeSpan.FromSeconds(15));

        // 修题提示与复核记录：确认一次落库，重复确认不覆盖原确认时间。
        var confirmed = await runtime.ConfirmPaperUpdateReviewAsync();
        Assert.NotNull(confirmed);
        Assert.True(confirmed!.Succeeded, confirmed.Error);
        var first = (await fixture.Store.LoadPaperUpdateReviewsAsync(fixture.Key)).Single();
        Assert.Equal(2, first.PaperVersion);

        await Task.Delay(20);
        var again = await runtime.ConfirmPaperUpdateReviewAsync();
        Assert.True(again!.Succeeded);
        var records = await fixture.Store.LoadPaperUpdateReviewsAsync(fixture.Key);
        var retained = Assert.Single(records);
        Assert.Equal(first.ConfirmedAt, retained.ConfirmedAt);
    }

    // ---------- 失焦记录-6 撤销设备不能写有效答案（裁决路径） ----------

    [Fact]
    public async Task DeviceDecisionRevocationStopsAnsweringAndKeepsLocalRecord()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        await using var runtime = fixture.Runtime();
        await runtime.InitializeLocalRecoveryAsync();
        await runtime.Manager.PrepareAndDownloadAsync();
        await runtime.Manager.ConfirmParticipationAsync();
        Assert.True(await runtime.Manager.HandleCommandAsync(fixture.Api.Start()));
        await runtime.SynchronizeLocalAnswersAsync();
        var answers = Assert.IsType<AnswerSession>(runtime.Answers);
        Assert.True((await answers.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);

        var decision = fixture.Api.Delivery(CommandEventType.DeviceDecided, eventId: "decision",
            authorizationVersion: 2);
        await runtime.Manager.HandleCommandAsync(decision);
        await runtime.SynchronizeLocalAnswersAsync();

        Assert.Equal(ExamPackageManager.Phase.Revoked, runtime.Manager.CurrentPhase);
        Assert.Null(runtime.Manager.UnlockedPayload);
        Assert.Null(runtime.Paper);
        Assert.Equal(AnswerSessionStatus.PermissionDenied, answers.Status);
        // 被撤销设备不能写有效答案。
        Assert.False((await answers.SetAnswerAsync("q2", AnswerValue.Selected("B"))).Succeeded);
        // 原答案与标记保留在本地。
        var saved = await fixture.Store.LoadAnswerStateAsync(fixture.Key);
        AnswerAssert.Equal(AnswerValue.Selected("A"), saved!.Answers["q1"]);
    }

    // ---------- 失焦记录-7 设备裁决等待状态 ----------

    [Fact]
    public async Task DeviceDecisionPendingIsVisibleUntilResolved()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        using var manager = fixture.Manager();
        fixture.Api.PreparationStatus = EntryPreparationStatus.DeviceDecisionPending;
        Assert.False(await manager.PrepareAndDownloadAsync());
        Assert.True(manager.DeviceDecisionPending);

        // 裁决通过后（准备状态恢复 Ready）自动重试即可继续。
        fixture.Api.PreparationStatus = EntryPreparationStatus.Ready;
        Assert.True(await manager.PrepareAndDownloadAsync());
        Assert.False(manager.DeviceDecisionPending);
    }

    // ---------- 失焦记录-8 本地库 v1→v2 迁移与复核记录持久化 ----------

    [Fact]
    public async Task LegacyV1DatabaseMigratesToV2AndKeepsReviewRecords()
    {
        var root = Path.Combine(Path.GetTempPath(), $"exam-store-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var databasePath = Path.Combine(root, "state.db");
            var databaseId = Guid.NewGuid().ToString("N");
            CreateLegacyV1Database(databasePath, databaseId);

            using (var store = new SqliteLocalStateStore(databasePath))
            {
                // 迁移后复核记录可用；保存两个版本的确认。
                Assert.True((await store.SavePaperUpdateReviewAsync(Key,
                    new PaperUpdateReviewRecord(2, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-4), DateTimeOffset.UtcNow))).Succeeded);
                Assert.True((await store.SavePaperUpdateReviewAsync(Key,
                    new PaperUpdateReviewRecord(3, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow))).Succeeded);
                // 同一版本重复确认不覆盖。
                Assert.True((await store.SavePaperUpdateReviewAsync(Key,
                    new PaperUpdateReviewRecord(2, DateTimeOffset.MinValue, DateTimeOffset.MinValue, DateTimeOffset.MaxValue))).Succeeded);
                var reviews = await store.LoadPaperUpdateReviewsAsync(Key);
                Assert.Equal(2, reviews.Count);
                Assert.Equal(2, reviews[0].PaperVersion);
                Assert.Equal(3, reviews[1].PaperVersion);
                Assert.NotEqual(DateTimeOffset.MaxValue, reviews[0].ConfirmedAt);
            }

            // 再次打开（已是 v2）：幂等，记录仍在。
            using (var reopened = new SqliteLocalStateStore(databasePath))
            {
                var reviews = await reopened.LoadPaperUpdateReviewsAsync(Key);
                Assert.Equal(2, reviews.Count);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateLegacyV1Database(string databasePath, string databaseId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE store_identity(
                singleton INTEGER PRIMARY KEY CHECK(singleton=1), database_id TEXT NOT NULL);
            CREATE TABLE answer_state(
                server_key TEXT NOT NULL, exam_id TEXT NOT NULL, candidate_id TEXT NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY(server_key,exam_id,candidate_id));
            CREATE TABLE pending_snapshot(
                server_key TEXT NOT NULL, exam_id TEXT NOT NULL, candidate_id TEXT NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY(server_key,exam_id,candidate_id),
                FOREIGN KEY(server_key,exam_id,candidate_id) REFERENCES answer_state(server_key,exam_id,candidate_id));
            CREATE TABLE fixed_submission(
                server_key TEXT NOT NULL, exam_id TEXT NOT NULL, candidate_id TEXT NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY(server_key,exam_id,candidate_id),
                FOREIGN KEY(server_key,exam_id,candidate_id) REFERENCES answer_state(server_key,exam_id,candidate_id));
            CREATE TABLE preparation(
                server_key TEXT NOT NULL, exam_id TEXT NOT NULL, candidate_id TEXT NOT NULL, payload TEXT NOT NULL,
                PRIMARY KEY(server_key,exam_id,candidate_id));
            INSERT INTO store_identity(singleton,database_id) VALUES(1,$database_id);
            PRAGMA user_version=1;
            """;
        command.Parameters.AddWithValue("$database_id", databaseId);
        command.ExecuteNonQuery();
        File.WriteAllText(databasePath + ".identity",
            JsonSerializer.Serialize(new { SchemaVersion = 1, DatabaseId = databaseId }, JsonDefaults.Create()));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition(), "等待条件超时。");
    }

    /// <summary>解密后的题包是 ZIP 载荷，用编译进包内的 PDF 版本标记区分 v1/v2。</summary>
    private static bool ContainsMarker(byte[] payload, string marker)
    {
        var needle = Encoding.UTF8.GetBytes(marker);
        for (var i = 0; i <= payload.Length - needle.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (payload[i + j] != needle[j]) { matched = false; break; }
            }
            if (matched) return true;
        }
        return false;
    }

    // ---------- 双版本合成包夹具 ----------

    private sealed class UpdateFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"exam-b06-tests-{Guid.NewGuid():N}");
        public string DatabasePath => Path.Combine(_root, "state.db");
        public string ControlPath => Directory.GetFiles(_root, "control.protected", SearchOption.AllDirectories).Single();
        public LocalStateKey Key { get; } = new("server", "exam", "candidate");
        public SqliteLocalStateStore Store { get; }
        public VersionedPackageApi Api { get; }
        private readonly DeviceRecord _device = new("device", "test-credential", "20260922001", "127.0.0.1", 5443,
            DateTimeOffset.UtcNow, "server", ActivationStatus.Active, CredentialVersion: 1, ServerEpoch: 1);
        private readonly StudentSessionResult _session = new("session", "device",
            new("candidate", "合成考生", "01", "001"), ["exam"], DateTimeOffset.UtcNow,
            new Dictionary<string, string> { ["exam"] = "candidate" });

        private UpdateFixture(VersionedPackageApi api)
        {
            Api = api;
            Store = new SqliteLocalStateStore(Path.Combine(_root, "state.db"));
        }

        public static async Task<UpdateFixture> CreateAsync() => new(await VersionedPackageApi.CreateAsync());

        public ExamPackageManager Manager() => new(Api, Path.Combine(_root, "packages"), _device, _session, "exam");
        public StudentExamRuntime Runtime(ILocalStateStore? store = null) => new(Manager(), _device, store ?? Store, Key.CandidateId);

        public void Dispose()
        {
            Store.Dispose();
            Api.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>同一题目结构、两个版本的合成加密包（v1/v2 正文不同），支持下载截断与解锁拒绝注入。</summary>
    private sealed class VersionedPackageApi : IExamPackageApi, IDisposable
    {
        private const string ManifestJson = """
            {"schemaVersion":1,"examId":"exam","examName":"B06合成考试",
             "subjects":[{"subjectId":"s1","name":"合成科目","maxScore":"2"}],
             "groups":[{"groupId":"g1","subjectId":"s1","name":"单选","type":"single","order":1,
               "questionCount":2,"maxScore":"2","scoring":{"oneCorrect":{"correct":"1","blank":"0","wrong":"0"}}}],
             "questions":[
               {"questionId":"q1","questionNo":1,"subjectId":"s1","groupId":"g1","source":"questions/B06合成考试_1.tex","options":["A","B"]},
               {"questionId":"q2","questionNo":2,"subjectId":"s1","groupId":"g1","source":"questions/B06合成考试_2.tex","options":["A","B"]}],
             "resourceRoots":[]}
            """;

        private readonly X509Certificate2 _node;
        private readonly Dictionary<long, EncryptedPackageEnvelope> _envelopes = new();
        private readonly Dictionary<long, byte[]> _ciphertexts = new();
        public long CurrentPaperVersion { get; set; } = 1;
        public bool TruncateDownload { get; set; }
        public bool DenyUnlock { get; set; }
        public string UnlockErrorCode { get; set; } = ErrorCodes.AuthorizationRevoked;
        public Func<AcknowledgeCommandRequest, ApiEnvelope<CommandAcknowledgement>>? AckHandler { get; set; }
        public EntryPreparationStatus PreparationStatus { get; set; } = EntryPreparationStatus.Ready;
        public System.Collections.Concurrent.ConcurrentQueue<AcknowledgeCommandRequest> Acks { get; } = new();
        public Func<long, CancellationToken, Task>? BeforeDownload { get; set; }
        public Action<PackageUnlockRequest>? BeforeUnlock { get; set; }
        public Func<ConfirmParticipationRequest, ApiEnvelope<ParticipationResult>>? ConfirmHandler { get; set; }
        public List<PrepareExamEntryRequest> PrepareRequests { get; } = [];
        public List<ConfirmParticipationRequest> ConfirmRequests { get; } = [];

        private VersionedPackageApi(X509Certificate2 node) => _node = node;

        public static async Task<VersionedPackageApi> CreateAsync()
        {
            using var rsa = RSA.Create(2048);
            var certificate = new CertificateRequest("CN=B06-test-node", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            var api = new VersionedPackageApi(certificate);
            var root = Path.Combine(Path.GetTempPath(), "b06-package-" + Guid.NewGuid().ToString("N"));
            try
            {
                var source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
                var compiled = Directory.CreateDirectory(Path.Combine(root, "compiled")).FullName;
                await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), ManifestJson, Encoding.UTF8);
                for (var version = 1; version <= 3; version++)
                {
                    for (var number = 1; number <= 2; number++)
                    {
                        await File.WriteAllTextAsync(Path.Combine(compiled, $"B06合成考试_{number}.pdf"),
                            $"%PDF-1.4\n%synthetic-v{version}-q{number}\n%%EOF", new UTF8Encoding(false));
                    }
                    var package = await new StudentPackageBuilder(new PackageEncryptionService(4096)).BuildAsync(
                        new(source, compiled, Path.Combine(root, "preview"), "package", version,
                            Path.Combine(root, "cipher", $"package-v{version}.pkg"), [new("node", certificate)]));
                    Assert.True(package.Succeeded, string.Join(";", package.Issues.Select(issue => issue.Message)));
                    api._envelopes[version] = package.Envelope!;
                    api._ciphertexts[version] = await File.ReadAllBytesAsync(package.EncryptedPackagePath!);
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            return api;
        }

        public Task<ApiEnvelope<EntryPreparationResult>> PrepareEntryAsync(PrepareExamEntryRequest request, CancellationToken ct = default)
        {
            PrepareRequests.Add(request);
            var ciphertext = _ciphertexts[CurrentPaperVersion];
            return Task.FromResult(Ok(new EntryPreparationResult(request.RequestId, "exam", PreparationStatus,
                new ExamResourceDescriptor("/api/v1/resources/package", ciphertext.Length, CurrentPaperVersion, 1),
                null, null, null, null, null, DateTimeOffset.UtcNow)));
        }

        public Task<ApiEnvelope<InstructionsDocument>> GetInstructionsAsync(string preparationId, string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Ok(new InstructionsDocument(1, ["B06 合成考试说明。"])));

        public async IAsyncEnumerable<byte[]> DownloadPackageChunksAsync(ExamResourceDescriptor descriptor, string preparationId,
            string sessionId, [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            if (BeforeDownload is not null) await BeforeDownload(descriptor.PaperVersion, ct);
            var bytes = _ciphertexts[descriptor.PaperVersion];
            if (TruncateDownload)
            {
                yield return bytes[..^1];
                yield break;
            }
            yield return bytes;
        }

        public Task<ApiEnvelope<ParticipationResult>> ConfirmParticipationAsync(ConfirmParticipationRequest request, CancellationToken ct = default)
        {
            ConfirmRequests.Add(request);
            return Task.FromResult(ConfirmHandler?.Invoke(request) ?? Ok(new ParticipationResult("participation", "exam", "candidate",
                ParticipationBusinessState.Answering, "device", 1, 1, 7, "resource-envelope:package",
                Lifecycle: ExamLifecycle.Running)));
        }

        public Task<ApiEnvelope<ParticipationResult>> WithdrawParticipationAsync(WithdrawParticipationRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("B06 测试不执行退出。");

        public Task<ApiEnvelope<PackageUnlockMaterial>> ResolveUnlockAsync(PackageUnlockRequest request, CancellationToken ct = default)
        {
            BeforeUnlock?.Invoke(request);
            if (DenyUnlock && request.PaperVersion > 1)
            {
                return Task.FromResult(new ApiEnvelope<PackageUnlockMaterial>("server", "node", 1, "correlation",
                    null, new ApiError(UnlockErrorCode, "测试：新版解锁被拒绝。")));
            }
            var envelope = _envelopes[request.PaperVersion];
            return Task.FromResult(Ok(new PackageUnlockMaterial(envelope.PackageId, envelope.PaperVersion,
                envelope.ChunkSize, envelope.NoncePrefix,
                PackageEncryptionService.UnwrapDataKey(envelope, "node", _node))));
        }

        public Task<ApiEnvelope<CommandAcknowledgement>> AcknowledgeCommandAsync(AcknowledgeCommandRequest request, CancellationToken ct = default)
        {
            Acks.Enqueue(request);
            if (AckHandler is not null) return Task.FromResult(AckHandler(request));
            return Task.FromResult(Ok(new CommandAcknowledgement(request.EventId, request.RequestId,
                request.ProcessingResult, request.ActualPaperVersion, request.FailureReason, DateTimeOffset.UtcNow)));
        }

        public CommandDelivery Start() => Delivery(CommandEventType.Start, "start", resourceVersion: 1, stateVersion: 1,
            unlockReference: "resource-envelope:package", authorizationVersion: 1);

        public CommandDelivery Delivery(CommandEventType eventType, string eventId, long resourceVersion = 1,
            long stateVersion = 1, string? unlockReference = null, long authorizationVersion = 1) =>
            new(new ServerNodeEnvelope("server", "node", 1), eventId, "exam", "participation", 1, eventType,
                stateVersion, "device", authorizationVersion, resourceVersion, unlockReference,
                new Dictionary<string, string>(), DateTimeOffset.UtcNow, 1);

        private static ApiEnvelope<T> Ok<T>(T value) => new("server", "node", 1, "correlation", value, null);

        public void Dispose() => _node.Dispose();
    }
}
