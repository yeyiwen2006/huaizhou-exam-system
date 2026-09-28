using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;

namespace Exam.Admin.Services;

public sealed class AdminApiClient : IDisposable
{
    private readonly HttpMessageHandler? _messageHandler;
    private HttpClient _httpClient;
    private readonly JsonSerializerOptions _json = JsonDefaults.Create();
    private string _operatorId = string.Empty;
    private long _generation;
    private readonly Dictionary<string, string> _pendingMutations = new(StringComparer.Ordinal);
    private string? _serverId;
    private string? _nodeId;
    private long _serverEpoch;

    public long Generation => _generation;
    public event EventHandler? ConfigurationChanged;

    public void Disconnect()
    {
        var previous = _httpClient;
        _httpClient = CreateClient();
        _operatorId = string.Empty;
        ResetConnectionState();
        previous.Dispose();
    }

    private void ResetConnectionState()
    {
        _generation++;
        _serverId = null;
        _nodeId = null;
        _serverEpoch = 0;
        _pendingMutations.Clear();
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureCurrentConnection(long generation)
    {
        if (generation != Generation)
            throw new OperationCanceledException("连接已更换，已忽略旧连接的响应；写操作结果请在原服务器核对。");
    }

    private void ValidateEnvelope<T>(ApiEnvelope<T> envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.ServerId) || string.IsNullOrWhiteSpace(envelope.NodeId) ||
            envelope.ServerEpoch < 1)
            throw new AdminApiException("INVALID_SERVER_IDENTITY", "响应缺少有效的服务器身份或代次。", HttpStatusCode.BadGateway);
        if (_serverId is not null &&
            (!StringComparer.Ordinal.Equals(_serverId, envelope.ServerId) || envelope.ServerEpoch < _serverEpoch ||
             (envelope.ServerEpoch == _serverEpoch && !StringComparer.Ordinal.Equals(_nodeId, envelope.NodeId))))
            throw new AdminApiException("STALE_SERVER_IDENTITY", "响应来自不同服务器或过期节点代次，已拒绝使用。", HttpStatusCode.BadGateway);
        _serverId = envelope.ServerId;
        _nodeId = envelope.NodeId;
        _serverEpoch = envelope.ServerEpoch;
    }

    public AdminApiClient(HttpMessageHandler? messageHandler = null)
    {
        _messageHandler = messageHandler;
        _httpClient = CreateClient();
    }

    public bool IsConfigured => _httpClient.BaseAddress is not null &&
                                _httpClient.DefaultRequestHeaders.Authorization is not null &&
                                !string.IsNullOrWhiteSpace(_operatorId);

    public string? ServerId => _serverId;
    public string OperatorId => _operatorId;

    public void Configure(string baseAddress, string operatorToken, string operatorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseAddress);
        if (!Uri.TryCreate(baseAddress.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new ArgumentException("服务器地址必须使用 HTTPS（仅本机测试允许 HTTP），且不能含用户信息、查询参数或片段。", nameof(baseAddress));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(operatorToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);

        var normalized = uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(uri.AbsoluteUri + '/', UriKind.Absolute);
        var configuredClient = CreateClient();
        configuredClient.BaseAddress = normalized;
        configuredClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        var previousClient = Interlocked.Exchange(ref _httpClient, configuredClient);
        previousClient.Dispose();
        _operatorId = operatorId.Trim();
        ResetConnectionState();
    }

    public async Task CheckConnectionAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        var generation = Generation;
        var client = _httpClient;
        using var response = await client.GetAsync("health/ready", cancellationToken);
        EnsureCurrentConnection(generation);
        if (!response.IsSuccessStatusCode)
        {
            throw new AdminApiException("SERVICE_NOT_READY", $"服务器就绪检查失败：HTTP {(int)response.StatusCode}。", response.StatusCode);
        }

        // The health endpoint is public. A missing exam on the operator-only route must carry
        // the official error envelope; a proxy's generic 404 does not prove authentication.
        using var authResponse = await client.GetAsync(
            $"api/v1/versioning/exams/{Guid.NewGuid():N}", cancellationToken);
        ApiEnvelope<JsonElement>? authEnvelope = null;
        if (authResponse.StatusCode == HttpStatusCode.NotFound)
        {
            try
            {
                authEnvelope = await authResponse.Content.ReadFromJsonAsync<ApiEnvelope<JsonElement>>(
                    _json, cancellationToken);
            }
            catch (JsonException)
            {
                // An intermediary may return an HTML/plain-text 404 page.
            }
        }

        EnsureCurrentConnection(generation);
        if (authResponse.StatusCode != HttpStatusCode.NotFound ||
            authEnvelope?.Error?.Code != "EXAM_NOT_FOUND")
        {
            throw new AdminApiException(
                "OPERATOR_AUTH_FAILED",
                $"考务身份验证失败：HTTP {(int)authResponse.StatusCode}。",
                authResponse.StatusCode);
        }
        ValidateEnvelope(authEnvelope!);
    }

    /// <summary>Null expectedVersion means create-only; updates use the version the operator loaded.
    /// Never refresh the expected version during a save, which would overwrite concurrent edits.</summary>
    public async Task<ExamVersionSnapshot> SaveScheduleAsync(
        string examId,
        ScheduleWindow schedule,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        if (expectedVersion is < 0) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        var requestId = Guid.NewGuid().ToString("N");
        return expectedVersion is null
            ? await SendAsync<ExamVersionSnapshot>(HttpMethod.Post, "api/v1/versioning/exams",
                new { requestId, examId, schedule }, cancellationToken)
            : await SendAsync<ExamVersionSnapshot>(HttpMethod.Put,
                $"api/v1/versioning/exams/{Uri.EscapeDataString(examId)}/schedule",
                new { requestId, expectedVersion, schedule }, cancellationToken);
    }

    public async Task<ExamVersionSnapshot> GetExamAsync(
        string examId,
        CancellationToken cancellationToken = default) =>
        await SendAsync<ExamVersionSnapshot>(
            HttpMethod.Get,
            $"api/v1/versioning/exams/{Uri.EscapeDataString(examId)}",
            null,
            cancellationToken);

    public async Task<ActivationResult> ApproveActivationAsync(
        string activationRequestId,
        string? roomGroupId,
        CancellationToken cancellationToken = default) =>
        await SendAsync<ActivationResult>(
            HttpMethod.Post,
            "api/v1/activation/approve",
            new ApproveActivationRequest(Guid.NewGuid().ToString("N"), activationRequestId, Normalize(roomGroupId)),
            cancellationToken);

    public async Task<ActivationResult> FindDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default) =>
        await SendAsync<ActivationResult>(
            HttpMethod.Get,
            $"api/v1/activation/devices/{Uri.EscapeDataString(deviceId)}",
            null,
            cancellationToken);

    public async Task<IReadOnlyList<ParticipationResult>> ListActualParticipationsAsync(
        string examId,
        CancellationToken cancellationToken = default) =>
        await SendAsync<IReadOnlyList<ParticipationResult>>(
            HttpMethod.Get,
            $"api/v1/participation/exams/{Uri.EscapeDataString(examId)}/actual",
            null,
            cancellationToken);

    public async Task<IReadOnlyList<CollectionCompletedNotification>> ListCollectionEventsAsync(
        string examId,
        long afterRosterVersion,
        CancellationToken cancellationToken = default) =>
        await SendAsync<IReadOnlyList<CollectionCompletedNotification>>(
            HttpMethod.Get,
            $"api/v1/submissions/collection/events?examId={Uri.EscapeDataString(examId)}&afterRosterVersion={afterRosterVersion}",
            null,
            cancellationToken);

    public async Task<DeviceDecisionResult> DecideDeviceAsync(
        string participationId,
        string selectedDeviceId,
        long expectedVersion,
        string reason,
        CancellationToken cancellationToken = default) =>
        await SendAsync<DeviceDecisionResult>(
            HttpMethod.Post,
            "api/v1/participation/device-decisions",
            new DeviceDecisionRequest(
                Guid.NewGuid().ToString("N"),
                participationId,
                selectedDeviceId,
                expectedVersion,
                _operatorId,
                reason),
            cancellationToken);

    public async Task<CommandEventView> ResendCommandAsync(
        string eventId,
        string participationId,
        long entryVersion,
        string deviceId,
        long authorizationVersion,
        CancellationToken cancellationToken = default) =>
        await SendAsync<CommandEventView>(
            HttpMethod.Post,
            $"api/v1/commands/{Uri.EscapeDataString(eventId)}/resend",
            new
            {
                requestId = Guid.NewGuid().ToString("N"),
                participationId,
                entryVersion,
                deviceId,
                authorizationVersion
            },
            cancellationToken);

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string relativeUri,
        object? payload,
        CancellationToken cancellationToken)
        where T : class
    {
        EnsureConfigured();
        var generation = Generation;
        var client = _httpClient;
        using var request = new HttpRequestMessage(method, relativeUri);
        string? mutationKey = null;
        if (payload is not null)
        {
            var body = JsonSerializer.SerializeToNode(payload, _json)!.AsObject();
            // Keep the same requestId after a lost response/5xx. Only a definitive result
            // completes a mutation. Keys contain a digest, never the roster or token.
            body.Remove("requestId");
            mutationKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                method.Method + " " + relativeUri + " " + body.ToJsonString(_json))));
            if (!_pendingMutations.TryGetValue(mutationKey, out var requestId))
            {
                if (_pendingMutations.Count >= 128)
                    throw new InvalidOperationException("未确认的写操作过多，请先核对原服务器结果，不再发送新的写请求。");
                requestId = Guid.NewGuid().ToString("N");
                _pendingMutations.Add(mutationKey, requestId);
            }
            body["requestId"] = requestId;
            request.Content = JsonContent.Create(body, options: _json);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        EnsureCurrentConnection(generation);
        ApiEnvelope<T>? envelope = null;
        try
        {
            if (response.Content.Headers.ContentLength != 0)
                envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(_json, cancellationToken);
        }
        catch (JsonException)
        {
            // Do not show or log a proxy's HTML/error body, which can contain sensitive values.
            if (response.IsSuccessStatusCode)
                throw new AdminApiException("INVALID_RESPONSE", "服务器成功响应不是有效的业务封套。", response.StatusCode);
        }
        EnsureCurrentConnection(generation);
        if (envelope is not null) ValidateEnvelope(envelope);
        if (!response.IsSuccessStatusCode || envelope?.Error is not null)
        {
            var error = envelope?.Error;
            // Only an official, non-retryable 4xx is a definitive rejection.
            if (mutationKey is not null && error is { Retryable: false } &&
                (int)response.StatusCode is >= 400 and < 500 &&
                response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests))
                _pendingMutations.Remove(mutationKey);
            throw new AdminApiException(error?.Code ?? $"HTTP_{(int)response.StatusCode}",
                error?.Message ?? $"服务器请求失败：HTTP {(int)response.StatusCode}。", response.StatusCode);
        }

        var result = envelope?.Result ?? throw new AdminApiException("EMPTY_RESPONSE",
            "服务器返回成功状态，但未包含结果。", response.StatusCode);
        if (mutationKey is not null) _pendingMutations.Remove(mutationKey);
        return result;
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("请先填写服务器地址、考务身份和令牌并连接服务器。");
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private HttpClient CreateClient() => _messageHandler is null
        ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        : new HttpClient(_messageHandler, disposeHandler: false);

    public void Dispose()
    {
        _httpClient.Dispose();
        _messageHandler?.Dispose();
    }
}

public sealed class AdminApiException(string code, string message, HttpStatusCode statusCode)
    : Exception(message)
{
    public string Code { get; } = code;
    public HttpStatusCode StatusCode { get; } = statusCode;
}
