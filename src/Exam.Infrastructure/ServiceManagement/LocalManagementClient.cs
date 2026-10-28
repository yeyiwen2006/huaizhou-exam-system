using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Contracts.HighAvailability;

namespace Exam.Infrastructure.ServiceManagement;

public sealed record ManagementClientResponse<T>(HttpStatusCode StatusCode, ManagementEnvelope<T> Envelope);

/// <summary>Independent management transport shared by GUI and CLI; never probes business readiness.</summary>
public sealed class LocalManagementClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _serverId;
    private readonly string _nodeId;
    private readonly Func<string?> _token;
    private readonly TimeSpan _requestTimeout;
    private readonly JsonSerializerOptions _json = JsonDefaults.Create();

    public LocalManagementClient(Uri endpoint, string serverId, string nodeId, Func<string?> token,
        HttpMessageHandler? transport = null, TimeSpan? requestTimeout = null)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps ||
            endpoint.Host.Length == 0 || endpoint.Port is < 1 or > 65535 ||
            endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0 ||
            string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(nodeId))
            throw new ArgumentException("管理连接必须是独立的服务器 HTTPS 根地址并指定预期服务器和节点。");
        _serverId = serverId;
        _nodeId = nodeId;
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _requestTimeout = requestTimeout ?? ManagementTimeouts.Write;
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        // Injected transports are for trusted composition/tests. Production uses normal certificate validation.
        _http = new HttpClient(transport ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
        { BaseAddress = endpoint, Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<ManagementClientResponse<ManagementStatus>> GetStatusAsync(CancellationToken token = default) =>
        SendAsync<ManagementStatus>(HttpMethod.Get, "service/status", null, token);

    public Task<ManagementClientResponse<ManagementOperation>> CancelOperationAsync(string requestId, CancellationToken token = default) =>
        SendAsync<ManagementOperation>(HttpMethod.Post, "operations/" + Uri.EscapeDataString(requestId) + "/cancel", "{}", token);
    public Task<ManagementClientResponse<ManagementBusinessSession>> ExchangeBusinessSessionAsync(ManagementBusinessSessionRequest request, CancellationToken token = default) =>
        SendAsync<ManagementBusinessSession>(HttpMethod.Post, "business-session", JsonSerializer.Serialize(request, _json), token);

    public async Task<ManagementClientResponse<HighAvailabilityStatus>> GetLocalHighAvailabilityAsync(CancellationToken token = default)
    {
        var response = await SendAsync<HighAvailabilityStatus>(HttpMethod.Get, "high-availability/local-status", null, token);
        RequireHighAvailability(response.Envelope.Result);
        return response;
    }

    public async Task<ManagementClientResponse<HighAvailabilityStatus>> GetHighAvailabilityAsync(CancellationToken token = default)
    {
        var response = await SendAsync<HighAvailabilityStatus>(HttpMethod.Get, "high-availability/status", null, token);
        RequireHighAvailability(response.Envelope.Result);
        return response;
    }

    public Task<ManagementClientResponse<ServiceConfiguration>> GetConfigurationAsync(CancellationToken token = default) =>
        SendAsync<ServiceConfiguration>(HttpMethod.Get, "service/configuration", null, token);

    public Task<ManagementClientResponse<BackupInfo>> GetBackupInfoAsync(string sourceDirectory, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory)) throw new ArgumentException("请填写服务器上的备份来源目录。");
        return SendAsync<BackupInfo>(HttpMethod.Get, "service/backup-info?sourceDirectory=" + Uri.EscapeDataString(sourceDirectory), null, token);
    }

    public Task<ManagementClientResponse<IReadOnlyList<ManagementAuditEntry>>> GetLogsAsync(int limit,
        CancellationToken token = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return SendAsync<IReadOnlyList<ManagementAuditEntry>>(HttpMethod.Get,
            "service/logs?limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture), null, token);
    }

    public Task<ManagementClientResponse<IReadOnlyList<ProtectedRuntimeLogEntry>>> GetRuntimeLogsAsync(int limit,
        CancellationToken token = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return SendAsync<IReadOnlyList<ProtectedRuntimeLogEntry>>(HttpMethod.Get,
            "service/runtime-logs?limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture), null, token);
    }

    public async Task<ManagementClientResponse<ManagementOperation>> GetOperationAsync(string requestId, CancellationToken token = default)
    {
        RequireRequestId(requestId);
        var response = await SendAsync<ManagementOperation>(HttpMethod.Get, "operations/" + Uri.EscapeDataString(requestId), null, token);
        RequireOperationBinding(response.Envelope.Result, requestId, null);
        return response;
    }

    public async Task<ManagementClientResponse<ManagementOperation>> ExecuteAsync(ManagementOperationKind kind, string requestJson,
        CancellationToken token = default)
    {
        if (Encoding.UTF8.GetByteCount(requestJson) > 65536) throw new ArgumentException("管理请求超过大小限制。");
        using var document = JsonDocument.Parse(requestJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String)
            throw new ArgumentException("管理变更必须携带 requestId。");
        var requestId = id.GetString()!;
        RequireRequestId(requestId);
        var route = kind switch
        {
            ManagementOperationKind.Start => "start", ManagementOperationKind.Stop => "stop",
            ManagementOperationKind.Checks => "checks", ManagementOperationKind.Configure => "configuration",
            ManagementOperationKind.Backup => "backups", ManagementOperationKind.Restore => "restores",
            ManagementOperationKind.Takeover => "takeover", ManagementOperationKind.Emergency => "emergency",
            ManagementOperationKind.Rebuild => "rebuild", ManagementOperationKind.Reprotect => "reprotect",
            _ => throw new ArgumentException("管理操作无效。")
        };
        var response = await SendAsync<ManagementOperation>(kind == ManagementOperationKind.Configure ? HttpMethod.Put : HttpMethod.Post,
            (kind is ManagementOperationKind.Takeover or ManagementOperationKind.Emergency or ManagementOperationKind.Rebuild or ManagementOperationKind.Reprotect
                ? "high-availability/" : "service/") + route, requestJson, token);
        RequireOperationBinding(response.Envelope.Result, requestId, kind);
        return response;
    }

    private async Task<ManagementClientResponse<T>> SendAsync<T>(HttpMethod method, string path, string? body, CancellationToken token)
    {
        // ResponseHeadersRead only bounds headers with HttpClient.Timeout. Keep one deadline through body reading.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_requestTimeout);
        try { return await SendCoreAsync<T>(method, path, body, deadline.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new HttpRequestException("管理响应超时，须按原 requestId 核对变更结果。"); }
        catch (IOException)
        { throw new HttpRequestException("管理响应读取中断，须按原 requestId 核对变更结果。"); }
    }

    private async Task<ManagementClientResponse<T>> SendCoreAsync<T>(HttpMethod method, string path, string? body, CancellationToken token)
    {
        var credential = _token();
        if (string.IsNullOrWhiteSpace(credential)) throw new UnauthorizedAccessException("缺少受保护的管理凭据。");
        using var request = new HttpRequestMessage(method, "/management/v1/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.Add(ManagementHeaders.ServerId, _serverId);
        request.Headers.Add(ManagementHeaders.NodeId, _nodeId);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        // Never follow redirects or accept an HTML/login page as a successful management result.
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new HttpRequestException("管理连接返回重定向，拒绝继续发送凭据。");
        if (response.Content.Headers.ContentLength > 1024 * 1024)
            throw new HttpRequestException("管理响应超过大小限制。");
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + read > 1024 * 1024) throw new HttpRequestException("管理响应超过大小限制。");
            memory.Write(buffer, 0, read);
        }
        ManagementEnvelope<T>? envelope;
        try { envelope = JsonSerializer.Deserialize<ManagementEnvelope<T>>(memory.GetBuffer().AsSpan(0, (int)memory.Length), _json); }
        catch (JsonException) { throw new HttpRequestException("管理响应格式无效。"); }
        if (envelope is null || envelope.ServerId != _serverId || envelope.NodeId != _nodeId ||
            string.IsNullOrWhiteSpace(envelope.CorrelationId) ||
            (envelope.EpochVerified ? envelope.ServerEpoch is null or <= 0 || envelope.EpochVerifiedAt is null :
                envelope.ServerEpoch is not null || envelope.EpochVerifiedAt is not null) ||
            (response.IsSuccessStatusCode && envelope.Result is null && envelope.Error is null) ||
            (!response.IsSuccessStatusCode && envelope.Error is null))
            throw new HttpRequestException("管理响应身份、代次或结果无效。");
        return new(response.StatusCode, envelope);
    }

    private void RequireOperationBinding(ManagementOperation? operation, string requestId, ManagementOperationKind? kind)
    {
        if (operation is not null && (operation.RequestId != requestId || operation.ServerId != _serverId ||
            operation.NodeId != _nodeId || string.IsNullOrWhiteSpace(operation.ActorId) || kind is not null && operation.Kind != kind))
            throw new HttpRequestException("管理结果与原请求绑定不一致。");
        if (operation is null) return;
        RequireHighAvailability(operation.HighAvailability);
        var completed = operation.FailoverCompletedState;
        if (completed is not null && (operation.Phase != ManagementOperationPhase.Completed ||
            operation.Kind is not (ManagementOperationKind.Takeover or ManagementOperationKind.Emergency or ManagementOperationKind.Rebuild or ManagementOperationKind.Reprotect) ||
            operation.HighAvailability?.State is not { } current || completed.ServerId != _serverId ||
            !FailoverStateHistory.Covers(current, completed) ||
            !completed.Audit.Any(entry => entry.Action == $"F02:{operation.Kind}:{requestId}" && entry.ActorId == operation.ActorId &&
                entry.ServerEpoch == completed.ServerEpoch)))
            throw new HttpRequestException("历史主备完成证据与当前权威或原请求不一致。");
        if (!Enum.IsDefined(operation.Kind) || !Enum.IsDefined(operation.Phase) ||
            operation.FailoverWorkInProgress && (operation.Kind is not (ManagementOperationKind.Takeover or ManagementOperationKind.Rebuild or
                ManagementOperationKind.Emergency or ManagementOperationKind.Reprotect) ||
                operation.Phase is not (ManagementOperationPhase.Accepted or ManagementOperationPhase.NeedsVerification)) ||
            operation.Phase == ManagementOperationPhase.Staged &&
                (operation.Kind != ManagementOperationKind.Restore || operation.Restore is not
                    { IsolationStaged: true, BusinessReady: false }) ||
            operation.Phase == ManagementOperationPhase.Completed && (operation.Error is not null || !(operation.Kind switch
            {
                ManagementOperationKind.Start => operation.Status is { ProcessStatus: BusinessServiceStatus.Running } started &&
                    (started.Readiness == BusinessReadiness.Ready || started.Readiness == BusinessReadiness.NotReady &&
                        started.Checks.Any(check => check.Name == "database-standby" && check.Passed) &&
                        started.Checks.Any(check => check.Name == "standby-listener" && check.Passed) &&
                        started.Checks.All(check => check.Passed || !check.BlocksWrites)),
                ManagementOperationKind.Stop => operation.Status is { ProcessStatus: BusinessServiceStatus.Stopped },
                ManagementOperationKind.Configure => operation.Configuration is { Version: > 0 },
                ManagementOperationKind.Checks => operation.Checks is { Count: > 0 },
                ManagementOperationKind.Backup => operation.Backup is { IncludesDatabase: true, IncludesProtectedConfiguration: true, IncludesVersionedResources: true },
                ManagementOperationKind.Restore => operation.Restore is { IsolationStaged: true, RecoveryVerified: true, BusinessReady: true },
                ManagementOperationKind.Takeover or ManagementOperationKind.Emergency or ManagementOperationKind.Rebuild =>
                    operation.HighAvailability is { State: not null, VerifiedAt: not null } ha &&
                    (operation.Kind != ManagementOperationKind.Rebuild
                        ? (completed ?? ha.State).ActiveNodeId == _nodeId
                        : (completed ?? ha.State).Nodes.TryGetValue(_nodeId, out var rebuilt) && rebuilt.Role == DeploymentNodeRole.Standby &&
                          (completed is not null || rebuilt.DatabaseInRecovery)),
                ManagementOperationKind.Reprotect => completed is { ProtectionMode: ProtectionMode.SynchronousDualNode } ||
                    operation.HighAvailability is { State.ProtectionMode: ProtectionMode.SynchronousDualNode,
                        VerifiedAt: not null, SynchronousProtectionVerified: true },
                _ => false
            })))
            throw new HttpRequestException("管理操作阶段与实际结果不一致。");
    }

    private void RequireHighAvailability(HighAvailabilityStatus? availability)
    {
        if (availability is null) return;
        var state = availability.State;
        if (availability.ServerId != _serverId || availability.NodeId != _nodeId || availability.Checks is null ||
            state is null && (availability.VerifiedAt is not null || availability.SynchronousProtectionVerified) ||
            state is not null && (state.ServerId != _serverId || state.ServerEpoch <= 0 || state.Version <= 0 ||
                !Enum.IsDefined(state.ProtectionMode) || state.Nodes is null || state.Nodes.Count is < 1 or > 2 || !state.Nodes.ContainsKey(_nodeId) ||
                !state.Nodes.TryGetValue(state.ActiveNodeId, out var active) || active.Role != DeploymentNodeRole.Primary ||
                state.Nodes.Values.Count(n => n.Role == DeploymentNodeRole.Primary) != 1 ||
                state.Nodes.Any(n => n.Value is null || n.Key != n.Value.NodeId || !Enum.IsDefined(n.Value.Role))) ||
            availability.VerifiedAt is not null && !availability.Checks.Any(c => c.Name == "failover-state" && c.Passed && c.BlocksWrites))
            throw new HttpRequestException("主备响应身份、状态或核实证据无效。");
        if (availability.SynchronousProtectionVerified && (state is null || state.Nodes.Count != 2 ||
            availability.VerifiedAt is null || state.Nodes.Values.Count(n => n.Role == DeploymentNodeRole.Standby) != 1 ||
            state.Nodes.Values.Any(n => !n.Reachable || !n.KeyMaterialReady || !n.CertificateReady ||
                n.Role == DeploymentNodeRole.Primary && n.DatabaseInRecovery || n.Role == DeploymentNodeRole.Standby && !n.DatabaseInRecovery) ||
            availability.Checks.Any(c => c.BlocksWrites && !c.Passed) ||
            !availability.Checks.Any(c => c.Name == "synchronous-protection" && c.Passed)))
            throw new HttpRequestException("主备响应缺少完整同步保护证据。");
    }

    private static void RequireRequestId(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 200) throw new ArgumentException("requestId 无效。");
    }

    public void Dispose() => _http.Dispose();
}
