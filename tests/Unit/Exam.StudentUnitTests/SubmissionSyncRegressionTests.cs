using System.IO;
using System.Net.Http;
using Exam.Application.Commands;
using Microsoft.Data.Sqlite;
using Exam.Application.Submissions;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Domain.Submissions;
using Exam.Server.Api;
using Exam.Student.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Exam.StudentUnitTests;

public sealed partial class SubmissionSyncServiceTests
{
    [Fact]
    public async Task Review_ActualA05HttpEndpointConfirmsItsFlatReceipt()
    {
        await using var fixture = await StartedAsync();
        await fixture.Answers!.FixSubmissionAsync("http-submission");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExamServer:ServerId"] = "server", ["ExamServer:NodeId"] = "node", ["ExamServer:ServerEpoch"] = "1"
        });
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            var json = JsonDefaults.Create();
            options.SerializerOptions.PropertyNamingPolicy = json.PropertyNamingPolicy;
            foreach (var converter in json.Converters) options.SerializerOptions.Converters.Add(converter);
        });
        var store = new InMemorySubmissionStore();
        var queue = new InMemoryGradingTaskQueue();
        await store.SetAuthorizationStateAsync(new("exam", "exam-candidate", "participation", 1,
            "device", 1, ParticipationBusinessState.Answering, 1, 1));
        var access = new InMemoryParticipationAccessRegistry();
        access.Set(new("exam", "exam-candidate", "participation", 1, "device", 1,
            ExamLifecycle.Running, ParticipationBusinessState.Answering, 1, 1));
        builder.Services.AddSingleton(new SubmissionService(store, queue, accessReader: access));
        builder.Services.AddSingleton(new CollectionCompletionService(store, new InMemoryCollectionCompletionStore()));
        // B06 失焦端点属于同一分组：分组在首个请求时整体构建，服务必须在 DI 中可用。
        builder.Services.AddSingleton(new FocusEventService(new InMemoryFocusEventStore()));
        await using var app = builder.Build();
        app.MapGroup("/api/v1").MapSubmissionApi();
        await app.StartAsync();
        using var api = new HttpStudentApi(app.GetTestServer().CreateHandler());
        api.ConfigureConnection(fixture.Device);
        await using var sync = new SubmissionSyncService(api, fixture.Runtime, fixture.Device);
        await sync.KickAsync();
        Assert.Equal(SubmissionSyncPhase.Confirmed, sync.Phase);
        Assert.Equal("http-submission", sync.ConfirmedReceipt?.SubmissionId);
        Assert.Equal(1, queue.Count);
        var found = await api.GetSubmissionReceiptAsync("participation", 1, "device", 1);
        Assert.True(found.IsSuccess);
        Assert.Equal(sync.ConfirmedReceipt, found.Result);
        var local = (await fixture.Store.LoadFixedSubmissionAsync(fixture.Key))!;
        Assert.Equal(sync.ConfirmedReceipt, local.Receipt);
    }

    [Fact]
    public async Task Review_ProgressUsesTheDurableSnapshotRequestId()
    {
        await using var fixture = await StartedAsync();
        await fixture.Answers!.SetAnswerAsync("q1", AnswerValue.Selected("A"));
        var saved = (await fixture.Store.LoadPendingSnapshotAsync(fixture.Key))!;
        await using var sync = fixture.CreateSync();
        await sync.KickAsync();
        Assert.Equal(saved.RequestId, Assert.Single(fixture.SubmissionApi.ProgressRequests).RequestId);
    }

    [Fact]
    public async Task Review_BlockedStateStopsAllSubsequentNetworkWrites()
    {
        await using var fixture = await StartedAsync();
        var record = (await fixture.Answers!.FixSubmissionAsync("original")).Record!;
        fixture.SubmissionApi.SubmitHandler = _ => new("server", "node", 1, "request", null,
            new ApiError(ErrorCodes.AlreadySubmitted, "合成冲突", Retryable: false));
        fixture.SubmissionApi.QueryHandler = () => fixture.SubmissionApi.BuildFinal(record with { SubmissionId = "other" });
        await using var sync = fixture.CreateSync();
        await sync.KickAsync();
        Assert.Equal(SubmissionSyncPhase.Blocked, sync.Phase);
        await sync.KickAsync();
        Assert.Single(fixture.SubmissionApi.FinalRequests);
        Assert.Single(fixture.SubmissionApi.QueryCalls);
    }

    [Fact]
    public async Task Review_VerifiedEndFreezesAndSubmitsTheLastSavedEdits()
    {
        await using var fixture = await StartedAsync();
        await fixture.Answers!.SetAnswerAsync("q1", AnswerValue.Selected("B"));
        Assert.True(await fixture.Runtime.Manager.HandleCommandAsync(fixture.Api.End()));
        await fixture.Runtime.SynchronizeLocalAnswersAsync();
        Assert.True(fixture.Runtime.HasFixedSubmission);
        await using var sync = fixture.CreateSync();
        await sync.KickAsync();
        Assert.Equal(SubmissionSyncPhase.Confirmed, sync.Phase);
        Assert.Equal(new[] { "B" }, Assert.Single(fixture.SubmissionApi.FinalRequests).FrozenAnswers["q1"].OptionIds);
        Assert.Empty(fixture.SubmissionApi.ProgressRequests);
    }
    [Fact]
    public async Task Review_RealRuntimeRestartResendsTheOriginalFixedRecordWithoutUnlock()
    {
        await using var fixture = await StartedAsync();
        var original = (await fixture.Answers!.FixSubmissionAsync("restart-submission")).Record!;
        fixture.SubmissionApi.SubmitHandler = request =>
        {
            fixture.SubmissionApi.Persist(request);
            throw new HttpRequestException("合成丢响�?");
        };
        await using (var first = fixture.CreateSync())
        {
            await first.KickAsync();
            Assert.Equal(SubmissionSyncPhase.WaitingRetry, first.Phase);
        }
        await fixture.RestartForRecoveryAsync();
        Assert.Null(fixture.Runtime.Answers);
        Assert.True(fixture.Runtime.HasFixedSubmission);
        await using var recovered = fixture.CreateSync();
        await recovered.KickAsync();
        Assert.Equal(SubmissionSyncPhase.Confirmed, recovered.Phase);
        Assert.All(fixture.SubmissionApi.FinalRequests, request =>
        {
            Assert.Equal(original.RequestId, request.RequestId);
            Assert.Equal(original.SubmissionId, request.SubmissionId);
            Assert.Equal(original.AuthorizationVersion, request.AuthorizationVersion);
        });
        Assert.Equal(recovered.ConfirmedReceipt, (await fixture.Store.LoadFixedSubmissionAsync(fixture.Key))!.Receipt);
    }

    [Fact]
    public async Task Review_EndPersistedBeforeCrashRecoversAndFreezesOnlyTheOriginalSnapshot()
    {
        await using var fixture = await StartedAsync();
        await fixture.Answers!.SetAnswerAsync("q1", AnswerValue.Selected("B"));
        Assert.True(await fixture.Runtime.Manager.HandleCommandAsync(fixture.Api.End()));
        // 故意不调用运行时�? END 固定步骤，模拟两个本地存储之间的进程中断�?
        Assert.Null(await fixture.Store.LoadFixedSubmissionAsync(fixture.Key));
        await fixture.RestartForRecoveryAsync();
        Assert.Null(fixture.Runtime.Answers);
        var fixedRecord = Assert.IsType<FixedSubmissionRecord>(fixture.Runtime.FixedSubmission);
        Assert.Equal(new[] { "B" }, fixedRecord.FrozenAnswers["q1"].OptionIds);
        await using var sync = fixture.CreateSync();
        await sync.KickAsync();
        Assert.Equal(SubmissionSyncPhase.Confirmed, sync.Phase);
    }

    [Fact]
    public async Task Review_ProgressIdentitySurvivesARealRuntimeRestart()
    {
        await using var fixture = await StartedAsync();
        await using (var first = fixture.CreateSync()) await first.KickAsync();
        await fixture.RestartForRecoveryAsync(resumeAnswering: true);
        await using var next = fixture.CreateSync();
        await next.KickAsync();
        Assert.Equal(2, fixture.SubmissionApi.ProgressRequests.Count);
        Assert.Equal(fixture.SubmissionApi.ProgressRequests[0].RequestId, fixture.SubmissionApi.ProgressRequests[1].RequestId);
        Assert.Equal(fixture.SubmissionApi.ProgressRequests[0].AnswerVersion, fixture.SubmissionApi.ProgressRequests[1].AnswerVersion);
    }

    [Fact]
    public async Task Review_UnchangedProgressIsReplayedEverySixtyMonotonicSeconds()
    {
        await using var fixture = await StartedAsync();
        var time = new SubmissionTestTime();
        await using var sync = new SubmissionSyncService(fixture.SubmissionApi, fixture.Runtime, fixture.Device, time);
        await sync.KickAsync();
        await sync.KickAsync();
        Assert.Single(fixture.SubmissionApi.ProgressRequests);
        time.Advance(TimeSpan.FromSeconds(60));
        await sync.KickAsync();
        Assert.Equal(2, fixture.SubmissionApi.ProgressRequests.Count);
        Assert.Equal(fixture.SubmissionApi.ProgressRequests[0].RequestId, fixture.SubmissionApi.ProgressRequests[1].RequestId);
    }

    [Fact]
    public async Task Review_SavedEditsWakeTheRunningWorkerWithoutWaitingForThePeriodicTick()
    {
        await using var fixture = await StartedAsync();
        var uploaded = new TaskCompletionSource<SyncProgressRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SubmissionApi.ProgressHandler = request =>
        {
            uploaded.TrySetResult(request);
            return new("server", "node", 1, request.RequestId,
                new(request.ExamId, request.CandidateId, request.ParticipationId, request.EntryVersion, request.DeviceId,
                    request.AuthorizationVersion, request.AnswerVersion, request.PaperVersion, request.Answers,
                    request.CurrentQuestionId, DateTimeOffset.UtcNow), null);
        };
        await using var sync = fixture.CreateSync();
        var run = sync.RunAsync(CancellationToken.None);
        await uploaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        uploaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.Answers!.SetAnswerAsync("q1", AnswerValue.Selected("A"));
        var latest = await uploaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(fixture.Answers.AnswerVersion, latest.AnswerVersion);
        Assert.Equal(new[] { "A" }, latest.Answers["q1"].OptionIds);
        await sync.DisposeAsync();
        await run;
    }

    [Theory]
    [InlineData("exam")]
    [InlineData("candidate")]
    [InlineData("participation")]
    [InlineData("submission")]
    [InlineData("receipt")]
    [InlineData("paper")]
    [InlineData("grading")]
    [InlineData("time")]
    public async Task Review_MalformedOrMismatchedReceiptsNeverBecomeSuccess(string field)
    {
        await using var fixture = await StartedAsync();
        var frozen = (await fixture.Answers!.FixSubmissionAsync("original")).Record!;
        var receipt = fixture.SubmissionApi.BuildFinal(frozen).Receipt;
        receipt = field switch
        {
            "exam" => receipt with { ExamId = "other" },
            "candidate" => receipt with { CandidateId = "other" },
            "participation" => receipt with { ParticipationId = "other" },
            "submission" => receipt with { SubmissionId = "other" },
            "receipt" => receipt with { ReceiptId = "" },
            "paper" => receipt with { PaperVersion = 2 },
            "grading" => receipt with { GradingVersion = 0 },
            _ => receipt with { ReceivedAt = default }
        };
        fixture.SubmissionApi.SubmitHandler = _ => new("server", "node", 1, "request", receipt, null);
        await using var sync = fixture.CreateSync();
        await sync.KickAsync();
        await sync.KickAsync();
        Assert.Equal(SubmissionSyncPhase.Blocked, sync.Phase);
        Assert.Null(sync.ConfirmedReceipt);
        Assert.Null((await fixture.Store.LoadFixedSubmissionAsync(fixture.Key))!.Receipt);
        Assert.Single(fixture.SubmissionApi.FinalRequests);
    }

    [Fact]
    public async Task Review_TransientReceiptQueryFailureRemainsRetryable()
    {
        await using var fixture = await StartedAsync();
        var record = (await fixture.Answers!.FixSubmissionAsync("original")).Record!;
        fixture.SubmissionApi.SubmitHandler = _ => new("server", "node", 1, "request", null,
            new ApiError(ErrorCodes.AlreadySubmitted, "合成已有答卷", Retryable: false));
        fixture.SubmissionApi.QueryResponseHandler = () => new("server", "node", 1, "query", null,
            new ApiError(ErrorCodes.ServiceRecovering, "合成临时查询失败", Retryable: true));
        await using var sync = fixture.CreateSync();
        await sync.KickAsync();
        Assert.Equal(SubmissionSyncPhase.WaitingRetry, sync.Phase);
        fixture.SubmissionApi.QueryResponseHandler = null;
        fixture.SubmissionApi.QueryHandler = () => fixture.SubmissionApi.BuildFinal(record);
        await sync.KickAsync();
        Assert.Equal(SubmissionSyncPhase.Confirmed, sync.Phase);
        Assert.Equal(2, fixture.SubmissionApi.QueryCalls.Count);
    }

    [Fact]
    public async Task Review_RevokedProgressAuthorizationAlsoStopsLocalEditing()
    {
        await using var fixture = await StartedAsync();
        fixture.SubmissionApi.ProgressHandler = _ => new("server", "node", 1, "request", null,
            new ApiError(ErrorCodes.AuthorizationRevoked, "合成撤权", Retryable: false));
        await using var sync = fixture.CreateSync();
        await sync.KickAsync();
        Assert.Equal(SubmissionSyncPhase.Blocked, sync.Phase);
        Assert.Equal(AnswerSessionStatus.PermissionDenied, fixture.Answers!.Status);
        Assert.False((await fixture.Answers.SetAnswerAsync("q1", AnswerValue.Selected("B"))).Succeeded);
        await sync.KickAsync();
        Assert.Single(fixture.SubmissionApi.ProgressRequests);
    }

    [Fact]
    public async Task Review_ReceiptWriteFailureRetriesLocallyWithoutResubmitting()
    {
        await using var fixture = await StartedAsync();
        await fixture.Answers!.FixSubmissionAsync("original");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER deny_receipt BEFORE UPDATE ON fixed_submission BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;";
        await command.ExecuteNonQueryAsync();
        await using var sync = fixture.CreateSync();
        await sync.KickAsync();
        Assert.Equal(SubmissionSyncPhase.WaitingRetry, sync.Phase);
        Assert.Null(sync.ConfirmedReceipt);
        Assert.Single(fixture.SubmissionApi.FinalRequests);
        command.CommandText = "DROP TRIGGER deny_receipt;";
        await command.ExecuteNonQueryAsync();
        await sync.KickAsync();
        Assert.Equal(SubmissionSyncPhase.Confirmed, sync.Phase);
        Assert.Single(fixture.SubmissionApi.FinalRequests);
        var receipt = sync.ConfirmedReceipt!;
        Assert.Equal(receipt, (await fixture.Store.LoadFixedSubmissionAsync(fixture.Key))!.Receipt);
        Assert.False((await fixture.Store.SaveSubmissionReceiptAsync(fixture.Key, receipt with { ReceiptId = "replacement" })).Succeeded);
        Assert.Equal(receipt, (await fixture.Store.LoadFixedSubmissionAsync(fixture.Key))!.Receipt);
    }

    [Fact]
    public async Task Review_DisposalAwaitsAllAdmittedRoundsWithoutObjectDisposedRaces()
    {
        await using var fixture = await StartedAsync();
        var sync = fixture.CreateSync();
        var run = sync.RunAsync(CancellationToken.None);
        var requests = Enumerable.Range(0, 24).Select(_ => sync.KickAsync()).ToArray();
        await sync.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var request in requests)
        {
            try { await request; }
            catch (OperationCanceledException) { }
        }
        await run;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => sync.KickAsync());
    }

    [Fact]
    public async Task Review_SlowClockSynchronizationCannotDelayFinalSubmissionOrShutdown()
    {
        await using var fixture = await StartedAsync();
        await fixture.Answers!.FixSubmissionAsync("clock-isolation");
        var timeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SubmissionApi.TimeHandler = async ct =>
        {
            timeStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("取消前不应结束合成校时等待�?");
        };
        var clock = new ServerClock();
        await using var sync = new SubmissionSyncService(fixture.SubmissionApi, fixture.Runtime, fixture.Device, clock: clock);
        var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sync.StatusChanged += (_, _) => { if (sync.Phase == SubmissionSyncPhase.Confirmed) confirmed.TrySetResult(); };
        var run = sync.RunAsync(CancellationToken.None);
        await timeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(clock.IsSynchronized);
        Assert.Equal(SubmissionSyncPhase.Confirmed, sync.Phase);
        await sync.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await run;
    }

    [Fact]
    public async Task Review_RestoredLocalWriteWakesTheWorkerEvenWhenVisibleVersionDidNotChange()
    {
        await using var fixture = await StartedAsync();
        await using var sync = fixture.CreateSync();
        await sync.KickAsync();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER deny_state BEFORE UPDATE ON answer_state BEGIN SELECT RAISE(ABORT, 'synthetic write failure'); END;";
        await command.ExecuteNonQueryAsync();
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sync.StatusChanged += (_, _) => { if (sync.Phase == SubmissionSyncPhase.Idle) idle.TrySetResult(); };
        var run = sync.RunAsync(CancellationToken.None);
        Assert.False((await fixture.Answers!.SetAnswerAsync("q1", AnswerValue.Selected("B"))).Succeeded);
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        command.CommandText = "DROP TRIGGER deny_state;";
        await command.ExecuteNonQueryAsync();
        var uploaded = new TaskCompletionSource<SyncProgressRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SubmissionApi.ProgressHandler = request => { uploaded.TrySetResult(request); return null; };
        await fixture.Runtime.RetryLocalSaveAsync();
        var request = await uploaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(fixture.Answers.AnswerVersion, request.AnswerVersion);
        Assert.Equal(new[] { "B" }, request.Answers["q1"].OptionIds);
        await sync.DisposeAsync();
        await run;
    }

    private sealed class SubmissionTestTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.MinValue;
        public void Advance(TimeSpan span) => _ticks += span.Ticks;
    }

}
