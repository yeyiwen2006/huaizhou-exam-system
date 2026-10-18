using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Operators;

namespace Exam.Admin.Services;

public sealed partial class AdminApiClient
{
    private sealed record TrustedRoute(string NodeId, Uri Address, Uri Origin, string Pin)
    { public bool PublicAlias => Address != Origin; }
    private sealed record TrustedRoutes(string ServerId, long Epoch, string ActiveNodeId, TrustedRoute[] Nodes);
    private readonly SemaphoreSlim _routeGate = new(1, 1);
    private readonly string _routingFile;
    private TrustedRoutes? _trustedRoutes;
    private long _identityGeneration;
    private long _routeGeneration;
    private long _nextRouteAttempt;
    public event EventHandler? TrustedEndpointChanged;
    public Uri? CurrentEndpoint => _httpClient.BaseAddress;
    public string? SuggestedServerAddress
    { get { var saved = ReadRoutes(); return saved?.Nodes.FirstOrDefault(node => node.NodeId == saved.ActiveNodeId)?.Address.AbsoluteUri; } }

    private TrustedRoutes? ReadRoutes()
    {
        if (!File.Exists(_routingFile)) return null;
        if (new FileInfo(_routingFile).Length > 32768 || (File.GetAttributes(_routingFile) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("已保存的主备连接记录无效。");
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(_routingFile), Encoding.UTF8.GetBytes("Exam.Admin.Routes.v1"), DataProtectionScope.CurrentUser);
        try
        {
            var routes = JsonSerializer.Deserialize<TrustedRoutes>(bytes, _json) ?? throw new IOException("主备连接记录为空。");
            ValidateRoutes(routes); return routes;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void ValidateRoutes(TrustedRoutes routes)
    {
        if (string.IsNullOrWhiteSpace(routes.ServerId) || routes.Epoch < 1 || routes.Nodes is not { Length: >= 1 and <= 2 } ||
            !routes.Nodes.Any(n => n.NodeId == routes.ActiveNodeId) ||
            routes.Nodes.Select(n => n.NodeId).Distinct(StringComparer.Ordinal).Count() != routes.Nodes.Length ||
            routes.Nodes.Select(n => n.Address).Distinct().Count() != routes.Nodes.Length)
            throw new IOException("主备连接身份不完整。");
        foreach (var route in routes.Nodes)
            if (string.IsNullOrWhiteSpace(route.NodeId) || NormalizeAddress(route.Address.AbsoluteUri) != route.Address ||
                NormalizeAddress(route.Origin.AbsoluteUri) != route.Origin || route.Pin.Length != 64 || !route.Pin.All(char.IsAsciiHexDigit) ||
                route.PublicAlias && Uri.CheckHostName(route.Address.Host) != UriHostNameType.Dns)
                throw new IOException("主备连接地址或证书指纹无效。");
    }

    private void SaveRoutes(TrustedRoutes routes)
    {
        ValidateRoutes(routes);
        var parent = Path.GetDirectoryName(_routingFile)!;
        for (var directory = new DirectoryInfo(parent); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("主备连接目录不能经过链接。");
        Directory.CreateDirectory(parent);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(routes, _json);
        var temporary = _routingFile + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, ProtectedData.Protect(bytes, Encoding.UTF8.GetBytes("Exam.Admin.Routes.v1"), DataProtectionScope.CurrentUser));
            File.Move(temporary, _routingFile, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private HttpClient CreateRoutedClient(TrustedRoutes? routes)
    {
        if (_messageHandler is not null) return new(_messageHandler, disposeHandler: false);
        var handler = new HttpClientHandler { AllowAutoRedirect = false, Proxy = new RadminDirectProxy(HttpClient.DefaultProxy) };
        if (routes is not null)
            handler.ServerCertificateCustomValidationCallback = (request, certificate, _, errors) =>
            {
                var route = routes.Nodes.SingleOrDefault(node => request.RequestUri?.GetLeftPart(UriPartial.Authority) == node.Address.GetLeftPart(UriPartial.Authority));
                return route is null || route.PublicAlias ? errors == SslPolicyErrors.None : certificate is not null &&
                    certificate.GetCertHashString(HashAlgorithmName.SHA256).Equals(route.Pin, StringComparison.OrdinalIgnoreCase);
            };
        return new(handler) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 64 * 1024 * 1024 };
    }

    private async Task LearnRoutesAsync(CancellationToken token)
    {
        ActivatedServerTopology topology;
        try { topology = await SendAsync<ActivatedServerTopology>(HttpMethod.Get, "api/v1/operators/server-topology", null, token); }
        catch (AdminApiException error) when (error.StatusCode == HttpStatusCode.NotFound) { return; }
        if (topology.ServerId != _serverId || topology.Nodes is not { Count: >= 1 and <= 2 } || _httpClient.BaseAddress is not { } connected)
            throw new IOException("主备连接信息与当前服务器不一致。");
        var saved = _trustedRoutes;
        var nodes = topology.Nodes.Select(node =>
        {
            var origin = new UriBuilder(Uri.UriSchemeHttps, node.Host, node.Port).Uri;
            var previous = saved?.Nodes.SingleOrDefault(route => route.NodeId == node.NodeId);
            if (previous is not null && (previous.Origin != origin || !previous.Pin.Equals(node.CertificateSha256, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("主备节点身份变化，原连接记录已保留。");
            return previous ?? new TrustedRoute(node.NodeId, node.NodeId == _nodeId ? connected : origin, origin, node.CertificateSha256);
        }).ToArray();
        var routes = new TrustedRoutes(topology.ServerId, _serverEpoch, _nodeId!,
            nodes.Concat(saved?.Nodes.Where(old => nodes.All(node => node.NodeId != old.NodeId)) ?? []).ToArray());
        SaveRoutes(routes); _trustedRoutes = routes;
    }

    private async Task<Uri> ResolveLoginRouteAsync(Uri requested, CancellationToken token)
    {
        var saved = ReadRoutes();
        _trustedRoutes = saved?.Nodes.Any(node => node.Address == requested) == true ? saved : null;
        if (_trustedRoutes is not { } routes) return requested;
        var active = await FindActiveRouteAsync(routes, routes.Epoch, null, token);
        if (active is null) throw new HttpRequestException("可信主备尚未返回活动节点，原连接记录已保留，请稍后重试。");
        var verified = routes with { Epoch = active.Value.Epoch, ActiveNodeId = active.Value.Route.NodeId };
        SaveRoutes(verified); _trustedRoutes = verified;
        return active.Value.Route.Address;
    }

    private async Task<(TrustedRoute Route, long Epoch)?> FindActiveRouteAsync(TrustedRoutes routes, long minimumEpoch, string? preferred, CancellationToken token)
    {
        using var discovery = CancellationTokenSource.CreateLinkedTokenSource(token);
        async Task<(TrustedRoute Route, long Epoch)?> Probe(TrustedRoute route)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(discovery.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                using var client = CreateRoutedClient(routes);
                using var response = await client.GetAsync(new Uri(route.Address, "api/v1/commands/time"), deadline.Token);
                if (!response.IsSuccessStatusCode) return null;
                var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<ServerTimeSnapshot>>(_json, deadline.Token);
                if (envelope is not { IsSuccess: true, Result: { } result } || envelope.ServerId != routes.ServerId ||
                    envelope.NodeId != route.NodeId || envelope.ServerEpoch < minimumEpoch ||
                    envelope.ServerEpoch == routes.Epoch && envelope.NodeId != routes.ActiveNodeId ||
                    result.Server != new ServerNodeEnvelope(envelope.ServerId, envelope.NodeId, envelope.ServerEpoch)) return null;
                return (route, envelope.ServerEpoch);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
            catch (Exception error) when (error is HttpRequestException or JsonException) { return null; }
        }
        var probes = routes.Nodes.Where(node => preferred is null || node.NodeId == preferred).Select(Probe).ToList();
        try
        {
            while (probes.Count > 0)
            {
                await Task.WhenAny(probes);
                var ready = probes.Where(task => task.IsCompleted).ToArray();
                var results = await Task.WhenAll(ready);
                token.ThrowIfCancellationRequested();
                var active = results.Where(result => result.HasValue).OrderByDescending(result => result!.Value.Epoch).FirstOrDefault();
                if (active is not null) return active;
                foreach (var task in ready) probes.Remove(task);
            }
            return null;
        }
        finally
        {
            // A proven active node does not have to wait for the isolated old node's timeout.
            discovery.Cancel();
            try { await Task.WhenAll(probes); } catch (OperationCanceledException) { }
        }
    }

    public Task<bool> ReconnectToActiveNodeAsync(string serverId, string nodeId, long epoch, CancellationToken token = default) =>
        _trustedRoutes?.ServerId == serverId ? TryReconnectBusinessAsync(_routeGeneration, token, nodeId, epoch) : Task.FromResult(false);

    private async Task<bool> TryReconnectBusinessAsync(long failedGeneration, CancellationToken token, string? preferred = null, long minimumEpoch = 0)
    {
        var identity = _identityGeneration;
        await _routeGate.WaitAsync(token);
        try
        {
            if (identity != _identityGeneration || !IsConfigured || _trustedRoutes is not { } routes) return false;
            if (failedGeneration != _routeGeneration &&
                (preferred is null || preferred == _nodeId && _serverEpoch >= minimumEpoch)) return true;
            if (preferred is null && Environment.TickCount64 < _nextRouteAttempt) return false;
            var active = await FindActiveRouteAsync(routes, Math.Max(minimumEpoch, _serverEpoch), preferred, token);
            if (active is null) { _nextRouteAttempt = Environment.TickCount64 + 3000; return false; }
            if (active.Value.Route.Address == _httpClient.BaseAddress && active.Value.Epoch <= _serverEpoch) return false;
            var candidate = CreateRoutedClient(routes);
            try
            {
                candidate.BaseAddress = active.Value.Route.Address;
                candidate.DefaultRequestHeaders.Authorization = _httpClient.DefaultRequestHeaders.Authorization;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
                using var response = await candidate.GetAsync("api/v1/operators/me", deadline.Token);
                var account = await response.Content.ReadFromJsonAsync<ApiEnvelope<OperatorAccount>>(_json, deadline.Token);
                if (!response.IsSuccessStatusCode || account is not { IsSuccess: true, Result: { } result } ||
                    account.ServerId != routes.ServerId || account.NodeId != active.Value.Route.NodeId || account.ServerEpoch != active.Value.Epoch ||
                    !string.Equals(result.Username, _operatorId, StringComparison.OrdinalIgnoreCase) || result.Status != OperatorStatus.Active)
                    return false;
                if (identity != _identityGeneration) return false;
                var updated = routes with { Epoch = account.ServerEpoch, ActiveNodeId = account.NodeId };
                SaveRoutes(updated);
                var previous = _httpClient; _httpClient = candidate; candidate = null!;
                _trustedRoutes = updated; _serverId = account.ServerId; _nodeId = account.NodeId; _serverEpoch = account.ServerEpoch; _account = result;
                // View operations belong to the authenticated logical server, not an
                // endpoint. Keep public Generation stable so their finally blocks and
                // results remain valid across a trusted same-session route change.
                _routeGeneration++; _nextRouteAttempt = 0;
                previous.Dispose();
                TrustedEndpointChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            finally { candidate?.Dispose(); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (Exception error) when (error is HttpRequestException or JsonException) { return false; }
        finally { _routeGate.Release(); }
    }

    private static bool CanReconnect(Exception error) => error is HttpRequestException || error is OperationCanceledException ||
        error is AdminApiException api && (api.Code is ErrorCodes.NotActiveServer or ErrorCodes.StaleServerEpoch or ErrorCodes.ServiceRecovering or
            ErrorCodes.ReplicaUnavailable || (int)api.StatusCode >= 500);
}
