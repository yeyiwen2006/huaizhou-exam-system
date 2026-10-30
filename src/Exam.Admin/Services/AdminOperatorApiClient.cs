using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Operators;

namespace Exam.Admin.Services;

// 考务账号：注册与登录在未登录时调用；登录成功后以会话令牌和用户名配置本客户端。
public sealed partial class AdminApiClient
{
    private OperatorAccount? _account;

    public OperatorAccount? Account => _account;
    public bool IsAdministrator => _account?.IsAdministrator == true;

    public async Task<RegisterOperatorResult> RegisterAsync(string baseAddress, string username, string displayName, string password,
        CancellationToken cancellationToken = default) =>
        await SendAnonymousAsync<RegisterOperatorResult>(baseAddress, "api/v1/operators/register",
            new RegisterOperatorRequest(username, displayName, password), cancellationToken);

    /// <summary>登录成功后，后续请求都带上本次会话；用户名作为操作记录中的操作者。</summary>
    public async Task<OperatorAccount> LoginAsync(string baseAddress, string username, string password,
        CancellationToken cancellationToken = default)
    {
        var generation = Generation;
        var (address, routes) = await ResolveLoginRouteAsync(NormalizeAddress(baseAddress), cancellationToken);
        EnsureCurrentConnection(generation);
        ApiEnvelope<OperatorSession>? authenticated = null;
        var session = await SendAnonymousAsync<OperatorSession>(address.AbsoluteUri, "api/v1/operators/login",
            new LoginOperatorRequest(username, password), cancellationToken, envelope =>
            {
                if (string.IsNullOrWhiteSpace(envelope.ServerId) || string.IsNullOrWhiteSpace(envelope.NodeId) || envelope.ServerEpoch < 1 ||
                    routes is not null && (envelope.ServerId != routes.ServerId || envelope.ServerEpoch < routes.Epoch ||
                        routes.Nodes.Single(node => node.Address == address).NodeId != envelope.NodeId))
                    throw new System.IO.IOException("登录节点与可信主备身份不一致。");
                authenticated = envelope;
            }, routes, useCurrentRoutes: false);
        EnsureCurrentConnection(generation);
        _trustedRoutes = routes;
        Configure(address.AbsoluteUri, session.Token, session.Account.Username);
        _serverId = authenticated!.ServerId; _nodeId = authenticated.NodeId; _serverEpoch = authenticated.ServerEpoch;
        _account = session.Account;
        var authenticatedGeneration = Generation;
        // Route discovery is optional metadata after this node has authenticated us.
        // A transient discovery failure must not turn a usable login into a failure.
        try { await LearnRoutesAsync(cancellationToken); }
        catch (HttpRequestException) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (AdminApiException error) when ((int)error.StatusCode >= 500 && error.Code.StartsWith("HTTP_", StringComparison.Ordinal)) { }
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCurrentConnection(authenticatedGeneration);
        return session.Account;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (IsConfigured) await SendAsync<object>(HttpMethod.Post, "api/v1/operators/logout", null, cancellationToken);
        }
        catch (Exception error) when (error is AdminApiException or HttpRequestException or TaskCanceledException)
        {
            // 服务器不可达时仍清除本机会话；服务器上的会话可由管理员停用账号撤销。
        }
        finally
        {
            _account = null;
            Disconnect();
        }
    }

    public async Task<IReadOnlyList<OperatorAccount>> ListOperatorsAsync(CancellationToken cancellationToken = default) =>
        await SendAsync<OperatorAccount[]>(HttpMethod.Get, "api/v1/operators", null, cancellationToken);

    public async Task<OperatorAccount> SetOperatorStatusAsync(string username, OperatorStatus status,
        CancellationToken cancellationToken = default) =>
        await SendAsync<OperatorAccount>(HttpMethod.Put, "api/v1/operators/status",
            new SetOperatorStatusRequest(username, status), cancellationToken);

    private async Task<T> SendAnonymousAsync<T>(string baseAddress, string path, object body, CancellationToken cancellationToken,
        Action<ApiEnvelope<T>>? verify = null, TrustedRoutes? routes = null, bool useCurrentRoutes = true)
        where T : class
    {
        var address = NormalizeAddress(baseAddress);
        using var client = CreateRoutedClient(useCurrentRoutes ? _trustedRoutes : routes);
        client.BaseAddress = address;
        using var response = await client.PostAsJsonAsync(path, body, _json, cancellationToken);
        ApiEnvelope<T>? envelope = null;
        try { envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(_json, cancellationToken); }
        catch (JsonException) { }
        if (!response.IsSuccessStatusCode || envelope?.Error is not null || envelope?.Result is null)
            throw new AdminApiException(envelope?.Error?.Code ?? $"HTTP_{(int)response.StatusCode}",
                envelope?.Error?.Message ?? $"服务器请求失败：HTTP {(int)response.StatusCode}。",
                response.StatusCode);
        verify?.Invoke(envelope);
        return envelope.Result;
    }
}
