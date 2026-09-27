using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;

namespace Exam.Infrastructure.ServiceManagement;

public sealed record ManagementClientResponse<T>(HttpStatusCode StatusCode, ManagementEnvelope<T> Envelope);

/// <summary>Independent management transport shared by GUI and CLI; never probes business readiness.</summary>
public sealed class LocalManagementClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _serverId;
    private readonly string _nodeId;
    private readonly Func<string?> _token;
    private readonly JsonSerializerOptions _json = JsonDefaults.Create();

    public LocalManagementClient(Uri endpoint, string serverId, string nodeId, Func<string?> token,
        HttpMessageHandler? transport = null)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps ||
            !(endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
              IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address)) ||
            endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0 ||
            string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(nodeId))
            throw new ArgumentException("管理连接必须是独立的本机 HTTPS 根地址并指定预期服务器和节点。");
        _serverId = serverId;
        _nodeId = nodeId;
        _token = token ?? throw new ArgumentNullException(nameof(token));
        // Injected transports are for trusted composition/tests. Production uses normal certificate validation.
        _http = new HttpClient(transport ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
        { BaseAddress = endpoint, Timeout = TimeSpan.FromMinutes(65) };
    }

    public Task<ManagementClientResponse<ManagementStatus>> GetStatusAsync(CancellationToken token = default) =>
        SendAsync<ManagementStatus>(HttpMethod.Get, "service/status", null, token);

    public Task<ManagementClientResponse<ServiceConfiguration>> GetConfigurationAsync(CancellationToken token = default) =>
        SendAsync<ServiceConfiguration>(HttpMethod.Get, "service/configuration", null, token);

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
            _ => throw new ArgumentException("管理操作无效。")
        };
        var response = await SendAsync<ManagementOperation>(kind == ManagementOperationKind.Configure ? HttpMethod.Put : HttpMethod.Post,
            "service/" + route, requestJson, token);
        RequireOperationBinding(response.Envelope.Result, requestId, kind);
        return response;
    }

    private async Task<ManagementClientResponse<T>> SendAsync<T>(HttpMethod method, string path, string? body, CancellationToken token)
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
        if (!Enum.IsDefined(operation.Kind) || !Enum.IsDefined(operation.Phase) ||
            operation.Phase == ManagementOperationPhase.Staged &&
                (operation.Kind != ManagementOperationKind.Restore || operation.Restore is not
                    { IsolationStaged: true, RecoveryVerified: false, BusinessReady: false }) ||
            operation.Phase == ManagementOperationPhase.Completed && (operation.Error is not null || !(operation.Kind switch
            {
                ManagementOperationKind.Start => operation.Status is { ProcessStatus: BusinessServiceStatus.Running, Readiness: BusinessReadiness.Ready },
                ManagementOperationKind.Stop => operation.Status is { ProcessStatus: BusinessServiceStatus.Stopped },
                ManagementOperationKind.Configure => operation.Configuration is { Version: > 0 },
                ManagementOperationKind.Checks => operation.Checks is { Count: > 0 },
                ManagementOperationKind.Backup => operation.Backup is { IncludesDatabase: true, IncludesProtectedConfiguration: true, IncludesVersionedResources: true },
                ManagementOperationKind.Restore => operation.Restore is { IsolationStaged: true, RecoveryVerified: true, BusinessReady: true },
                _ => false
            })))
            throw new HttpRequestException("管理操作阶段与实际结果不一致。");
    }

    private static void RequireRequestId(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 200) throw new ArgumentException("requestId 无效。");
    }

    public void Dispose() => _http.Dispose();
}
