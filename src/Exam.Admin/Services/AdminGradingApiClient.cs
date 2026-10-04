using System.Net.Http;

using Exam.Contracts.Grading;
using Exam.Contracts.Versioning;
using Exam.Infrastructure.Packaging;

namespace Exam.Admin.Services;

public sealed partial class AdminApiClient : IGradingRevisionClient
{
    public Task<string> GetGradingOperatorAsync(CancellationToken ct = default)
    { EnsureConfigured(); ct.ThrowIfCancellationRequested(); return Task.FromResult(OperatorId); }

    public Task<ExamVersionSnapshot> SendGradingRevisionAsync(PublishGradingRulesRequest request,
        CancellationToken ct = default) => SendAsync<ExamVersionSnapshot>(HttpMethod.Post,
            $"api/v1/versioning/exams/{Uri.EscapeDataString(request.ExamId)}/grading-publications", request, ct,
            preserveRequestId: true);

    public Task<GradingMaterialSnapshot> GetGradingMaterialsAsync(string examId, CancellationToken ct = default) =>
        SendAsync<GradingMaterialSnapshot>(HttpMethod.Get, $"api/v1/exams/{Uri.EscapeDataString(examId)}/grading-materials", null, ct);

    public Task<GradingStatusReport> GetGradingStatusAsync(string examId, CancellationToken cancellationToken = default) =>
        SendAsync<GradingStatusReport>(HttpMethod.Get,
            $"api/v1/grading/{Uri.EscapeDataString(examId)}/status", null, cancellationToken);
}
