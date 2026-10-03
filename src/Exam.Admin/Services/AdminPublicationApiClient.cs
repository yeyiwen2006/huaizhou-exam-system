using System.Net;
using Exam.Infrastructure.Packaging;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Versioning;

namespace Exam.Admin.Services;

public sealed partial class AdminApiClient : IExamPublicationClient
{
    public Task<PublicationRecipients> GetPublicationRecipientsAsync(CancellationToken ct = default) =>
        SendAsync<PublicationRecipients>(HttpMethod.Get, "api/v1/publications/recipients", null, ct);

    /// <summary>
    /// 只上传密文（学生包与源材料归档），调用方在结果不确定时沿用同一请求与同一份密文重试。
    /// 归档里含独立答案，因此和包一样必须先加密：明文不得进入表单临时缓冲。
    /// </summary>
    public async Task<ExamVersionSnapshot> UploadPublicationAsync(string examId,
        UploadExamBundleRequest upload, string encryptedPath, string materialsPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        ArgumentNullException.ThrowIfNull(upload);
        EnsureConfigured();
        var generation = Generation;
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(upload, _json), Encoding.UTF8, "application/json"), "metadata");
        await using var stream = new FileStream(encryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        form.Add(new StreamContent(stream), "package", "package.enc");
        await using var materials = new FileStream(materialsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        form.Add(new StreamContent(materials), "materials", "materials.enc");
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"api/v1/exams/{Uri.EscapeDataString(examId)}/publications/upload") { Content = form };
        using var response = await _httpClient.SendAsync(request, ct);
        EnsureCurrentConnection(generation);
        ApiEnvelope<ExamVersionSnapshot>? envelope = null;
        try
        {
            if (response.Content.Headers.ContentLength != 0)
                envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<ExamVersionSnapshot>>(_json, ct);
        }
        catch (JsonException)
        {
            if (response.IsSuccessStatusCode)
                throw new AdminApiException("INVALID_RESPONSE", "服务器成功响应不是有效的业务封套。", response.StatusCode);
        }
        EnsureCurrentConnection(generation);
        if (envelope is not null) ValidateEnvelope(envelope);
        if (!response.IsSuccessStatusCode || envelope?.Error is not null)
            throw new AdminApiException(envelope?.Error?.Code ?? $"HTTP_{(int)response.StatusCode}",
                envelope?.Error?.Message ?? $"发布请求失败：HTTP {(int)response.StatusCode}。", response.StatusCode);
        return envelope?.Result ?? throw new AdminApiException("EMPTY_RESPONSE",
            "服务器没有返回已发布版本，请核对服务器状态；不得假定发布成功。", HttpStatusCode.BadGateway);
    }
}
