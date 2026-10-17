using System.Net.Http.Headers;
using Exam.Contracts.Common;
using Exam.Infrastructure.HighAvailability;
using Exam.Infrastructure.ServiceManagement;
using Npgsql;

namespace Exam.Server.Runtime;

/// <summary>Never promotes a node. A trusted newer active node permanently retires this process's old epoch.
/// Physical isolation must remain in place until a returning node has been rebuilt from the active server.</summary>
public sealed class NodeAuthorityGuard(NpgsqlDataSource source, IConfiguration configuration,
    ServerIdentityOptions identity, HttpClient http, IHostApplicationLifetime lifetime)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _checked;
    private volatile bool _retired;
    private long _nextCheck;
    public bool IsRetired => _retired;

    public async Task<bool> CheckAsync(CancellationToken token)
    {
        if (_retired) return false;
        await _gate.WaitAsync(token);
        try
        {
            if (_retired) return false;
            var directory = configuration["Confirmation:Directory"];
            var retirement = string.IsNullOrWhiteSpace(directory) ? null : new NodeRetirementStore(directory);
            if (!_checked && retirement?.Read() is { } old &&
                (old.ServerId != identity.ServerId || old.NodeId != identity.NodeId ||
                 old.Epoch >= identity.ServerEpoch || old.ActiveEpoch > identity.ServerEpoch))
                return Retire();
            if (_checked && Environment.TickCount64 < _nextCheck) return true;
            _checked = true; _nextCheck = Environment.TickCount64 + 3000;
            var peerId = configuration["Confirmation:StandbyNodeId"];
            var address = configuration["Confirmation:StandbyHttpsUrl"];
            if (peerId == identity.NodeId || string.IsNullOrWhiteSpace(peerId))
            {
                var other = configuration.GetSection("HighAvailability:Nodes").GetChildren().SingleOrDefault(n => n.Key != identity.NodeId);
                peerId = other?.Key; address = other?["BusinessHttpsUrl"];
            }
            var secret = configuration["Confirmation:PeerToken"];
            if (string.IsNullOrWhiteSpace(peerId) || string.IsNullOrWhiteSpace(address) ||
                string.IsNullOrWhiteSpace(secret) || secret.Length < 32) return true;
            if (!Uri.TryCreate(address, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" ||
                endpoint.AbsolutePath != "/" || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
                throw new IOException("节点身份核对地址无效。");
            ReplicaResourceState? peer;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromMilliseconds(750));
                using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "replication/v1/authority"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                request.Headers.Add("X-Exam-Server-Id", identity.ServerId);
                request.Headers.Add("X-Exam-Node-Id", peerId);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 16384) return true;
                await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
                peer = await ReplicaResourceTransfer.ReadStateAsync(body, deadline.Token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { return true; } // Missing heartbeat never changes ownership.
            if (peer is null || peer.ServerId != identity.ServerId || peer.NodeId != peerId ||
                peer.InRecovery || peer.ActiveNodeId != peer.NodeId || peer.Epoch <= identity.ServerEpoch) return true;
            await using var system = source.CreateCommand("SELECT system_identifier::text FROM pg_control_system()");
            if ((string?)await system.ExecuteScalarAsync(token) != peer.SystemIdentifier)
                throw new IOException("可信节点的数据库集群身份不符。");
            _retired = true;
            try
            {
                if (retirement is null) throw new IOException("旧节点隔离记录目录不可用。");
                await retirement.SaveAsync(new RetiredNodeEpoch(identity.ServerId, identity.NodeId,
                    identity.ServerEpoch, peer.ActiveNodeId, peer.Epoch), token);
            }
            finally { lifetime.StopApplication(); }
            return false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return Retire(); }
        finally { _gate.Release(); }
    }

    private bool Retire()
    {
        _retired = true; lifetime.StopApplication(); return false;
    }
}
