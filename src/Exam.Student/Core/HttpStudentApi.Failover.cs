using System.Net.Http;
using System.Net.Http.Json;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Activation;

namespace Exam.Student.Core;

public sealed partial class HttpStudentApi
{
    private long _nextFailedPeerProbe;
    public Task<ApiEnvelope<ActivatedServerTopology>> GetTrustedTopologyAsync(CancellationToken ct = default) =>
        SendAsync<ActivatedServerTopology>(HttpMethod.Get, "/api/v1/student/server-topology", null, true, ct);
    public sealed class IdentityRejectedException(string message) : HttpRequestException(message);

    public (DeviceRecord Device, ServerNodeEnvelope Server, long Generation)? CurrentTrustedEndpoint
    {
        get
        {
            lock (_connectionGate)
                return _device is null || _trustedServerId is null || _nodeId is null || _server is null ? null :
                    (_device with { Host = _server.Host, Port = _server.Port, ServerEpoch = _serverEpoch },
                        new(_trustedServerId, _nodeId, _serverEpoch), _connectionGeneration);
        }
    }

    public bool IsCurrentEndpoint(long generation)
    { lock (_connectionGate) return generation == _connectionGeneration; }

    private void ValidateIdentity(string serverId, string nodeId, long epoch, bool allowStaleStatus = false)
    {
        if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(nodeId) || epoch < 1 ||
            _trustedServerId is not null && serverId != _trustedServerId || !allowStaleStatus && epoch < _serverEpoch ||
            !allowStaleStatus && epoch == _serverEpoch && _device?.ActiveNodeId is { } activeNode && nodeId != activeNode ||
            _device?.Topology is { } topology && topology.Nodes.SingleOrDefault(node => node.Address == _server)?.NodeId != nodeId)
            throw new IdentityRejectedException("服务器身份、节点或服务代次不匹配，原登记和答卷已保留。");
    }

    private async Task<bool> TryReconnectAsync(long identityGeneration, long failedGeneration, CancellationToken ct)
    {
        await _failoverGate.WaitAsync(ct);
        var changed = false;
        try
        {
            TrustedServerTopology? topology;
            Uri? failed;
            long minimumEpoch;
            string? confirmedNodeId;
            lock (_connectionGate)
            {
                if (identityGeneration != _identityGeneration) throw new IdentityRejectedException("设备身份已切换，停止原请求。");
                if (failedGeneration != _connectionGeneration) return true;
                topology = _device?.Topology;
                failed = _server;
                minimumEpoch = _serverEpoch;
                confirmedNodeId = _device?.ActiveNodeId;
            }
            if (topology is null) return false;
            if (Environment.TickCount64 < Interlocked.Read(ref _nextFailedPeerProbe)) return false;
            foreach (var node in topology.Nodes.Where(node => node.Address != failed))
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromSeconds(15));
                    // No credentials, redirect following, discovery or promotion during probing.
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(node.Address, "/api/v1/commands/time"));
                    using var response = await SendTrustedAsync(request, deadline.Token);
                    if (!response.IsSuccessStatusCode) continue;
                    var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<ServerTimeSnapshot>>(_json, deadline.Token);
                    if (envelope is not { IsSuccess: true, Result: { } result } || envelope.ServerId != topology.ServerId ||
                        envelope.NodeId != node.NodeId || envelope.ServerEpoch < minimumEpoch ||
                        envelope.ServerEpoch == minimumEpoch && node.NodeId != confirmedNodeId ||
                        result.Server != new ServerNodeEnvelope(envelope.ServerId, envelope.NodeId, envelope.ServerEpoch)) continue;
                    lock (_connectionGate)
                    {
                        if (identityGeneration != _identityGeneration) throw new IdentityRejectedException("设备身份已切换，停止原请求。");
                        if (failedGeneration != _connectionGeneration) return true;
                        if (envelope.ServerEpoch < _serverEpoch ||
                            envelope.ServerEpoch == _serverEpoch && node.NodeId != _device?.ActiveNodeId) continue;
                        var updated = _device! with { ServerEpoch = envelope.ServerEpoch, ActiveNodeId = node.NodeId };
                        (PersistTrustedConnection ?? throw new InvalidOperationException("主备连接缺少受保护持久化入口。"))(updated);
                        _device = updated;
                        _server = node.Address;
                        _nodeId = node.NodeId;
                        _serverEpoch = envelope.ServerEpoch;
                        _connectionGeneration++;
                        changed = true;
                        Interlocked.Exchange(ref _nextFailedPeerProbe, 0);
                    }
                    return true;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                catch (Exception error) when (error is HttpRequestException and not IdentityRejectedException or System.Text.Json.JsonException) { }
            }
            Interlocked.Exchange(ref _nextFailedPeerProbe, Environment.TickCount64 + 3000);
            return false;
        }
        finally
        {
            _failoverGate.Release();
            if (changed) TrustedEndpointChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
