using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Student;
using Exam.Contracts.Submissions;

namespace Exam.Student.Core;

/// <summary>Authenticated HTTPS transport. TLS validation is never bypassed and redirects are disabled.</summary>
public sealed partial class HttpStudentApi : IStudentApi, IStudentPresenceApi, IDisposable
{
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _json = JsonDefaults.Create();
    private DeviceRecord? _device;
    private Uri? _server;
    private string? _trustedServerId;
    private long _serverEpoch;
    private readonly object _connectionGate = new();
    private long _connectionGeneration;
    private long _identityGeneration;
    private string? _nodeId;
    private readonly SemaphoreSlim _failoverGate = new(1, 1);
    public Action<DeviceRecord>? PersistTrustedConnection { get; set; }
    public event EventHandler? TrustedEndpointChanged;

    /// <summary>
    /// B07 部署指纹校验失败（或证书链不受信任）时抛出；继承 HttpRequestException，
    /// 沿用各页面既有的网络异常处理路径，激活页对其给出更明确的提示。
    /// </summary>
    public sealed class UntrustedServerException : HttpRequestException
    {
        public UntrustedServerException(string message, Exception inner) : base(message, inner) { }
    }

    public HttpStudentApi() : this(null) { }

    public HttpStudentApi(HttpMessageHandler? handler)
    {
        _client = new HttpClient(handler ?? CreateDefaultHandler())
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 4 * 1024 * 1024
        };
    }

    /// <summary>
    /// TLS 校验遵循 ServerTrust：部署配置提供证书指纹时精确 pin，否则保持 Windows 标准链校验。
    /// 回调在每次 TLS 握手时读取当前配置，应用启动完成前发出的请求同样受最终配置约束。
    /// </summary>
    private SocketsHttpHandler CreateDefaultHandler() => new()
    {
        AllowAutoRedirect = false,
        SslOptions = { RemoteCertificateValidationCallback = (sender, certificate, chain, errors) =>
            _device?.Topology is { } topology ? ServerTrust.ValidateNodeCertificate(topology, sender, certificate)
                : ServerTrust.ValidateCertificate(sender, certificate, chain, errors) }
    };

    public void ConfigureConnection(DeviceRecord device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(device.Host) ||
            Uri.CheckHostName(device.Host) == UriHostNameType.Unknown)
        {
            throw new ArgumentException("服务器地址或端口无效。");
        }

        var server = new UriBuilder(Uri.UriSchemeHttps, device.Host, device.Port).Uri;
        device.Topology?.Validate();
        if (device.Topology is { } topology && (topology.ServerId != device.ServerId ||
            !topology.Nodes.Any(node => node.Address == server) ||
            device.ActiveNodeId is not null && !topology.Nodes.Any(node => node.NodeId == device.ActiveNodeId)))
            throw new InvalidOperationException("主备逻辑身份与设备登记不一致。");
        lock (_connectionGate)
        {
            if (_device?.Host != device.Host || _device?.Port != device.Port ||
                _device?.DeviceId != device.DeviceId || _device?.Credential != device.Credential)
            {
                _identityGeneration++;
                _connectionGeneration++;
                _serverEpoch = device.ServerEpoch;
                _trustedServerId = device.ServerId;
                _server = device.Topology?.Nodes.SingleOrDefault(node => node.NodeId == device.ActiveNodeId)?.Address ?? server;
                _nodeId = device.Topology?.Nodes.SingleOrDefault(node => node.Address == _server)?.NodeId;
            }
            else
            {
                if (_trustedServerId is not null && device.ServerId is not null && _trustedServerId != device.ServerId)
                {
                    throw new InvalidOperationException("服务器身份与已验证连接不一致。");
                }
                if (_device?.Topology is { } previous && (device.Topology is null || !previous.Equivalent(device.Topology)))
                    throw new InvalidOperationException("主备信任配置不可由连接刷新替换。");
                _serverEpoch = Math.Max(_serverEpoch, device.ServerEpoch);
                _trustedServerId ??= device.ServerId;
                _nodeId ??= device.Topology?.Nodes.SingleOrDefault(node => node.Address == _server)?.NodeId;
            }

            _device = device with { ServerEpoch = _serverEpoch, ActiveNodeId = _nodeId ?? device.ActiveNodeId };
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

    public Task<ApiEnvelope<Exam.Contracts.Administration.DevicePresenceLease>> OpenPresenceAsync(
        Exam.Contracts.Administration.OpenDevicePresenceRequest request, CancellationToken ct = default) =>
        SendAsync<Exam.Contracts.Administration.DevicePresenceLease>(HttpMethod.Post, "/api/v1/student/presence/open", request, true, ct);

    public Task<ApiEnvelope<Exam.Contracts.Administration.DevicePresenceReceipt>> ReportPresenceAsync(
        Exam.Contracts.Administration.UpdateDevicePresenceRequest request, CancellationToken ct = default) =>
        SendAsync<Exam.Contracts.Administration.DevicePresenceReceipt>(HttpMethod.Post, "/api/v1/student/presence", request, true, ct);

    public Task<ApiEnvelope<PersistedProgress>> SyncProgressAsync(
        SyncProgressRequest request, CancellationToken ct = default) =>
        SendAsync<PersistedProgress>(HttpMethod.Put, "/api/v1/submissions/progress", request, true, ct);

    public Task<ApiEnvelope<SubmissionReceipt>> SubmitFinalAnswersAsync(
        SubmitFinalAnswersRequest request, CancellationToken ct = default) =>
        SendAsync<SubmissionReceipt>(HttpMethod.Post, "/api/v1/submissions/final", request, true, ct);

    public Task<ApiEnvelope<FocusEventsReceipt>> ReportFocusEventsAsync(
        ReportFocusEventsRequest request, CancellationToken ct = default) =>
        SendAsync<FocusEventsReceipt>(HttpMethod.Post, "/api/v1/submissions/focus-events", request, true, ct);

    public Task<ApiEnvelope<Exam.Contracts.Versioning.ExamVersionSnapshot>> ConfirmPaperReviewAsync(
        string examId, string participantId,
        Exam.Contracts.Versioning.ConfirmPaperReviewRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(examId) || string.IsNullOrWhiteSpace(participantId))
            throw new ArgumentException("确认试卷复核需要完整的考试与参试标识。");
        return SendAsync<Exam.Contracts.Versioning.ExamVersionSnapshot>(
            HttpMethod.Post,
            $"/api/v1/versioning/exams/{Uri.EscapeDataString(examId)}" +
            $"/participants/{Uri.EscapeDataString(participantId)}/paper-review",
            request, true, ct);
    }

    public Task<ApiEnvelope<SubmissionReceipt>> GetSubmissionReceiptAsync(
        string participationId, long entryVersion, string deviceId, long authorizationVersion,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(participationId) || string.IsNullOrWhiteSpace(deviceId) ||
            entryVersion < 1 || authorizationVersion < 1)
        {
            throw new ArgumentException("查询最终提交需要完整的参试与设备授权信息。");
        }

        var query = $"participationId={Uri.EscapeDataString(participationId)}" +
            $"&entryVersion={entryVersion}&deviceId={Uri.EscapeDataString(deviceId)}" +
            $"&authorizationVersion={authorizationVersion}";
        return SendAsync<SubmissionReceipt>(HttpMethod.Get, $"/api/v1/submissions/receipts?{query}", null, true, ct);
    }

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
            !string.IsNullOrEmpty(result.UserInfo) ||
            !string.IsNullOrEmpty(result.Fragment))
        {
            throw new InvalidOperationException("服务器返回了不可信的资源地址。");
        }
        if (result.GetLeftPart(UriPartial.Authority) != server.GetLeftPart(UriPartial.Authority))
        {
            // An old package descriptor may still name the prior trusted node after takeover.
            if (_device?.Topology?.Nodes.Any(node => node.Address.GetLeftPart(UriPartial.Authority) == result.GetLeftPart(UriPartial.Authority)) != true)
                throw new InvalidOperationException("服务器返回了不可信的资源地址。");
            result = new Uri(server, result.PathAndQuery);
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

    /// <summary>发送请求；TLS 层不受信任（指纹不匹配或链校验失败）时转成 UntrustedServerException。</summary>
    private async Task<HttpResponseMessage> SendTrustedAsync(HttpRequestMessage request, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        try
        {
            return await _client.SendAsync(request, completion, ct);
        }
        catch (HttpRequestException error) when (ContainsAuthenticationError(error))
        {
            throw new UntrustedServerException("服务器证书校验失败，当前节点不受信任。", error);
        }
    }

    private static bool ContainsAuthenticationError(Exception error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is System.Security.Authentication.AuthenticationException)
            {
                return true;
            }
        }
        return false;
    }

    private async Task<ApiEnvelope<T>> SendAsync<T>(
        HttpMethod method, string path, object? body, bool authenticated, CancellationToken ct)
    {
        // Freeze bytes once: retries must not invent request IDs or rebuild final answers.
        var bytes = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), _json);
        long identityGeneration;
        long generation;
        lock (_connectionGate) { identityGeneration = _identityGeneration; generation = _connectionGeneration; }
        ApiEnvelope<T> result;
        try
        {
            result = await SendOnceAsync<T>(method, path, bytes, authenticated, identityGeneration, ct);
        }
        catch (Exception error) when (error is HttpRequestException and not UntrustedServerException and not IdentityRejectedException ||
            error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            if (!await TryReconnectAsync(identityGeneration, generation, ct)) throw;
            return await SendOnceAsync<T>(method, path, bytes, authenticated, identityGeneration, ct);
        }
        if (result.Error?.Code is ErrorCodes.NotActiveServer or ErrorCodes.ServiceRecovering or ErrorCodes.StaleServerEpoch or ErrorCodes.ReplicaUnavailable &&
            await TryReconnectAsync(identityGeneration, generation, ct))
            return await SendOnceAsync<T>(method, path, bytes, authenticated, identityGeneration, ct);
        return result;
    }

    private async Task<ApiEnvelope<T>> SendOnceAsync<T>(
        HttpMethod method, string path, byte[]? body, bool authenticated, long identityGeneration, CancellationToken ct)
    {
        HttpRequestMessage message;
        long generation;
        lock (_connectionGate)
        {
            if (identityGeneration != _identityGeneration) throw new IdentityRejectedException("设备身份已切换，停止原请求。");
            message = CreateRequest(method, path, authenticated);
            generation = _connectionGeneration;
        }
        using var request = message;
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new("application/json");
        }
        using var response = await SendTrustedAsync(request, ct);
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
            ValidateIdentity(envelope.ServerId, envelope.NodeId, envelope.ServerEpoch);
            if (envelope.IsSuccess)
            {
                if (string.IsNullOrWhiteSpace(envelope.NodeId) || envelope.Result is null)
                {
                    throw new HttpRequestException("服务器没有返回有效业务结果。");
                }
                if (envelope.Result is ServerTimeSnapshot time &&
                    time.Server != new ServerNodeEnvelope(envelope.ServerId, envelope.NodeId, envelope.ServerEpoch))
                    throw new IdentityRejectedException("服务器时间与外层身份信息不一致。");
                ValidateServer(envelope.ServerId, envelope.ServerEpoch);
                _nodeId = envelope.NodeId;
            }
        }
        if (generation != _connectionGeneration) TrustedEndpointChanged?.Invoke(this, EventArgs.Empty);
        return envelope;
    }

    private void ValidateServer(string serverId, long epoch)
    {
        if (string.IsNullOrWhiteSpace(serverId) || epoch < 1 ||
            (_trustedServerId is not null && !string.Equals(_trustedServerId, serverId, StringComparison.Ordinal)) ||
            epoch < _serverEpoch)
        {
            throw new IdentityRejectedException("服务器身份或服务代次不匹配，已停止处理响应。");
        }
        if (epoch > _serverEpoch && _device?.Topology is not null)
        {
            var updated = _device with { ServerEpoch = epoch, ActiveNodeId = _nodeId };
            (PersistTrustedConnection ?? throw new InvalidOperationException("主备连接缺少受保护持久化入口。"))(updated);
            _device = updated;
            _connectionGeneration++;
        }
        _trustedServerId ??= serverId;
        _serverEpoch = epoch;
    }

    internal static T RequireResult<T>(ApiEnvelope<T> envelope) =>
        envelope.IsSuccess && envelope.Result is not null ? envelope.Result
            : throw new InvalidOperationException(envelope.Error?.Message ?? "服务器未确认操作结果。");

    public void Dispose() => _client.Dispose();
}
