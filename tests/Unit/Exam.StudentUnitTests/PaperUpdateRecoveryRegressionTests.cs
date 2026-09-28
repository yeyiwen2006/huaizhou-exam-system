using System.IO;
using System.Net.Http;
using System.Reflection;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

public sealed partial class PaperUpdateTests
{
    [Fact]
    public async Task Review_CommittedAnswerTransactionRecoversPackageAndReviewAfterRestart()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        var runtime = fixture.Runtime();
        try
        {
            await StartReviewRuntimeAsync(runtime, fixture);
            Assert.True((await runtime.Answers!.SetAnswerAsync("q1", AnswerValue.Selected("B"))).Succeeded);
            Assert.True((await runtime.Answers.ToggleMarkAsync("q2")).Succeeded);
            Assert.True((await runtime.Answers.SetCurrentQuestionAsync("q2")).Succeeded);
            await RecordUpdateAsync(runtime, fixture, 2);
            Assert.True((await runtime.ApplyPaperUpdateAsync()).Succeeded);
            // 未发送 Applied：控制文件仍可只有预提交意图，SQLite 已是版本 2。
            var before = (await fixture.Store.LoadPendingSnapshotAsync(fixture.Key))!;
            await runtime.DisposeAsync();
            runtime = fixture.Runtime();
            Assert.True(await runtime.InitializeLocalRecoveryAsync());
            Assert.Equal(2, runtime.Manager.Descriptor!.PaperVersion);
            Assert.Null(runtime.Manager.UnlockedPayload); // 本地意图不构成当前授权。
            Assert.Equal(2, runtime.PendingReviewVersion);
            Assert.True(await runtime.Manager.PrepareAndDownloadAsync());
            Assert.True((await runtime.Manager.ConfirmParticipationAsync())!.IsSuccess);
            await runtime.SynchronizeLocalAnswersAsync();
            Assert.Equal(2, runtime.Paper!.PaperVersion);
            Assert.Equal(before.RequestId, (await fixture.Store.LoadPendingSnapshotAsync(fixture.Key))!.RequestId);
            Assert.Contains("q2", runtime.Answers!.Marks);
            Assert.Equal("q2", runtime.Answers.CurrentQuestionId);
            AnswerAssert.Equal(AnswerValue.Selected("B"), runtime.Answers.Answers["q1"]);
            Assert.True((await runtime.ConfirmPaperUpdateReviewAsync(2))!.Succeeded);
            Assert.Null(runtime.PendingReviewVersion);
            var confirmed = Assert.Single(await fixture.Store.LoadPaperUpdateReviewsAsync(fixture.Key));
            Assert.True((await runtime.ConfirmPaperUpdateReviewAsync(2))!.Succeeded);
            Assert.Equal(confirmed, Assert.Single(await fixture.Store.LoadPaperUpdateReviewsAsync(fixture.Key)));
        }
        finally { await runtime.DisposeAsync(); }
    }

    [Fact]
    public async Task Review_SqlFailureRetainsOldVersionAndRetryCommitsExactlyOnce()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        await using var runtime = fixture.Runtime();
        await StartReviewRuntimeAsync(runtime, fixture);
        var before = runtime.Answers!.AnswerVersion;
        var payload = runtime.Manager.UnlockedPayload!;
        var bytes = payload.ToArray();
        ExecuteReviewSql(fixture.DatabasePath, "CREATE TRIGGER review_fail BEFORE UPDATE ON answer_state BEGIN SELECT RAISE(ABORT,'synthetic'); END;");
        await RecordUpdateAsync(runtime, fixture, 2);
        Assert.False((await runtime.ApplyPaperUpdateAsync()).Succeeded);
        Assert.Equal(1, runtime.Manager.Descriptor!.PaperVersion);
        Assert.Same(payload, runtime.Manager.UnlockedPayload);
        Assert.Equal(bytes, payload);
        Assert.Equal(AnswerSessionStatus.WriteBlocked, runtime.Answers.Status);
        Assert.Equal(1, (await fixture.Store.LoadAnswerStateAsync(fixture.Key))!.PaperVersion);
        ExecuteReviewSql(fixture.DatabasePath, "DROP TRIGGER review_fail;");
        Assert.True((await runtime.ApplyPaperUpdateAsync()).Succeeded);
        Assert.Equal(2, runtime.Manager.Descriptor.PaperVersion);
        Assert.Equal(2, runtime.Answers.PaperVersion);
        Assert.Equal(before + 1, runtime.Answers.AnswerVersion);
        Assert.Equal(AnswerSessionStatus.Active, runtime.Answers.Status);
    }

    [Fact]
    public async Task Review_ControlIntentWriteFailureAfterDecryptionDoesNotAdvanceSqlOrDestroyOldPaper()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        await using var runtime = fixture.Runtime();
        await StartReviewRuntimeAsync(runtime, fixture);
        await RecordUpdateAsync(runtime, fixture, 2);
        var payload = runtime.Manager.UnlockedPayload!;
        var bytes = payload.ToArray();
        FileStream? held = null;
        fixture.Api.BeforeUnlock = request =>
        {
            if (request.PaperVersion == 2)
                held = new FileStream(fixture.ControlPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        };
        try
        {
            Assert.False((await runtime.ApplyPaperUpdateAsync()).Succeeded);
            Assert.NotNull(held); // 注入发生在候选包已准备、确认，且解锁请求已记入日志之后。
            Assert.Same(payload, runtime.Manager.UnlockedPayload);
            Assert.Equal(bytes, payload);
            Assert.Equal(1, (await fixture.Store.LoadAnswerStateAsync(fixture.Key))!.PaperVersion);
            Assert.Equal(AnswerSessionStatus.Active, runtime.Answers!.Status);
        }
        finally { held?.Dispose(); }
    }

    [Theory]
    [InlineData("end")]
    [InlineData("revoked")]
    [InlineData("fixed")]
    public async Task Review_TerminalEventDuringDownloadCannotReactivateOrInstall(string terminal)
    {
        using var fixture = await UpdateFixture.CreateAsync();
        await using var runtime = fixture.Runtime();
        await StartReviewRuntimeAsync(runtime, fixture);
        Assert.True((await runtime.Answers!.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
        await RecordUpdateAsync(runtime, fixture, 2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Api.BeforeDownload = async (version, ct) =>
        {
            if (version > 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
        };
        var update = runtime.ApplyPaperUpdateAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task stopped = Task.CompletedTask;
        try
        {
            if (terminal == "fixed")
                Assert.True((await runtime.Answers.FixSubmissionAsync("terminal-submission")).Succeeded);
            else
            {
                var command = terminal == "end"
                    ? fixture.Api.Delivery(CommandEventType.End, "terminal-end", 1, 4)
                    : fixture.Api.Delivery(CommandEventType.DeviceDecided, "terminal-revoke", authorizationVersion: 2);
                var method = typeof(StudentExamRuntime).GetMethod("ApplyCommandAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                stopped = (Task)method.Invoke(runtime, [command, CancellationToken.None])!;
                Assert.Null(runtime.Manager.UnlockedPayload);
            }
            Assert.NotEqual(AnswerSessionStatus.Active, runtime.Answers.Status);
        }
        finally { release.TrySetResult(); }
        Assert.False((await update.WaitAsync(TimeSpan.FromSeconds(10))).Succeeded);
        await stopped.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, runtime.Manager.Descriptor!.PaperVersion);
        var saved = (await fixture.Store.LoadAnswerStateAsync(fixture.Key))!;
        Assert.Equal(1, saved.PaperVersion);
        AnswerAssert.Equal(AnswerValue.Selected("A"), saved.Answers["q1"]);
        Assert.DoesNotContain(fixture.Api.Acks, item => item.EventId == "recovery-update-2" && item.ProcessingResult == CommandProcessingResult.Applied);
    }

    [Fact]
    public async Task Review_LostConfirmationReplyReusesOriginalPreparationAndConfirmation()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        await using var runtime = fixture.Runtime();
        await StartReviewRuntimeAsync(runtime, fixture);
        await RecordUpdateAsync(runtime, fixture, 2);
        fixture.Api.ConfirmHandler = _ => throw new HttpRequestException("synthetic lost confirm reply");
        Assert.False((await runtime.ApplyPaperUpdateAsync()).Succeeded);
        var first = fixture.Api.ConfirmRequests.Last();
        var prepareCount = fixture.Api.PrepareRequests.Count;
        fixture.Api.ConfirmHandler = null;
        Assert.True((await runtime.ApplyPaperUpdateAsync()).Succeeded);
        Assert.Equal(first, fixture.Api.ConfirmRequests.Last());
        Assert.Equal(prepareCount, fixture.Api.PrepareRequests.Count);
    }

    [Fact]
    public async Task Review_OlderUnacknowledgedAppliedDoesNotBlockNewerPaper()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        await using var runtime = fixture.Runtime();
        await StartReviewRuntimeAsync(runtime, fixture);
        fixture.Api.AckHandler = request => request.EventId == "recovery-update-2" && request.ProcessingResult == CommandProcessingResult.Applied
            ? throw new HttpRequestException("synthetic lost v2 reply") : AckReply(request);
        var v2 = await RecordUpdateAsync(runtime, fixture, 2);
        runtime.PaperUpdate.Notify(v2);
        await WaitUntilAsync(() => runtime.PaperUpdate.Phase == PaperUpdatePhase.Applied && runtime.Answers!.PaperVersion == 2,
            TimeSpan.FromSeconds(15));
        var v3 = await RecordUpdateAsync(runtime, fixture, 3);
        runtime.PaperUpdate.Notify(v3);
        await WaitUntilAsync(() => runtime.Answers!.PaperVersion == 3 && runtime.PaperUpdate.Phase == PaperUpdatePhase.Applied,
            TimeSpan.FromSeconds(15));
        Assert.Equal(3, runtime.Paper!.PaperVersion);
        Assert.Equal(3, runtime.Manager.Descriptor!.PaperVersion);
        Assert.False((await runtime.ConfirmPaperUpdateReviewAsync(2))!.Succeeded);
        Assert.Equal(3, runtime.PendingReviewVersion);
        Assert.True((await runtime.ConfirmPaperUpdateReviewAsync(3))!.Succeeded);
        Assert.Equal(3, Assert.Single(await fixture.Store.LoadPaperUpdateReviewsAsync(fixture.Key)).PaperVersion);
        fixture.Api.AckHandler = null;
        runtime.PaperUpdate.Retry();
        // 真实管理器重放日志中的稳定 v2 请求，而不是用 v3 重新解释原结果。
        await WaitUntilAsync(() => fixture.Api.Acks.Count(item => item.EventId == v2.EventId && item.ProcessingResult == CommandProcessingResult.Applied) >= 3,
            TimeSpan.FromSeconds(15));
        var requests = fixture.Api.Acks.Where(item => item.EventId == v2.EventId && item.ProcessingResult == CommandProcessingResult.Applied).ToArray();
        Assert.Single(requests.Select(item => item.RequestId).Distinct());
        Assert.All(requests, item => Assert.Equal(2, item.ActualPaperVersion));
    }

    [Fact]
    public async Task Review_UnacknowledgedAppliedRequestSurvivesRuntimeRestart()
    {
        using var fixture = await UpdateFixture.CreateAsync();
        var runtime = fixture.Runtime();
        try
        {
            await StartReviewRuntimeAsync(runtime, fixture);
            await RecordUpdateAsync(runtime, fixture, 2);
            Assert.True((await runtime.ApplyPaperUpdateAsync()).Succeeded);
            fixture.Api.AckHandler = _ => throw new HttpRequestException("synthetic lost reply");
            Assert.False(await runtime.Manager.ReportUpdateOutcomeAsync("recovery-update-2", true, 2, null));
            var request = fixture.Api.Acks.Last();
            await runtime.DisposeAsync();
            runtime = fixture.Runtime();
            fixture.Api.AckHandler = null;
            Assert.True(await runtime.InitializeLocalRecoveryAsync());
            Assert.True(await runtime.Manager.PrepareAndDownloadAsync());
            Assert.True((await runtime.Manager.ConfirmParticipationAsync())!.IsSuccess);
            await runtime.SynchronizeLocalAnswersAsync();
            runtime.PaperUpdate.Retry();
            await WaitUntilAsync(() => fixture.Api.Acks.Count(item => item.RequestId == request.RequestId) >= 2,
                TimeSpan.FromSeconds(15));
            Assert.Equal(request, fixture.Api.Acks.Last(item => item.RequestId == request.RequestId));
            Assert.Equal(2, runtime.Answers!.PaperVersion);
            Assert.Equal(2, runtime.PendingReviewVersion);
        }
        finally { await runtime.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_UnknownSqlResultIsReadBackOrRecoveredWithoutOverwriting(bool failReadBack)
    {
        using var fixture = await UpdateFixture.CreateAsync();
        var fault = new UpdateResultFaultStore(fixture.Store);
        var runtime = fixture.Runtime(fault);
        try
        {
            await StartReviewRuntimeAsync(runtime, fixture);
            Assert.True((await runtime.Answers!.SetAnswerAsync("q1", AnswerValue.Selected("B"))).Succeeded);
            await RecordUpdateAsync(runtime, fixture, 2);
            var before = runtime.Answers.AnswerVersion;
            var payload = runtime.Manager.UnlockedPayload;
            fault.LoseNextUpdateResult = true;
            fault.FailReadBack = failReadBack;
            var result = await runtime.ApplyPaperUpdateAsync();
            Assert.Equal(!failReadBack, result.Succeeded);
            Assert.Equal(2, (await fixture.Store.LoadAnswerStateAsync(fixture.Key))!.PaperVersion);
            if (failReadBack)
            {
                Assert.Same(payload, runtime.Manager.UnlockedPayload);
                Assert.Equal(AnswerSessionStatus.WriteBlocked, runtime.Answers.Status);
                Assert.False((await runtime.Answers.SetAnswerAsync("q1", AnswerValue.Selected("A"))).Succeeded);
                await runtime.DisposeAsync();
                runtime = fixture.Runtime();
                Assert.True(await runtime.InitializeLocalRecoveryAsync());
                Assert.Equal(2, runtime.Manager.Descriptor!.PaperVersion);
                Assert.True(await runtime.Manager.PrepareAndDownloadAsync());
                Assert.True((await runtime.Manager.ConfirmParticipationAsync())!.IsSuccess);
                await runtime.SynchronizeLocalAnswersAsync();
            }
            Assert.Equal(before + 1, runtime.Answers!.AnswerVersion);
            Assert.Equal(2, runtime.Answers.PaperVersion);
            AnswerAssert.Equal(AnswerValue.Selected("B"), runtime.Answers.Answers["q1"]);
        }
        finally { await runtime.DisposeAsync(); }
    }

    private static async Task StartReviewRuntimeAsync(StudentExamRuntime runtime, UpdateFixture fixture)
    {
        Assert.True(await runtime.InitializeLocalRecoveryAsync());
        await StartReviewManagerAsync(runtime.Manager, fixture);
        await runtime.SynchronizeLocalAnswersAsync();
        Assert.Equal(AnswerSessionStatus.Active, runtime.Answers!.Status);
    }

    private static async Task<CommandDelivery> RecordUpdateAsync(StudentExamRuntime runtime, UpdateFixture fixture, long version)
    {
        fixture.Api.CurrentPaperVersion = version;
        var command = fixture.Api.Delivery(CommandEventType.PaperUpdated, $"recovery-update-{version}", version, version);
        Assert.True(await runtime.Manager.HandleCommandAsync(command));
        return command;
    }

    private static ApiEnvelope<CommandAcknowledgement> AckReply(AcknowledgeCommandRequest request) => new("server", "node", 1,
        "correlation", new(request.EventId, request.RequestId, request.ProcessingResult, request.ActualPaperVersion,
            request.FailureReason, DateTimeOffset.UtcNow), null);

    private sealed class UpdateResultFaultStore(ILocalStateStore inner) : ILocalStateStore
    {
        public bool LoseNextUpdateResult { get; set; }
        public bool FailReadBack { get; set; }
        private bool _writeCommitted;
        public Task<AnswerStateRecord?> LoadAnswerStateAsync(LocalStateKey key, CancellationToken ct = default) =>
            _writeCommitted && FailReadBack ? throw new IOException("synthetic read unavailable") : inner.LoadAnswerStateAsync(key, ct);
        public async Task<StoreWriteResult> SaveAnswerStateAsync(LocalStateKey key, AnswerStateRecord state, PendingSnapshot? snapshot, CancellationToken ct = default)
        {
            var result = await inner.SaveAnswerStateAsync(key, state, snapshot, ct);
            if (LoseNextUpdateResult && state.PaperVersion > 1 && result.Succeeded)
            {
                LoseNextUpdateResult = false;
                _writeCommitted = true;
                return StoreWriteResult.Fail("synthetic lost local commit result");
            }
            return result;
        }
        public Task<StoreWriteResult> SaveMarksAsync(LocalStateKey key, IReadOnlyCollection<string> marks, CancellationToken ct = default) => inner.SaveMarksAsync(key, marks, ct);
        public Task<PendingSnapshot?> LoadPendingSnapshotAsync(LocalStateKey key, CancellationToken ct = default) => inner.LoadPendingSnapshotAsync(key, ct);
        public Task<(StoreWriteResult Result, FixedSubmissionRecord? Record)> TryFixSubmissionAsync(LocalStateKey key, FixedSubmissionRecord record, CancellationToken ct = default) => inner.TryFixSubmissionAsync(key, record, ct);
        public Task<(StoreWriteResult Result, FixedSubmissionRecord? Record)> TryFixSubmissionAsync(LocalStateKey key, FixedSubmissionRecord record, AnswerStateRecord state, PendingSnapshot snapshot, CancellationToken ct = default) => inner.TryFixSubmissionAsync(key, record, state, snapshot, ct);
        public Task<FixedSubmissionRecord?> LoadFixedSubmissionAsync(LocalStateKey key, CancellationToken ct = default) => inner.LoadFixedSubmissionAsync(key, ct);
        public Task<StoreWriteResult> SavePreparationAsync(LocalStateKey key, PreparationRecord record, CancellationToken ct = default) => inner.SavePreparationAsync(key, record, ct);
        public Task<PreparationRecord?> LoadPreparationAsync(LocalStateKey key, CancellationToken ct = default) => inner.LoadPreparationAsync(key, ct);
        public Task<StoreWriteResult> SavePaperUpdateReviewAsync(LocalStateKey key, PaperUpdateReviewRecord record, CancellationToken ct = default) => inner.SavePaperUpdateReviewAsync(key, record, ct);
        public Task<IReadOnlyList<PaperUpdateReviewRecord>> LoadPaperUpdateReviewsAsync(LocalStateKey key, CancellationToken ct = default) => inner.LoadPaperUpdateReviewsAsync(key, ct);
        public Task<StoreWriteResult> DeleteAsync(LocalStateKey key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
        public void Dispose() { } // 外层夹具拥有真实数据库生命周期。
    }
}
