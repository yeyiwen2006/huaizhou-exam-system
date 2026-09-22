using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Student;

namespace Exam.Student.Core;

/// <summary>Authenticated HTTPS transport. TLS validation is never bypassed and redirects are disabled.</summary>
public sealed partial class HttpStudentApi : IStudentApi, IDisposable
{
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _json = JsonDefaults.Create();
    private DeviceRecord? _device;
    private Uri? _server;
    private string? _trustedServerId;
    private long _serverEpoch;
    private readonly object _connectionGate = new();
    private long _connectionGeneration;

    public HttpStudentApi() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }

    public HttpStudentApi(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 4 * 1024 * 1024
        };
    }

    public void ConfigureConnection(DeviceRecord device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(device.Host) ||
            Uri.CheckHostName(device.Host) == UriHostNameType.Unknown)
        {
            throw new ArgumentException("服务器地址或端口无效。");
        }

        var server = new UriBuilder(Uri.UriSchemeHttps, device.Host, device.Port).Uri;
        lock (_connectionGate)
        {
            if (_server != server || _device?.DeviceId != device.DeviceId || _device?.Credential != device.Credential)
            {
                _connectionGeneration++;
                _serverEpoch = device.ServerEpoch;
                _trustedServerId = device.ServerId;
            }
            else
            {
                if (_trustedServerId is not null && device.ServerId is not null && _trustedServerId != device.ServerId)
                {
                    throw new InvalidOperationException("服务器身份与已验证连接不一致。");
                }
                _serverEpoch = Math.Max(_serverEpoch, device.ServerEpoch);
                _trustedServerId ??= device.ServerId;
            }

            _device = device;
            _server = server;
        }
    }

    public Task<ApiEnvelope<ActivationRequestResult>> SubmitActivationAsync(
        SubmitActivationRequest request, CancellationToken ct = default) =>
        SendAsync<ActivationRequestResult>(HttpMethod.Post, "/api/v1/activation/requests", request, false, ct);

    public Task<ApiEnvelope<ActivationRequestResult>> GetActivationStatusAsync(
        GetActivationStatusRequest request, CancellationToken ct = default) =>
        SendAsync<ActivationRequestResult>(HttpMethod.Post, "/api/v1/activation/status", request, false, ct);

    public Task<ApiEnvelope<ActivationResult>> ReactivateAsync(
        ReactivateDeviceRequest request, CancellationToken ct = default) =>
        SendAsync<ActivationResult>(HttpMethod.Post, "/api/v1/activation/reactivate", request, false, ct);

    public Task<ApiEnvelope<ActivationResult>> CancelActivationAsync(
        CancelOwnActivationRequest request, CancellationToken ct = default) =>
        SendAsync<ActivationResult>(HttpMethod.Post, "/api/v1/activation/cancel-own", request, false, ct);

    public Task<ApiEnvelope<StudentSessionResult>> LoginAsync(
        LoginStudentRequest request, CancellationToken ct = default) =>
        SendAsync<StudentSessionResult>(HttpMethod.Post, "/api/v1/participation/login", request, false, ct);

    public Task<ApiEnvelope<EntryPreparationResult>> PrepareEntryAsync(
        PrepareExamEntryRequest request, CancellationToken ct = default) =>
        SendAsync<EntryPreparationResult>(HttpMethod.Post, "/api/v1/participation/prepare-entry", request, true, ct);

    public Task<ApiEnvelope<ServerTimeSnapshot>> GetServerTimeAsync(CancellationToken ct = default) =>
        SendAsync<ServerTimeSnapshot>(HttpMethod.Get, "/api/v1/commands/time", null, false, ct);

    public async Task<IReadOnlyList<ExamOverview>> GetExamOverviewsAsync(
        string sessionId, CancellationToken ct = default)
    {
        var envelope = await SendAsync<IReadOnlyList<StudentExamOverview>>(
            HttpMethod.Get, $"/api/v1/student/exams?sessionId={Uri.EscapeDataString(sessionId)}", null, true, ct);
        return RequireResult(envelope).Select(exam => new ExamOverview(
            exam.ExamId, exam.Name, exam.ScheduledStart, exam.ScheduledEnd,
            exam.Subjects, exam.Lifecycle, exam.CategoryName)).ToArray();
    }

    internal Uri ResolveSameServer(string path)
    {
        var server = _server ?? throw new InvalidOperationException("请先配置并验证服务器地址。");
        if (!Uri.TryCreate(server, path, out var result) || result.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(result.Host, server.Host, StringComparison.OrdinalIgnoreCase) ||
            result.Port != server.Port || !string.IsNullOrEmpty(result.UserInfo) ||
            !string.IsNullOrEmpty(result.Fragment))
        {
            throw new InvalidOperationException("服务器返回了不可信的资源地址。");
        }
        return result;
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, bool authenticated)
    {
        var request = new HttpRequestMessage(method, ResolveSameServer(path));
        if (authenticated)
        {
            var device = _device ?? throw new InvalidOperationException("设备身份尚未配置。");
            request.Headers.Add("X-Exam-Device-Id", device.DeviceId);
            request.Headers.Add("X-Exam-Device-Credential", device.Credential);
        }
        return request;
    }

    private async Task<ApiEnvelope<T>> SendAsync<T>(
        HttpMethod method, string path, object? body, bool authenticated, CancellationToken ct)
    {
        HttpRequestMessage message;
        long generation;
        lock (_connectionGate)
        {
            message = CreateRequest(method, path, authenticated);
            generation = _connectionGeneration;
        }
        using var request = message;
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: _json);
        }
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new HttpRequestException("服务器重定向被拒绝，请核对可信服务器地址。");
        }
        ApiEnvelope<T>? envelope;
        try { envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(_json, ct); }
        catch (JsonException) { throw new HttpRequestException("服务器响应格式无效，请联系考务人员。"); }
        if (envelope is null || (!response.IsSuccessStatusCode && envelope.Error is null))
        {
            throw new HttpRequestException("服务器未确认操作结果，请使用原请求重试。");
        }
        lock (_connectionGate)
        {
            if (generation != _connectionGeneration)
            {
                throw new HttpRequestException("连接已切换，已忽略旧连接的响应。");
            }
            if (envelope.IsSuccess)
            {
                ValidateServer(envelope.ServerId, envelope.ServerEpoch);
                if (string.IsNullOrWhiteSpace(envelope.NodeId) || envelope.Result is null)
                {
                    throw new HttpRequestException("服务器没有返回有效业务结果。");
                }
            }
        }
        return envelope;
    }

    private void ValidateServer(string serverId, long epoch)
    {
        if (string.IsNullOrWhiteSpace(serverId) || epoch < 1 ||
            (_trustedServerId is not null && !string.Equals(_trustedServerId, serverId, StringComparison.Ordinal)) ||
            epoch < _serverEpoch)
        {
            throw new HttpRequestException("服务器身份或服务代次不匹配，已停止处理响应。");
        }
        _trustedServerId ??= serverId;
        _serverEpoch = epoch;
    }

    internal static T RequireResult<T>(ApiEnvelope<T> envelope) =>
        envelope.IsSuccess && envelope.Result is not null ? envelope.Result
            : throw new InvalidOperationException(envelope.Error?.Message ?? "服务器未确认操作结果。");

    public void Dispose() => _client.Dispose();
}
