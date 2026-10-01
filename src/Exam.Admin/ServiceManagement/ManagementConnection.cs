using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Operators;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Admin.ServiceManagement;

public sealed record ManagementTarget(string Address, string ServerId, string NodeId)
{
    public Uri Validate()
    {
        if (!Uri.TryCreate(Address.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
              IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)) ||
            uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 ||
            string.IsNullOrWhiteSpace(ServerId) || ServerId.Length > 200 ||
            string.IsNullOrWhiteSpace(NodeId) || NodeId.Length > 200)
            throw new ArgumentException("请输入独立的本机 HTTPS 根地址和预期服务器、节点标识。");
        return uri;
    }

    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Validate().AbsoluteUri + "\n" + ServerId + "\n" + NodeId)));
}

public sealed record ManagementReply<T>(HttpStatusCode StatusCode, ManagementEnvelope<T> Envelope)
{
    public bool IsUnauthorized => StatusCode == HttpStatusCode.Unauthorized;
}

public interface IManagementConnection : IDisposable
{
    ManagementTarget Target { get; }
    string? Actor { get; }
    Task LoginAsync(string username, string password, CancellationToken token);
    Task LogoutAsync(CancellationToken token);
    Task<ManagementReply<ManagementStatus>> StatusAsync(CancellationToken token);
    Task<ManagementReply<IReadOnlyList<ManagementAuditEntry>>> AuditAsync(int limit, CancellationToken token);
    Task<ManagementReply<IReadOnlyList<ProtectedRuntimeLogEntry>>> LogsAsync(int limit, CancellationToken token);
    Task<ManagementReply<ManagementOperation>> ExecuteAsync(ManagementOperationKind kind, string body, CancellationToken token);
    Task<ManagementReply<ManagementOperation>> FindAsync(string id, CancellationToken token);
}

/// <summary>The GUI reuses the F01 transport; only independent session exchange is added here.</summary>
public sealed class ManagementConnection : IManagementConnection
{
    private readonly HttpClient _session;
    private readonly LocalManagementClient _reads;
    private readonly LocalManagementClient _writes;
    private readonly JsonSerializerOptions _json = JsonDefaults.Create();
    private string? _credential;
    public ManagementTarget Target { get; }
    public string? Actor { get; private set; }

    public ManagementConnection(ManagementTarget target, Func<HttpMessageHandler>? transport = null,
        TimeSpan? readTimeout = null, TimeSpan? writeTimeout = null)
    {
        Target = target;
        var endpoint = target.Validate();
        _session = new HttpClient(transport?.Invoke() ?? new HttpClientHandler
        { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
        { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(20) };
        _reads = new(endpoint, target.ServerId, target.NodeId, () => _credential, transport?.Invoke(),
            readTimeout ?? TimeSpan.FromSeconds(20));
        _writes = new(endpoint, target.ServerId, target.NodeId, () => _credential, transport?.Invoke(),
            writeTimeout ?? TimeSpan.FromMinutes(65));
    }

    public async Task LoginAsync(string username, string password, CancellationToken token)
    {
        Clear();
        if (OperatorRules.DescribeUsername(username) is not null || password.Length is < 1 or > 128)
            throw new ArgumentException("用户名或口令格式无效。");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/management/v1/login")
        { Content = new StringContent(JsonSerializer.Serialize(new LoginOperatorRequest(username.Trim(), password), _json), Encoding.UTF8, "application/json") };
        var reply = await SessionAsync<LoginReply>(request, token);
        if (reply.Error is not null) throw new UnauthorizedAccessException("管理登录失败，请核对已启用的考务账号与本机缓存。");
        var result = reply.Result;
        if (result is null || string.IsNullOrWhiteSpace(result.Token) || result.Token.Length > 4096 ||
            !string.Equals(result.Username, username.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new HttpRequestException("管理登录结果无效。");
        _credential = result.Token;
        Actor = result.Username;
    }

    public async Task LogoutAsync(CancellationToken token)
    {
        var oldCredential = _credential;
        Clear(); // Local credentials are cleared even if revocation cannot be confirmed.
        if (oldCredential is null) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/management/v1/logout");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oldCredential);
        var response = await SessionAsync<LogoutReply>(request, token);
        if (response.Error is not null || response.Result is not { LoggedOut: true })
            throw new HttpRequestException("服务端退出结果尚未确认。");
    }

    private async Task<ManagementEnvelope<T>> SessionAsync<T>(HttpRequestMessage request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await _session.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if ((int)response.StatusCode is >= 300 and < 400 || response.Content.Headers.ContentLength > 65536)
            throw new HttpRequestException("拒绝重定向或过大的管理会话响应。");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var memory = new System.IO.MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
        {
            if (memory.Length + read > 65536) throw new HttpRequestException("管理会话响应过大。");
            memory.Write(buffer, 0, read);
        }
        ManagementEnvelope<T>? envelope;
        try { envelope = JsonSerializer.Deserialize<ManagementEnvelope<T>>(memory.GetBuffer().AsSpan(0, (int)memory.Length), _json); }
        catch (JsonException) { throw new HttpRequestException("管理会话响应格式无效。"); }
        if (envelope is null || envelope.ServerId != Target.ServerId || envelope.NodeId != Target.NodeId ||
            string.IsNullOrWhiteSpace(envelope.CorrelationId) ||
            (envelope.EpochVerified ? envelope.ServerEpoch is null or <= 0 || envelope.EpochVerifiedAt is null :
                envelope.ServerEpoch is not null || envelope.EpochVerifiedAt is not null) ||
            (response.IsSuccessStatusCode ? envelope.Error is not null || envelope.Result is null : envelope.Error is null))
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized && envelope is not null &&
                envelope.ServerId == Target.ServerId && envelope.NodeId == Target.NodeId && envelope.Error is not null)
                throw new UnauthorizedAccessException("管理登录已拒绝或会话已失效。");
            throw new HttpRequestException("管理会话身份或结果无效。");
        }
        return envelope;
    }

    private ManagementReply<T> Wrap<T>(ManagementClientResponse<T> reply)
    {
        if (reply.StatusCode == HttpStatusCode.Unauthorized) Clear();
        return new(reply.StatusCode, reply.Envelope);
    }
    public async Task<ManagementReply<ManagementStatus>> StatusAsync(CancellationToken token) => Wrap(await _reads.GetStatusAsync(token));
    public async Task<ManagementReply<IReadOnlyList<ManagementAuditEntry>>> AuditAsync(int limit, CancellationToken token) => Wrap(await _reads.GetLogsAsync(limit, token));
    public async Task<ManagementReply<IReadOnlyList<ProtectedRuntimeLogEntry>>> LogsAsync(int limit, CancellationToken token) => Wrap(await _reads.GetRuntimeLogsAsync(limit, token));
    public async Task<ManagementReply<ManagementOperation>> ExecuteAsync(ManagementOperationKind kind, string body, CancellationToken token) =>
        Wrap(await _writes.ExecuteAsync(kind, body, token));
    public async Task<ManagementReply<ManagementOperation>> FindAsync(string id, CancellationToken token) => Wrap(await _reads.GetOperationAsync(id, token));
    private void Clear() { _credential = null; Actor = null; }
    public void Dispose() { Clear(); _session.Dispose(); _reads.Dispose(); _writes.Dispose(); }
    private sealed record LoginReply(string Token, string Username, bool IsAdministrator);
    private sealed record LogoutReply(bool LoggedOut);
}
