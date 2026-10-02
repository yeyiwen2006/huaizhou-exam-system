using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Versioning;

namespace Exam.Infrastructure.Packaging;

/// <summary>无 WPF 依赖的考务发布适配器。HttpClient 必须禁用自动跳转，凭据由既有登录会话提供。</summary>
public sealed class HttpExamPublicationClient(HttpClient http, Action<HttpRequestMessage>? credentials = null) : IExamPublicationClient
{
    private readonly JsonSerializerOptions _json = JsonDefaults.Create();
    private string? _nodeId;
    private long _epoch;
    public long Generation => 0;
    public string? ServerId { get; private set; }

    public Task<ExamSummary[]> ListExamsAsync(CancellationToken ct = default) =>
        SendAsync<ExamSummary[]>(HttpMethod.Get, "api/v1/exams", null, ct);
    public Task<ExamVersionSnapshot> GetExamAsync(string examId, CancellationToken ct = default) =>
        SendAsync<ExamVersionSnapshot>(HttpMethod.Get, $"api/v1/versioning/exams/{Uri.EscapeDataString(examId)}", null, ct);
    public Task<ExamDetail> GetExamDetailAsync(string examId, CancellationToken ct = default) =>
        SendAsync<ExamDetail>(HttpMethod.Get, $"api/v1/exams/{Uri.EscapeDataString(examId)}", null, ct);
    public Task<PublicationRecipients> GetPublicationRecipientsAsync(CancellationToken ct = default) =>
        SendAsync<PublicationRecipients>(HttpMethod.Get, "api/v1/publications/recipients", null, ct);

    public async Task<ExamVersionSnapshot> UploadPublicationAsync(string examId, UploadExamBundleRequest upload,
        string encryptedPath, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(JsonSerializer.Serialize(upload, _json), Encoding.UTF8, "application/json"), "metadata");
        await using var stream = new FileStream(encryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        form.Add(new StreamContent(stream), "package", "package.enc");
        return await SendAsync<ExamVersionSnapshot>(HttpMethod.Post,
            $"api/v1/exams/{Uri.EscapeDataString(examId)}/publications/upload", form, ct);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, CancellationToken ct) where T : class
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        credentials?.Invoke(request);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        ApiEnvelope<T>? envelope;
        try
        {
            const int maximum = 16 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("响应超过允许大小。");
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var bytes = new byte[65536];
            while (true)
            {
                var count = await input.ReadAsync(bytes, ct);
                if (count == 0) break;
                if (buffer.Length + count > maximum) throw new InvalidDataException("响应超过允许大小。");
                await buffer.WriteAsync(bytes.AsMemory(0, count), ct);
            }
            envelope = JsonSerializer.Deserialize<ApiEnvelope<T>>(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)), _json);
        }
        catch (JsonException)
        {
            // Never echo proxy HTML, raw answer material or authentication headers.
            throw new ExamPublicationApiException("INVALID_RESPONSE", "服务器未返回有效业务回执，不能确认操作成功。",
                response.IsSuccessStatusCode ? HttpStatusCode.BadGateway : response.StatusCode);
        }
        if (envelope is null || string.IsNullOrWhiteSpace(envelope.ServerId) || string.IsNullOrWhiteSpace(envelope.NodeId) ||
            envelope.ServerEpoch < 1 || ServerId is not null &&
            (envelope.ServerId != ServerId || envelope.ServerEpoch < _epoch ||
             envelope.ServerEpoch == _epoch && envelope.NodeId != _nodeId))
            throw new ExamPublicationApiException("INVALID_SERVER_IDENTITY", "服务器身份或代次不一致，已停止操作。", HttpStatusCode.BadGateway);
        ServerId = envelope.ServerId;
        _nodeId = envelope.NodeId;
        _epoch = envelope.ServerEpoch;
        if (!response.IsSuccessStatusCode || envelope.Error is not null)
            throw new ExamPublicationApiException(envelope.Error?.Code ?? $"HTTP_{(int)response.StatusCode}",
                envelope.Error?.Message ?? "服务器没有确认本次操作完成。", response.StatusCode);
        return envelope.Result ?? throw new ExamPublicationApiException("EMPTY_RESPONSE",
            "服务器没有返回业务结果，不能确认操作成功。", HttpStatusCode.BadGateway);
    }
}

public sealed class ExamPublicationApiException(string code, string message, HttpStatusCode statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public HttpStatusCode StatusCode { get; } = statusCode;
}
