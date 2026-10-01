using System.Net.Http;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

/// <summary>
/// B06 发件箱补报验收：失焦事件批量上报并在确认后清空、临时失败保留原事件标识重试、
/// 复核确认上报沿用入队时的请求标识、明确的不可重试拒绝按终态删除、重启后待报记录保留。
/// </summary>
public sealed partial class SubmissionSyncServiceTests
{
    [Fact]
    public async Task FocusEvents_AreUploadedInSyncRoundAndOutboxClearedAfterReceipt()
    {
        await using var fixture = await StartedAsync();
        var sync = fixture.CreateSync();
        Assert.True(await fixture.Runtime.RecordFocusLossAsync(
            new FocusLossEvent(DateTimeOffset.UtcNow.AddSeconds(-5), TimeSpan.FromSeconds(5))));
        Assert.Single(await fixture.Store.LoadUploadsAsync(fixture.Key, 10));

        await sync.KickAsync();

        var request = Assert.Single(fixture.SubmissionApi.FocusRequests);
        Assert.Equal("exam", request.ExamId);
        Assert.Equal("exam-candidate", request.CandidateId);
        Assert.Equal("participation", request.ParticipationId);
        Assert.Equal(fixture.Device.DeviceId, request.DeviceId);
        var reported = Assert.Single(request.Events);
        Assert.Equal(5000, reported.DurationMilliseconds);
        Assert.Empty(await fixture.Store.LoadUploadsAsync(fixture.Key, 10));
        await sync.DisposeAsync();
    }

    [Fact]
    public async Task FocusEvents_TemporaryFailureKeepsEventIdForRetry()
    {
        await using var fixture = await StartedAsync();
        var sync = fixture.CreateSync();
        Assert.True(await fixture.Runtime.RecordFocusLossAsync(
            new FocusLossEvent(DateTimeOffset.UtcNow.AddSeconds(-3), TimeSpan.FromSeconds(3))));

        fixture.SubmissionApi.FocusHandler = _ => throw new HttpRequestException("网络中断");
        await sync.KickAsync();
        Assert.Single(fixture.SubmissionApi.FocusRequests);
        // 临时失败：发件箱保留，下一轮用同一事件标识重试（服务器按事件幂等去重）。
        Assert.Single(await fixture.Store.LoadUploadsAsync(fixture.Key, 10));

        fixture.SubmissionApi.FocusHandler = null;
        await sync.KickAsync();
        Assert.Equal(2, fixture.SubmissionApi.FocusRequests.Count);
        Assert.Equal(
            fixture.SubmissionApi.FocusRequests[0].Events[0].EventId,
            fixture.SubmissionApi.FocusRequests[1].Events[0].EventId);
        Assert.Empty(await fixture.Store.LoadUploadsAsync(fixture.Key, 10));
        await sync.DisposeAsync();
    }

    [Fact]
    public async Task FocusEvents_DefinitiveRejectionDropsRecordInsteadOfBlockingProgress()
    {
        await using var fixture = await StartedAsync();
        var sync = fixture.CreateSync();
        Assert.True(await fixture.Runtime.RecordFocusLossAsync(
            new FocusLossEvent(DateTimeOffset.UtcNow.AddSeconds(-2), TimeSpan.FromSeconds(2))));

        fixture.SubmissionApi.FocusHandler = _ => new ApiEnvelope<FocusEventsReceipt>(
            "server", "node", 1, "correlation", null,
            new ApiError(ErrorCodes.InvalidInput, "请求无效。", Retryable: false));
        await sync.KickAsync();
        Assert.Empty(await fixture.Store.LoadUploadsAsync(fixture.Key, 10));
        // 明确拒绝不拖垮进度同步。
        Assert.Equal(SubmissionSyncPhase.Progressing, sync.Phase);
        await sync.DisposeAsync();
    }

    [Fact]
    public async Task ReviewConfirmation_UploadsOnceWithEnqueuedRequestIdAndClearsOutbox()
    {
        await using var fixture = await StartedAsync();
        var sync = fixture.CreateSync();
        // 直接以运行时的载荷形状入队（复核确认路径在运行时测试中覆盖）。
        var enqueued = await fixture.Store.EnqueueUploadAsync(fixture.Key, new PendingUploadRecord(
            "review:2", "review",
            System.Text.Json.JsonSerializer.Serialize(
                new StudentExamRuntime.ReviewConfirmationPayload(2, "review-request-1"),
                JsonDefaults.Create())));
        Assert.True(enqueued.Succeeded);

        fixture.SubmissionApi.ReviewHandler = _ => new ApiEnvelope<Exam.Contracts.Versioning.ExamVersionSnapshot>(
            "server", "node", 1, "correlation", null, null);
        await sync.KickAsync();

        var request = Assert.Single(fixture.SubmissionApi.ReviewRequests);
        Assert.Equal("participation", request.ParticipantId);
        Assert.Equal(2, request.PaperVersion);
        Assert.Equal("review-request-1", request.RequestId);
        Assert.Equal(fixture.Device.DeviceId, request.DeviceId);
        Assert.Empty(await fixture.Store.LoadUploadsAsync(fixture.Key, 10));
        await sync.DisposeAsync();
    }

    [Fact]
    public async Task ReviewConfirmation_DefinitiveRejectionIsTerminalAndDropsRecord()
    {
        await using var fixture = await StartedAsync();
        var sync = fixture.CreateSync();
        // 默认假件返回不可重试拒绝（如版本已被更新版本的确认超越）。
        var enqueued = await fixture.Store.EnqueueUploadAsync(fixture.Key, new PendingUploadRecord(
            "review:1", "review",
            System.Text.Json.JsonSerializer.Serialize(
                new StudentExamRuntime.ReviewConfirmationPayload(1, "review-request-old"),
                JsonDefaults.Create())));
        Assert.True(enqueued.Succeeded);

        await sync.KickAsync();

        Assert.Single(fixture.SubmissionApi.ReviewRequests);
        Assert.Empty(await fixture.Store.LoadUploadsAsync(fixture.Key, 10));
        Assert.Equal(SubmissionSyncPhase.Progressing, sync.Phase);
        await sync.DisposeAsync();
    }

    [Fact]
    public async Task FocusEvents_SurviveRestartAndUploadFromRecoveredRuntime()
    {
        await using var fixture = await StartedAsync();
        Assert.True(await fixture.Runtime.RecordFocusLossAsync(
            new FocusLossEvent(DateTimeOffset.UtcNow.AddSeconds(-4), TimeSpan.FromSeconds(4))));

        // 重启恢复：待报记录仍在发件箱，由新同步服务实例继续补报。
        await fixture.RestartForRecoveryAsync();
        var sync = fixture.CreateSync();
        Assert.Single(await fixture.Store.LoadUploadsAsync(fixture.Key, 10));

        await sync.KickAsync();

        Assert.Single(fixture.SubmissionApi.FocusRequests);
        Assert.Empty(await fixture.Store.LoadUploadsAsync(fixture.Key, 10));
        await sync.DisposeAsync();
    }
}
