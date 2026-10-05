using Exam.Contracts.Activation;
using Exam.Contracts.Administration;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

/// <summary>
/// C04 学生端在线状态上报：一次运行一个连接、序号严格递增、答题改动按短窗口合并、
/// 网络失败按原序号幂等重试、业务拒绝后重建连接、退出后不再上报。
/// </summary>
public sealed class StudentPresenceTests
{
    private static readonly TimeSpan Merge = TimeSpan.FromMilliseconds(40);

    [Fact]
    public async Task EnsureOpen_OpensOncePerRunAndStartsOnTheLoginPage()
    {
        var api = new RecordingPresenceApi();
        using var reporter = new StudentPresenceReporter(api, () => Snapshot(), Merge);

        Assert.True(await reporter.EnsureOpenAsync());
        Assert.True(await reporter.EnsureOpenAsync());

        Assert.Single(api.Opens);
        Assert.True(reporter.IsOpen);
        Assert.Equal(StudentPresencePages.Login, reporter.Page);
        Assert.Null(reporter.LastError);
    }

    [Fact]
    public async Task ReportPage_SendsStrictlyIncreasingSequencesWithTheRequestedPage()
    {
        var api = new RecordingPresenceApi();
        using var reporter = new StudentPresenceReporter(api, () => Snapshot(), Merge);

        await reporter.ReportPageAsync(StudentPresencePages.SelectingExam);
        await reporter.ReportPageAsync(StudentPresencePages.Waiting);
        await reporter.ReportPageAsync(StudentPresencePages.Answering);

        Assert.Equal(new[] { StudentPresencePages.SelectingExam, StudentPresencePages.Waiting, StudentPresencePages.Answering },
            api.Updates.Select(item => item.Page));
        Assert.Equal(new long[] { 1, 2, 3 }, api.Updates.Select(item => item.Sequence));
        Assert.All(api.Updates, item => Assert.Equal(1, item.Generation));
    }

    [Fact]
    public async Task ReportProgress_CoalescesRapidChangesIntoOneReport()
    {
        var api = new RecordingPresenceApi();
        var question = "q-1";
        using var reporter = new StudentPresenceReporter(api, () => Snapshot(question: question), Merge);
        await reporter.ReportPageAsync(StudentPresencePages.Answering);

        // 答题改动只触发合并上报：窗口内多次改动合并成一次，且带窗口结束时的题号。
        reporter.ReportProgress();
        reporter.ReportProgress();
        question = "q-2";
        reporter.ReportProgress();
        await Task.Delay(Merge * 4);

        var update = Assert.Single(api.Updates.Skip(1));
        Assert.Equal(StudentPresencePages.Answering, update.Page);
        Assert.Equal("q-2", update.CurrentQuestionId);
        Assert.Equal(2, update.Sequence);
    }

    [Fact]
    public async Task ReportProgress_DoesNotResendContentTheServerAlreadyAccepted()
    {
        var api = new RecordingPresenceApi();
        using var reporter = new StudentPresenceReporter(api, () => Snapshot(), Merge);
        await reporter.ReportPageAsync(StudentPresencePages.Waiting);
        var accepted = api.Updates.Count;

        reporter.ReportProgress();
        await Task.Delay(Merge * 4);

        Assert.Equal(accepted, api.Updates.Count);
    }

    [Fact]
    public async Task Heartbeat_RefreshesTheSameContentWithANewSequence()
    {
        var api = new RecordingPresenceApi();
        using var reporter = new StudentPresenceReporter(api, () => Snapshot(), Merge);
        await reporter.ReportPageAsync(StudentPresencePages.Answering);
        await reporter.HeartbeatAsync();
        await reporter.HeartbeatAsync();

        Assert.Equal(new long[] { 1, 2, 3 }, api.Updates.Select(item => item.Sequence));
        // 内容不变也占用新序号：服务端用它刷新最后通信时间，离线判定才不会误判。
        Assert.All(api.Updates, item => Assert.Equal(StudentPresencePages.Answering, item.Page));
    }

    [Fact]
    public async Task TransientFailure_RetriesTheSameSequenceAndContent()
    {
        var api = new RecordingPresenceApi { FailNextUpdates = 1 };
        using var reporter = new StudentPresenceReporter(api, () => Snapshot(), Merge);

        await reporter.ReportPageAsync(StudentPresencePages.Waiting);
        await reporter.ReportPageAsync(StudentPresencePages.Waiting);

        Assert.Equal(2, api.Updates.Count);
        // 服务端按序号幂等重放：失败那次没提交，重试用同一序号与同一内容。
        Assert.Equal(api.Updates[0].Sequence, api.Updates[1].Sequence);
        Assert.Equal(api.Updates[0].Page, api.Updates[1].Page);
        Assert.Null(reporter.LastError);
    }

    [Fact]
    public async Task BusinessRejection_ReopensTheConnectionOnlyOncePerCooldown()
    {
        var api = new RecordingPresenceApi
        {
            UpdateHandler = _ => Fail<DevicePresenceReceipt>("服务器未接受在线状态。"),
        };
        using var reporter = new StudentPresenceReporter(api, () => Snapshot(), Merge);

        await reporter.ReportPageAsync(StudentPresencePages.Waiting);
        Assert.Equal(2, api.Opens.Count);
        Assert.Equal("服务器未接受当前在线状态。", reporter.LastError);

        // 冷却期内再次被拒绝不再重建连接，避免反复冲同一个失败。
        await reporter.ReportPageAsync(StudentPresencePages.Answering);
        Assert.Equal(2, api.Opens.Count);
    }

    [Fact]
    public async Task Exit_SendsDisconnectAndStopsReportingUntilTheNextOpen()
    {
        var api = new RecordingPresenceApi();
        using var reporter = new StudentPresenceReporter(api, () => Snapshot(), Merge);
        await reporter.ReportPageAsync(StudentPresencePages.Answering);

        await reporter.ExitAsync();

        var exit = api.Updates[^1];
        Assert.True(exit.Disconnect);
        Assert.Equal(StudentPresencePages.Exited, exit.Page);
        Assert.Null(exit.SessionId);
        Assert.Null(exit.ExamId);
        Assert.False(reporter.IsOpen);

        var afterExit = api.Updates.Count;
        await reporter.ReportPageAsync(StudentPresencePages.Answering);
        reporter.ReportProgress();
        await Task.Delay(Merge * 4);
        Assert.Equal(afterExit, api.Updates.Count);

        // 重新登录后才再次建立连接。
        Assert.True(await reporter.EnsureOpenAsync());
        Assert.Equal(2, api.Opens.Count);
    }

    [Fact]
    public void DownloadState_MapsUpdatePhaseAndPackagePhaseToTheServerVocabulary()
    {
        Assert.Equal(StudentPresenceDownloads.Failed,
            StudentPresenceDownloads.From(ExamPackageManager.Phase.Unlocked, true, 2, 2, PaperUpdatePhase.Failed));
        Assert.Equal(StudentPresenceDownloads.Updating,
            StudentPresenceDownloads.From(ExamPackageManager.Phase.Downloaded, true, 1, 2, PaperUpdatePhase.Pending));
        Assert.Equal(StudentPresenceDownloads.Updated,
            StudentPresenceDownloads.From(ExamPackageManager.Phase.Unlocked, true, 2, 2, PaperUpdatePhase.Applied));
        Assert.Equal(StudentPresenceDownloads.Downloading,
            StudentPresenceDownloads.From(ExamPackageManager.Phase.Downloading, false, 0, 0, null));
        Assert.Equal(StudentPresenceDownloads.Downloaded,
            StudentPresenceDownloads.From(ExamPackageManager.Phase.Unlocked, true, 1, 1, null));
        Assert.Equal(StudentPresenceDownloads.NotDownloaded,
            StudentPresenceDownloads.From(ExamPackageManager.Phase.Idle, false, 0, 0, null));
        Assert.Equal(StudentPresenceDownloads.NotDownloaded,
            StudentPresenceDownloads.From(ExamPackageManager.Phase.Revoked, true, 1, 1, null));
    }

    private static ApiEnvelope<T> Ok<T>(T result) => new("synthetic-server", "node-a", 1, "req", result, null);

    private static ApiEnvelope<T> Fail<T>(string message) =>
        new("synthetic-server", "node-a", 1, "req", default, new ApiError("VERSION_CONFLICT", message));

    private static StudentPresenceSnapshot Snapshot(string? question = null, string? examId = "exam-1") =>
        new("session-1", examId, question, 1, StudentPresenceDownloads.Downloaded);

    private sealed class RecordingPresenceApi : IStudentApi
    {
        public List<OpenDevicePresenceRequest> Opens { get; } = new();
        public List<UpdateDevicePresenceRequest> Updates { get; } = new();
        public int FailNextUpdates { get; set; }
        public Func<UpdateDevicePresenceRequest, ApiEnvelope<DevicePresenceReceipt>>? UpdateHandler { get; set; }

        public Task<ApiEnvelope<DevicePresenceLease>> OpenPresenceAsync(
            OpenDevicePresenceRequest request, CancellationToken ct = default)
        {
            Opens.Add(request);
            return Task.FromResult(Ok(new DevicePresenceLease(request.ConnectionId, Opens.Count, DateTimeOffset.UtcNow)));
        }

        public Task<ApiEnvelope<DevicePresenceReceipt>> UpdatePresenceAsync(
            UpdateDevicePresenceRequest request, CancellationToken ct = default)
        {
            Updates.Add(request);
            if (FailNextUpdates > 0)
            {
                FailNextUpdates--;
                throw new HttpRequestException("网络暂不可用。");
            }
            if (UpdateHandler is { } handler) return Task.FromResult(handler(request));
            return Task.FromResult(Ok(new DevicePresenceReceipt(request.Generation, request.Sequence, DateTimeOffset.UtcNow)));
        }

        public void ConfigureConnection(DeviceRecord device) => throw new NotSupportedException();
        public Task<ApiEnvelope<ActivationRequestResult>> SubmitActivationAsync(SubmitActivationRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<ActivationRequestResult>> GetActivationStatusAsync(GetActivationStatusRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<ActivationResult>> ReactivateAsync(ReactivateDeviceRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<ActivationResult>> CancelActivationAsync(CancelOwnActivationRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<StudentSessionResult>> LoginAsync(LoginStudentRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<EntryPreparationResult>> PrepareEntryAsync(PrepareExamEntryRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ExamOverview>> GetExamOverviewsAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<ServerTimeSnapshot>> GetServerTimeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<PersistedProgress>> SyncProgressAsync(SyncProgressRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<SubmissionReceipt>> SubmitFinalAnswersAsync(SubmitFinalAnswersRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<SubmissionReceipt>> GetSubmissionReceiptAsync(string participationId, long entryVersion, string deviceId, long authorizationVersion, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<FocusEventsReceipt>> ReportFocusEventsAsync(ReportFocusEventsRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiEnvelope<Exam.Contracts.Versioning.ExamVersionSnapshot>> ConfirmPaperReviewAsync(string examId, string participantId, Exam.Contracts.Versioning.ConfirmPaperReviewRequest request, CancellationToken ct = default) => throw new NotSupportedException();

    }
}
