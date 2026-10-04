using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

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

    /// <summary>
    /// 只改**答案与计分规则**：把本地材料包里的清单与完整答案提交给既有规则发布端点
    /// （不重新编译、不组包）。服务器会重新校验清单、答案与已发布试卷结构一致。
    /// <paramref name="expectedGradingVersion"/> 是**当前**规则版本，服务器据此判定并发冲突后发布下一版。
    /// </summary>
    public Task<ExamVersionSnapshot> ReviseGradingRulesAsync(string examId, string manifestJson, string answerKeyJson,
        long expectedGradingVersion, string reason, CancellationToken cancellationToken = default)
    {
        var ruleSetId = "rules-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson + answerKeyJson)))[..16].ToLowerInvariant();
        return SendAsync<ExamVersionSnapshot>(HttpMethod.Post,
            $"api/v1/versioning/exams/{Uri.EscapeDataString(examId)}/grading-publications", new
            {
                requestId = Guid.NewGuid().ToString("N"),
                expectedVersion = expectedGradingVersion,
                ruleSetId,
                validation = new { answersValidated = true, rulesValidated = true, recalculationReady = true },
                @operator = OperatorId,
                reason,
                manifestJson,
                answerKeyJson
            }, cancellationToken);
    }

    /// <summary>
    /// 读取计分状态：当前规则版本、完整答卷范围与计分完成情况。**每次调用都重新读取**（服务端不缓存），
    /// 用于把「发布成功 / 重算中 / 结果就绪」分开显示。
    /// </summary>
    public Task<GradingStatusReport> GetGradingStatusAsync(string examId, CancellationToken cancellationToken = default) =>
        SendAsync<GradingStatusReport>(HttpMethod.Get,
            $"api/v1/grading/{Uri.EscapeDataString(examId)}/status", null, cancellationToken);
}
