using System.IO;
using System.Net.Http;
using System.Text.Json;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Student.Core;
using Microsoft.Data.Sqlite;

namespace Exam.StudentUnitTests;

public sealed partial class PaperUpdateTests
{
    [Fact]
    public void Review_FocusLossCountsTheStillUnfocusedInterval()
    {
        var tracker = new FocusTracker();
        Assert.True(tracker.OnFocusLost());
        Assert.Equal(1, tracker.FocusLostCount);
        Assert.Null(tracker.LastFocusRegainedAt);
        Assert.False(tracker.OnFocusLost());
        tracker.OnFocusRegained();
        Assert.Equal(1, tracker.FocusLostCount);
    }

    [Fact]
    public void Review_LowerIntegrityInjectionFlagIsNotControlKeyState()
    {
        Assert.Equal(KioskKeyDecision.Allow, KioskGuard.Decide(0x1b, 0x02, false));
    }

    [Theory]
    [InlineData("number")]
    [InlineData("type")]
    [InlineData("options")]
    [InlineData("group")]
    [InlineData("abstain")]
    public async Task Review_AnswerSessionRejectsStructuralChangesEvenWhenChoicesRemainValid(string change)
    {
        using var store = new FakeLocalStateStore();
        var paper = TestPaper();
        var session = new AnswerSession(store, paper: paper);
        Assert.True((await session.StartAsync(Key, Ctx(1))).IsUsable);
        var first = paper.Questions[0];
        first = change switch
        {
            "number" => first with { QuestionNo = 3 },
            "type" => first with { QuestionType = "multiple" },
            "options" => first with { OptionIds = ["A", "B", "C"] },
            "group" => first with { GroupId = "other" },
            _ => first with { AllowAbstain = true }
        };
        var changed = new PaperStructure([first, paper.Questions[1]]);
        Assert.False((await session.SwitchPaperVersionAsync(2, changed)).Succeeded);
        Assert.Equal(1, (await store.LoadAnswerStateAsync(Key))!.PaperVersion);
    }

    [Fact]
    public async Task Review_ControlWriteFailureRetainsTheOldUsablePlaintext()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        using var manager = fixture.Manager();
        await StartReviewManagerAsync(manager, fixture);
        fixture.Api.CurrentPaperVersion = 2;
        await manager.HandleCommandAsync(fixture.Api.Delivery(CommandEventType.PaperUpdated, "write-fault", 2, 2));
        var original = manager.UnlockedPayload!;
        var copy = original.ToArray();
        using (new FileStream(fixture.ControlPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Record.ExceptionAsync(async () =>
            {
                var result = await manager.ApplyPaperUpdateAsync();
                Assert.False(result.Succeeded);
            });
            Assert.Null(error);
            Assert.Same(original, manager.UnlockedPayload);
            Assert.Equal(copy, original);
            Assert.Equal(1, manager.Descriptor!.PaperVersion);
            Assert.Equal(ExamPackageManager.Phase.Unlocked, manager.CurrentPhase);
        }
    }

    [Fact]
    public async Task Review_UpdateUnlockRevocationStopsOldPaperAccess()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        using var manager = fixture.Manager();
        await StartReviewManagerAsync(manager, fixture);
        fixture.Api.CurrentPaperVersion = 2;
        fixture.Api.DenyUnlock = true;
        await manager.HandleCommandAsync(fixture.Api.Delivery(CommandEventType.PaperUpdated, "revoke", 2, 2));
        Assert.False((await manager.ApplyPaperUpdateAsync()).Succeeded);
        Assert.Equal(ExamPackageManager.Phase.Revoked, manager.CurrentPhase);
        Assert.Null(manager.UnlockedPayload);
    }

    [Fact]
    public async Task Review_UpdateOutcomeRetryKeepsTheOriginalRequestId()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        using var manager = fixture.Manager();
        await StartReviewManagerAsync(manager, fixture);
        fixture.Api.CurrentPaperVersion = 2;
        await manager.HandleCommandAsync(fixture.Api.Delivery(CommandEventType.PaperUpdated, "ack-retry", 2, 2));
        Assert.True((await manager.ApplyPaperUpdateAsync()).Succeeded);
        fixture.Api.AckHandler = _ => throw new HttpRequestException("synthetic lost reply");
        await Record.ExceptionAsync(() => manager.ReportUpdateOutcomeAsync("ack-retry", true, 2, null));
        fixture.Api.AckHandler = null;
        Assert.True(await manager.ReportUpdateOutcomeAsync("ack-retry", true, 2, null));
        var attempts = fixture.Api.Acks.Where(x => x.EventId == "ack-retry" && x.ProcessingResult == CommandProcessingResult.Applied).ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal(attempts[0].RequestId, attempts[1].RequestId);
        // Replayed delivery must replay Applied, not downgrade to Received.
        await manager.HandleCommandAsync(fixture.Api.Delivery(CommandEventType.PaperUpdated, "ack-retry", 2, 2));
        Assert.Equal(CommandProcessingResult.Applied, fixture.Api.Acks.Last().ProcessingResult);
    }

    [Fact]
    public async Task Review_UpdateOutcomeRejectsMismatchingActualVersion()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        using var manager = fixture.Manager();
        await StartReviewManagerAsync(manager, fixture);
        fixture.Api.CurrentPaperVersion = 2;
        await manager.HandleCommandAsync(fixture.Api.Delivery(CommandEventType.PaperUpdated, "wrong-ack", 2, 2));
        Assert.True((await manager.ApplyPaperUpdateAsync()).Succeeded);
        fixture.Api.AckHandler = request => new("server", "node", 1, "correlation",
            new(request.EventId, request.RequestId, request.ProcessingResult, 99, request.FailureReason, DateTimeOffset.UtcNow), null);
        Assert.False(await manager.ReportUpdateOutcomeAsync("wrong-ack", true, 2, null));
    }

    [Fact]
    public async Task Review_UnknownEventCannotBeReportedAsApplied()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        using var manager = fixture.Manager();
        await StartReviewManagerAsync(manager, fixture);
        Assert.False(await manager.ReportUpdateOutcomeAsync("unknown-event", true, 1, null));
        Assert.DoesNotContain(fixture.Api.Acks, x => x.EventId == "unknown-event");
    }

    [Fact]
    public async Task Review_CoordinatorDoesNotAcceptAnUnvalidatedForeignEvent()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        await using var runtime = fixture.Runtime();
        await StartReviewManagerAsync(runtime.Manager, fixture);
        await runtime.SynchronizeLocalAnswersAsync();
        runtime.PaperUpdate.Notify(fixture.Api.Delivery(CommandEventType.PaperUpdated, "foreign") with { ExamId = "other-exam" });
        await Task.Delay(250);
        Assert.DoesNotContain(fixture.Api.Acks, x => x.EventId == "foreign");
        Assert.Equal(1, runtime.Manager.Descriptor!.PaperVersion);
    }

    [Fact]
    public async Task Review_AnswerWriteFailureCannotProduceAppliedOrReplaceOldPackage()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        await using var runtime = fixture.Runtime();
        await StartReviewManagerAsync(runtime.Manager, fixture);
        await runtime.SynchronizeLocalAnswersAsync();
        var original = runtime.Manager.UnlockedPayload!;
        ExecuteReviewSql(fixture.DatabasePath, "CREATE TRIGGER block_update BEFORE UPDATE ON answer_state BEGIN SELECT RAISE(ABORT,'synthetic write failure'); END;");
        fixture.Api.CurrentPaperVersion = 2;
        var update = fixture.Api.Delivery(CommandEventType.PaperUpdated, "answer-write-fault", 2, 2);
        await runtime.Manager.HandleCommandAsync(update);
        runtime.PaperUpdate.Notify(update);
        await WaitUntilAsync(() => runtime.PaperUpdate.Phase is PaperUpdatePhase.Applied or PaperUpdatePhase.Failed,
            TimeSpan.FromSeconds(15));
        Assert.Equal(PaperUpdatePhase.Failed, runtime.PaperUpdate.Phase);
        Assert.DoesNotContain(fixture.Api.Acks, x => x.EventId == update.EventId && x.ProcessingResult == CommandProcessingResult.Applied);
        Assert.Same(original, runtime.Manager.UnlockedPayload);
        Assert.Equal(1, runtime.Manager.Descriptor!.PaperVersion);
        Assert.Equal(1, (await fixture.Store.LoadAnswerStateAsync(fixture.Key))!.PaperVersion);
    }

    [Fact]
    public async Task Review_UpgradeRejectsWrongIdentityWithoutMutatingTheDatabase()
    {
        var root = Path.Combine(Path.GetTempPath(), "exam-review-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(root, "state.db");
            CreateLegacyV1Database(database, Guid.NewGuid().ToString("N"));
            var original = JsonSerializer.Serialize(new { SchemaVersion = 1, DatabaseId = Guid.NewGuid().ToString("N") }, JsonDefaults.Create());
            await File.WriteAllTextAsync(database + ".identity", original);
            using (var store = new SqliteLocalStateStore(database))
                await Assert.ThrowsAnyAsync<Exception>(() => store.LoadAnswerStateAsync(Key));
            Assert.Equal(1L, ReadReviewScalar(database, "PRAGMA user_version;"));
            Assert.Equal(0L, ReadReviewScalar(database, "SELECT count(*) FROM sqlite_master WHERE name='paper_update_review';"));
            Assert.Equal(original, await File.ReadAllTextAsync(database + ".identity"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Review_InterruptedUpgradeRepairsValidatedIdentitySidecar()
    {
        var root = Path.Combine(Path.GetTempPath(), "exam-review-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(root, "state.db");
            CreateLegacyV1Database(database, Guid.NewGuid().ToString("N"));
            ExecuteReviewSql(database, "CREATE TABLE paper_update_review(server_key TEXT NOT NULL, exam_id TEXT NOT NULL, candidate_id TEXT NOT NULL, paper_version INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(server_key,exam_id,candidate_id,paper_version)); PRAGMA user_version=2;");
            using (var store = new SqliteLocalStateStore(database))
                Assert.Empty(await store.LoadPaperUpdateReviewsAsync(Key));
            using var identity = JsonDocument.Parse(await File.ReadAllTextAsync(database + ".identity"));
            // v2 结构的旧库打开时连升到当前 v3（含 B06 发件箱表），侧文件修复为当前版本。
            Assert.Equal(3, identity.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(3L, ReadReviewScalar(database, "PRAGMA user_version;"));
            Assert.Equal(1L, ReadReviewScalar(database, "SELECT count(*) FROM sqlite_master WHERE name='upload_outbox';"));
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task StartReviewManagerAsync(ExamPackageManager manager, UpdateFixture fixture)
    {
        Assert.True(await manager.PrepareAndDownloadAsync());
        Assert.True((await manager.ConfirmParticipationAsync())!.IsSuccess);
        Assert.True(await manager.HandleCommandAsync(fixture.Api.Start()));
    }

    private static void ExecuteReviewSql(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long ReadReviewScalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
