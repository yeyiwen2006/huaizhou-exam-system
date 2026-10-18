using System.Net.Http.Headers;
using System.Net.Http.Json;
using Exam.Application.Security;
using Exam.Contracts.Common;
using Exam.Contracts.Operators;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.HighAvailability;

namespace Exam.Management;

/// <summary>One bounded identity read from the configured peer; no database or exam-data probes.</summary>
public sealed class PeerBusinessSessionAuthenticator : IDisposable
{
    private readonly Func<string, string?> _configuration;
    private readonly string _serverId, _nodeId;
    private readonly Func<long> _epoch;
    private readonly HttpClient _http;

    public PeerBusinessSessionAuthenticator(Func<string, string?> configuration, string serverId, string nodeId,
        Func<long> epoch, HttpMessageHandler? transport = null)
    {
        _configuration = configuration; _serverId = serverId; _nodeId = nodeId; _epoch = epoch;
        _http = new(transport ?? new HttpClientHandler
        {
            AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
            ServerCertificateCustomValidationCallback = (request, certificate, _, _) =>
                certificate is not null && Peer() is { } peer && request.RequestUri is { } uri &&
                (uri.GetLeftPart(UriPartial.Authority) == peer.Address.GetLeftPart(UriPartial.Authority) ||
                    uri.GetLeftPart(UriPartial.Authority) == ManagementAddress(peer).GetLeftPart(UriPartial.Authority)) &&
                string.Equals(certificate.Thumbprint, peer.Thumbprint, StringComparison.OrdinalIgnoreCase)
        }) { Timeout = ManagementTimeouts.Read };
    }

    private RetiredNodeEpoch? Retirement()
    {
        var directory = _configuration("Confirmation:Directory") ??
            _configuration($"HighAvailability:Nodes:{_nodeId}:ConfirmationDirectory");
        return string.IsNullOrWhiteSpace(directory) ? null : new NodeRetirementStore(directory).Read();
    }

    public bool LocalBusinessSessionAllowed() => Retirement() is not { } retired || retired.ActiveEpoch < _epoch();

    private (string NodeId, Uri Address, string Thumbprint)? Peer()
    {
        var nodes = (_configuration("HighAvailability:RequiredNodeIds:0"), _configuration("HighAvailability:RequiredNodeIds:1"));
        var id = nodes.Item1 == _nodeId ? nodes.Item2 : nodes.Item2 == _nodeId ? nodes.Item1 : null;
        if (string.IsNullOrWhiteSpace(id) || !Uri.TryCreate(_configuration($"HighAvailability:Nodes:{id}:BusinessHttpsUrl"), UriKind.Absolute, out var address) ||
            address.Scheme != "https" || address.AbsolutePath != "/" || address.UserInfo.Length != 0 || address.Query.Length != 0 || address.Fragment.Length != 0)
            return null;
        var pin = _configuration($"HighAvailability:Nodes:{id}:CertificateThumbprint");
        return string.IsNullOrWhiteSpace(pin) ? null : (id, address, pin);
    }

    public async Task<AuthenticatedOperator?> AuthenticateAsync(string credential, CancellationToken token)
    {
        if (Peer() is not { } peer) return null;
        var retirement = Retirement();
        if (retirement is not null && (retirement.ServerId != _serverId || retirement.NodeId != _nodeId || retirement.ActiveNodeId != peer.NodeId)) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(ManagementTimeouts.Read);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(peer.Address, "api/v1/operators/me"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 65536) return null;
            var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<OperatorAccount>>(JsonDefaults.Create(), deadline.Token);
            return envelope is { Error: null, Result: { Status: OperatorStatus.Active } account } &&
                envelope.ServerId == _serverId && envelope.NodeId == peer.NodeId &&
                envelope.ServerEpoch >= Math.Max(_epoch(), retirement?.ActiveEpoch ?? 1)
                ? new(account.Username, account.IsAdministrator) : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException) { return null; }
    }

    public void Dispose() => _http.Dispose();

    private Uri ManagementAddress((string NodeId, Uri Address, string Thumbprint) peer)
    {
        if (Uri.TryCreate(_configuration($"HighAvailability:Nodes:{peer.NodeId}:ManagementHttpsUrl"), UriKind.Absolute, out var configured) &&
            configured.Scheme == "https" && configured.AbsolutePath == "/" && configured.UserInfo.Length == 0 &&
            configured.Query.Length == 0 && configured.Fragment.Length == 0) return configured;
        return new UriBuilder(peer.Address) { Port = int.TryParse(_configuration("Management:HttpsPort"), out var port) ? port : 19443 }.Uri;
    }

    public async Task<AuthenticatedOperator?> AuthenticateManagementAsync(string sourceNodeId, string credential, CancellationToken token)
    {
        if (Peer() is not { } peer || peer.NodeId != sourceNodeId) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(ManagementTimeouts.Read);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ManagementAddress(peer), "management/v1/identity"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) return null;
        var envelope = await response.Content.ReadFromJsonAsync<ManagementEnvelope<AuthenticatedOperator>>(JsonDefaults.Create(), deadline.Token);
        return envelope is { Error: null, Result: { } account } && envelope.ServerId == _serverId && envelope.NodeId == sourceNodeId ? account : null;
    }
}
