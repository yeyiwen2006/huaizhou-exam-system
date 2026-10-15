using System.IO;
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

public sealed partial class AdminApiClient : IDisposable
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
        _account = null;
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

    public AdminSessionSnapshot? CaptureSession() => IsConfigured && _account is not null && _serverId is not null &&
        _httpClient.DefaultRequestHeaders.Authorization is { Scheme: "Bearer", Parameter: { Length: > 0 } credential }
        ? new(_httpClient.BaseAddress!, _serverId, _account.Username, credential, Generation) : null;

    public void Configure(string baseAddress, string operatorToken, string operatorId)
    {
        var normalized = NormalizeAddress(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);
        var configuredClient = CreateClient();
        configuredClient.BaseAddress = normalized;
        configuredClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        var previousClient = Interlocked.Exchange(ref _httpClient, configuredClient);
        previousClient.Dispose();
        _operatorId = operatorId.Trim();
        _account = null;
        ResetConnectionState();
    }

    private static Uri NormalizeAddress(string baseAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseAddress);
        if (!Uri.TryCreate(baseAddress.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port is < 1 or > 65535 || uri.AbsolutePath != "/" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
        {
            throw new ArgumentException("服务器地址必须是 HTTPS 根地址，且不能含用户信息、查询参数或片段。", nameof(baseAddress));
        }
        return uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? uri : new Uri(uri.AbsoluteUri + '/', UriKind.Absolute);
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

        // 健康检查是公开接口；再读取当前登录账号，确认会话仍然有效。
        try
        {
            _account = await SendAsync<Exam.Contracts.Operators.OperatorAccount>(HttpMethod.Get, "api/v1/operators/me", null, cancellationToken);
        }
        catch (AdminApiException error) when (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            // 代理返回的 404 或登录失效都不能证明当前会话有效。
            throw new AdminApiException("OPERATOR_AUTH_FAILED", "考务账号登录已失效，请重新登录。", error.StatusCode);
        }
    }

    /// <summary>Updates only the schedule version loaded by the operator. Exam creation uses the catalog.</summary>
    public async Task<ExamVersionSnapshot> SaveScheduleAsync(
        string examId,
        ScheduleWindow schedule,
        long? expectedVersion = null,
        CancellationToken cancellationToken = default,
        string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        if (expectedVersion is null)
            throw new InvalidOperationException("请先新建考试并读取已保存的时间，再修改考试时间。");
        if (expectedVersion < 1) throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        if (reason is { Length: > 1000 })
            throw new InvalidOperationException("修改时间备注不能超过1000字。");
        return await SendAsync<ExamVersionSnapshot>(HttpMethod.Put,
            $"api/v1/versioning/exams/{Uri.EscapeDataString(examId)}/schedule",
            new { requestId = Guid.NewGuid().ToString("N"), expectedVersion, schedule, reason = reason?.Trim() ?? string.Empty }, cancellationToken);
    }

    public async Task<ExamVersionSnapshot> GetExamAsync(
        string examId,
        CancellationToken cancellationToken = default) =>
        await SendAsync<ExamVersionSnapshot>(
            HttpMethod.Get,
            $"api/v1/versioning/exams/{Uri.EscapeDataString(examId)}",
            null,
            cancellationToken);

    public async Task<IReadOnlyList<ActivationRequestResult>> ListPendingActivationRequestsAsync(
        CancellationToken cancellationToken = default) =>
        await SendAsync<IReadOnlyList<ActivationRequestResult>>(
            HttpMethod.Get,
            "api/v1/activation/pending",
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

    /// <summary>失焦监测：按考生汇总本场考试的失焦次数与累计时长。</summary>
    public async Task<IReadOnlyList<Exam.Contracts.Submissions.FocusLossEventSummary>> ListFocusSummariesAsync(
        string examId,
        CancellationToken cancellationToken = default) =>
        await SendAsync<IReadOnlyList<Exam.Contracts.Submissions.FocusLossEventSummary>>(
            HttpMethod.Get,
            $"api/v1/submissions/focus-events?examId={Uri.EscapeDataString(examId)}",
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
        CancellationToken cancellationToken, bool preserveRequestId = false)
        where T : class
    {
        EnsureConfigured();
        var generation = Generation;
        var client = _httpClient;
        using var request = new HttpRequestMessage(method, relativeUri);
        string? mutationKey = null;
        if (payload is not null && preserveRequestId)
        {
            // Prepared grading revisions own a durable request ID. The generic mutation cache must
            // neither replace it nor generate a fresh one after a successful cross-process replay.
            var fixedBody = JsonSerializer.SerializeToNode(payload, _json)!.AsObject();
            if (fixedBody["requestId"]?.GetValue<string>() is not { Length: > 0 and <= 128 })
                throw new InvalidDataException("规则预览缺少有效的固定请求编号。");
            request.Content = JsonContent.Create(fixedBody, options: _json);
        }
        else if (payload is not null)
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
            throw new InvalidOperationException("请先登录考务账号。");
        }
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private HttpClient CreateClient() => _messageHandler is null
        ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, Proxy = new RadminDirectProxy(HttpClient.DefaultProxy) })
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
