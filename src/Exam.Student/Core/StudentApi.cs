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

    /// <summary>B05：上传当前答案进度；同一 RequestId 的重试按幂等重放处理。</summary>
    Task<ApiEnvelope<PersistedProgress>> SyncProgressAsync(
        SyncProgressRequest request, CancellationToken ct = default);

    /// <summary>B05：提交固定答卷；同一 RequestId/SubmissionId 重试返回原结果和回执。</summary>
    Task<ApiEnvelope<FinalSubmission>> SubmitFinalAnswersAsync(
        SubmitFinalAnswersRequest request, CancellationToken ct = default);

    /// <summary>B05：按参试与设备查询服务器已持有的最终提交及回执，用于丢回执后恢复原结果。</summary>
    Task<ApiEnvelope<FinalSubmission>> GetFinalSubmissionAsync(
        string participationId, long entryVersion, string deviceId, long authorizationVersion,
        CancellationToken ct = default);
}

public static class RequestIds
{
    public static string New() => Guid.NewGuid().ToString("N");
}
