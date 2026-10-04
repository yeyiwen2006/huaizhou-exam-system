using System.Net.Http.Json;
using Exam.Contracts.Grading;
using Exam.Contracts.Operators;
using Exam.Contracts.Versioning;

namespace Exam.Infrastructure.Packaging;

public sealed partial class HttpExamPublicationClient : IGradingRevisionClient
{
    public string? GradingNodeId => _nodeId;
    public long GradingServerEpoch => _epoch;
    public async Task<string> GetGradingOperatorAsync(CancellationToken ct = default)
    {
        var account = await SendAsync<OperatorAccount>(HttpMethod.Get, "api/v1/operators/me", null, ct);
        if (string.IsNullOrWhiteSpace(account.Username)) throw new InvalidDataException("当前考务身份无效。");
        return account.Username;
    }

    public Task<ExamVersionSnapshot> SendGradingRevisionAsync(PublishGradingRulesRequest request, CancellationToken ct = default) =>
        SendAsync<ExamVersionSnapshot>(HttpMethod.Post,
            $"api/v1/versioning/exams/{Uri.EscapeDataString(request.ExamId)}/grading-publications",
            JsonContent.Create(request, options: _json), ct);

    public Task<GradingMaterialSnapshot> GetGradingMaterialsAsync(string examId, CancellationToken ct = default) =>
        SendAsync<GradingMaterialSnapshot>(HttpMethod.Get, $"api/v1/exams/{Uri.EscapeDataString(examId)}/grading-materials", null, ct);

    public Task<GradingStatusReport> GetGradingStatusAsync(string examId, CancellationToken ct = default) =>
        SendAsync<GradingStatusReport>(HttpMethod.Get, $"api/v1/grading/{Uri.EscapeDataString(examId)}/status", null, ct);
}
