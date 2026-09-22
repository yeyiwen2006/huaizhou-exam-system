using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;

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
}

public static class RequestIds
{
    public static string New() => Guid.NewGuid().ToString("N");
}
