using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;

namespace Exam.Student.Core;

public sealed record ExamOverview(
    string ExamId,
    string Name,
    DateTimeOffset ScheduledStart,
    DateTimeOffset ScheduledEnd,
    IReadOnlyList<string> Subjects,
    ExamLifecycle Lifecycle,
    string? CategoryName = null);

public interface IStudentApi
{
    void ConfigureConnection(DeviceRecord device);
    Task<ApiEnvelope<ActivationRequestResult>> SubmitActivationAsync(
        SubmitActivationRequest request, CancellationToken ct = default);
    Task<ApiEnvelope<ActivationRequestResult>> GetActivationStatusAsync(
        GetActivationStatusRequest request, CancellationToken ct = default);
    Task<ApiEnvelope<ActivationResult>> ReactivateAsync(
        ReactivateDeviceRequest request, CancellationToken ct = default);
    Task<ApiEnvelope<ActivationResult>> CancelActivationAsync(
        CancelOwnActivationRequest request, CancellationToken ct = default);
    Task<ApiEnvelope<StudentSessionResult>> LoginAsync(
        LoginStudentRequest request, CancellationToken ct = default);
    Task<ApiEnvelope<EntryPreparationResult>> PrepareEntryAsync(
        PrepareExamEntryRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<ExamOverview>> GetExamOverviewsAsync(string sessionId, CancellationToken ct = default);
    Task<ApiEnvelope<ServerTimeSnapshot>> GetServerTimeAsync(CancellationToken ct = default);

    /// <summary>可靠交卷：上传当前答案进度；同一 RequestId 的重试按幂等重放处理。</summary>
    Task<ApiEnvelope<PersistedProgress>> SyncProgressAsync(
        SyncProgressRequest request, CancellationToken ct = default);

    /// <summary>可靠交卷：提交固定答卷；同一 RequestId/SubmissionId 重试返回原结果和回执。</summary>
    Task<ApiEnvelope<SubmissionReceipt>> SubmitFinalAnswersAsync(
        SubmitFinalAnswersRequest request, CancellationToken ct = default);

    /// <summary>可靠交卷：按参试与设备查询服务器已持有的最终提交及回执，用于丢回执后恢复原结果。</summary>
    Task<ApiEnvelope<SubmissionReceipt>> GetSubmissionReceiptAsync(
        string participationId, long entryVersion, string deviceId, long authorizationVersion,
        CancellationToken ct = default);

    /// <summary>批量补报复焦事件；服务器按 (ParticipationId, EventId) 幂等留痕，回执列出全部受理标识。</summary>
    Task<ApiEnvelope<FocusEventsReceipt>> ReportFocusEventsAsync(
        ReportFocusEventsRequest request, CancellationToken ct = default);

    /// <summary>向服务端确认已复核修订试卷；同一版本重复确认按幂等重放处理。</summary>
    Task<ApiEnvelope<Exam.Contracts.Versioning.ExamVersionSnapshot>> ConfirmPaperReviewAsync(
        string examId, string participantId,
        Exam.Contracts.Versioning.ConfirmPaperReviewRequest request, CancellationToken ct = default);
}

public static class RequestIds
{
    public static string New() => Guid.NewGuid().ToString("N");
}
