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
    private readonly PendingBusinessMutationStore _mutations;
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
        _identityGeneration++;
        _routeGeneration++;
        _serverId = null;
        _nodeId = null;
        _serverEpoch = 0;
        _nextRouteAttempt = 0;
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureCurrentConnection(long generation)
    {
        if (generation != Generation)
            throw new OperationCanceledException("连接已更换，已忽略旧连接的响应；写操作结果请在原服务器核对。");
    }

    private void EnsureCurrentRoute(long generation)
    {
        if (generation != _routeGeneration)
            throw new OperationCanceledException("业务连接已切换，沿原请求核对当前主机。");
    }

    private void EnsureCurrentIdentity(long identity)
    {
        if (identity != _identityGeneration)
            throw new OperationCanceledException("考务账号或服务器已更换，原操作不会发送到新会话。");
    }

    private void ValidateEnvelope<T>(ApiEnvelope<T> envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.ServerId) || string.IsNullOrWhiteSpace(envelope.NodeId) ||
            envelope.ServerEpoch < 1)
            throw new AdminApiException("INVALID_SERVER_IDENTITY", "响应缺少有效的服务器身份或代次。", HttpStatusCode.BadGateway);
        if (_serverId is not null &&
            (!StringComparer.Ordinal.Equals(_serverId, envelope.ServerId) || envelope.ServerEpoch < _serverEpoch ||
             (envelope.ServerEpoch == _serverEpoch && !StringComparer.Ordinal.Equals(_nodeId, envelope.NodeId)) ||
             _trustedRoutes is { } routes && routes.Nodes.SingleOrDefault(node => node.Address == _httpClient.BaseAddress)?.NodeId != envelope.NodeId))
            throw new AdminApiException("STALE_SERVER_IDENTITY", "响应来自不同服务器或过期节点代次，已拒绝使用。", HttpStatusCode.BadGateway);
        if (envelope is { IsSuccess: true, Result: not null } && _trustedRoutes is { } trusted &&
            envelope.ServerId == trusted.ServerId && envelope.ServerEpoch > trusted.Epoch &&
            trusted.Nodes.SingleOrDefault(node => node.Address == _httpClient.BaseAddress)?.NodeId == envelope.NodeId)
        {
            // A former primary can become active again at the same address while this
            // client is offline. Keep that confirmed epoch across application restarts.
            var confirmed = trusted with { Epoch = envelope.ServerEpoch, ActiveNodeId = envelope.NodeId };
            SaveRoutes(confirmed);
            _trustedRoutes = confirmed;
        }
        _serverId = envelope.ServerId;
        _nodeId = envelope.NodeId;
        _serverEpoch = envelope.ServerEpoch;
    }

    public AdminApiClient(HttpMessageHandler? messageHandler = null, string? pendingDirectory = null)
    {
        _messageHandler = messageHandler;
        _httpClient = CreateClient();
        var directory = pendingDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HuaizhouExam", "Admin", "BusinessRequests");
        _mutations = new(directory);
        _routingFile = Path.Combine(directory, "trusted-routes.bin");
    }

    public bool IsConfigured => _httpClient.BaseAddress is not null &&
                                _httpClient.DefaultRequestHeaders.Authorization is not null &&
                                !string.IsNullOrWhiteSpace(_operatorId);

    public string? ServerId => _serverId;
    public string OperatorId => _operatorId;

    public AdminSessionSnapshot? CaptureSession() => IsConfigured && _account is not null && _serverId is not null &&
        _httpClient.DefaultRequestHeaders.Authorization is { Scheme: "Bearer", Parameter: { Length: > 0 } credential }
        ? new(_httpClient.BaseAddress!, _serverId, _account.Username, credential, Generation, _nodeId, _serverEpoch) : null;

    public void Configure(string baseAddress, string operatorToken, string operatorId)
    {
        var normalized = NormalizeAddress(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);
        if (_trustedRoutes?.Nodes.Any(node => node.Address == normalized) != true) _trustedRoutes = null;
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
        var generation = _routeGeneration;
        var identity = _identityGeneration;
        var client = _httpClient;
        HttpResponseMessage received;
        try { received = await client.GetAsync("health/ready", cancellationToken); }
        catch (Exception error) when (CanReconnect(error) && !cancellationToken.IsCancellationRequested && identity == _identityGeneration)
        {
            if (!await TryReconnectBusinessAsync(generation, cancellationToken)) throw;
            EnsureCurrentIdentity(identity);
            received = await _httpClient.GetAsync("health/ready", cancellationToken); generation = _routeGeneration;
        }
        using var response = received;
        EnsureCurrentIdentity(identity);
        EnsureCurrentRoute(generation);
        if (!response.IsSuccessStatusCode)
        {
            if (await TryReconnectBusinessAsync(generation, cancellationToken))
            { EnsureCurrentIdentity(identity); await CheckConnectionAsync(cancellationToken); return; }
            throw new AdminApiException("SERVICE_NOT_READY", $"服务器就绪检查失败：HTTP {(int)response.StatusCode}。", response.StatusCode);
        }

        // 健康检查是公开接口；再读取当前登录账号，确认会话仍然有效。
        try
        {
            var account = await SendAsync<Exam.Contracts.Operators.OperatorAccount>(HttpMethod.Get, "api/v1/operators/me", null, cancellationToken);
            EnsureCurrentIdentity(identity);
            _account = account;
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
        HttpMethod method, string relativeUri, object? payload, CancellationToken cancellationToken, bool preserveRequestId = false)
        where T : class
    {
        var generation = _routeGeneration; var identity = _identityGeneration;
        // Caller-owned lists can change while a request is awaiting the network. Keep
        // the same business content as well as the journal's request ID during takeover.
        var frozenPayload = payload is null ? null : JsonSerializer.SerializeToNode(payload, _json)!.AsObject();
        try { return await SendCoreAsync<T>(method, relativeUri, frozenPayload, cancellationToken, preserveRequestId, identity); }
        catch (Exception error) when (CanReconnect(error) && !cancellationToken.IsCancellationRequested && identity == _identityGeneration)
        {
            if (!await TryReconnectBusinessAsync(generation, cancellationToken)) throw;
            // The mutation journal reuses the same request ID and exact business content.
            return await SendCoreAsync<T>(method, relativeUri, frozenPayload, cancellationToken, preserveRequestId, identity);
        }
    }

    private async Task<T> SendCoreAsync<T>(
        HttpMethod method,
        string relativeUri,
        JsonObject? payload,
        CancellationToken cancellationToken, bool preserveRequestId, long identity)
        where T : class
    {
        EnsureCurrentIdentity(identity);
        EnsureConfigured();
        if (payload is not null && !preserveRequestId && _serverId is null)
        {
            var account = await SendAsync<Exam.Contracts.Operators.OperatorAccount>(HttpMethod.Get,
                "api/v1/operators/me", null, cancellationToken);
            EnsureCurrentIdentity(identity);
            _account = account;
            if (!string.Equals(_account.Username, _operatorId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("当前登录账号与业务请求操作者不一致。");
        }
        EnsureCurrentIdentity(identity);
        var generation = _routeGeneration;
        var client = _httpClient;
        using var request = new HttpRequestMessage(method, relativeUri);
        PendingBusinessMutationStore.Mutation? mutation = null;
        if (payload is not null && preserveRequestId)
        {
            // Prepared grading revisions own a durable request ID. The generic mutation cache must
            // neither replace it nor generate a fresh one after a successful cross-process replay.
            var fixedBody = payload;
            if (fixedBody["requestId"]?.GetValue<string>() is not { Length: > 0 and <= 128 })
                throw new InvalidDataException("规则预览缺少有效的固定请求编号。");
            request.Content = JsonContent.Create(fixedBody, options: _json);
        }
        else if (payload is not null)
        {
            var body = payload.DeepClone().AsObject();
            // Keep the same requestId after a lost response/5xx. Only a definitive result
            // completes a mutation. Keys contain a digest, never the roster or token.
            body.Remove("requestId");
            mutation = _mutations.GetOrCreate(_serverId!, _operatorId.ToUpperInvariant(), _serverEpoch,
                method.Method, relativeUri, body.ToJsonString(_json));
            body["requestId"] = mutation.RequestId;
            request.Content = JsonContent.Create(body, options: _json);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        EnsureCurrentRoute(generation);
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
        EnsureCurrentRoute(generation);
        if (envelope is not null) ValidateEnvelope(envelope);
        if (!response.IsSuccessStatusCode || envelope?.Error is not null)
        {
            var error = envelope?.Error;
            // Only an official, non-retryable 4xx is a definitive rejection.
            if (mutation is not null && error is { Retryable: false } &&
                (int)response.StatusCode is >= 400 and < 500 &&
                response.StatusCode is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) &&
                error.Code is not (ErrorCodes.NotActiveServer or ErrorCodes.StaleServerEpoch or ErrorCodes.ServiceRecovering))
                _mutations.Complete(mutation);
            throw new AdminApiException(error?.Code ?? $"HTTP_{(int)response.StatusCode}",
                error?.Message ?? $"服务器请求失败：HTTP {(int)response.StatusCode}。", response.StatusCode);
        }

        var result = envelope?.Result ?? throw new AdminApiException("EMPTY_RESPONSE",
            "服务器返回成功状态，但未包含结果。", response.StatusCode);
        if (mutation is not null) _mutations.Complete(mutation);
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

    private HttpClient CreateClient() => CreateRoutedClient(_trustedRoutes);

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
