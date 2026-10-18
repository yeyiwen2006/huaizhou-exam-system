using System.Collections.Concurrent;
using System.Net;
using Exam.Contracts.HighAvailability;

namespace Exam.Admin.ServiceManagement;

public sealed partial class ServiceManagementViewModel
{
    private sealed record LocalNodeObservation(NodeState State, long Epoch, bool AuthenticationRequired = false);
    private readonly ConcurrentDictionary<string, LocalNodeObservation> _localNodeHealth = new(StringComparer.Ordinal);

    private async Task PollLocalNodeHealthAsync(ManagementTarget target, long version, CancellationToken token)
    {
        bool Current() => !token.IsCancellationRequested && !_disposed && _active && IsSignedIn && version == _managementSessionVersion;
        try
        {
            _localNodeHealth.TryAdd(target.Key, new(new(target.NodeId, DeploymentNodeRole.Isolated,
                false, false, long.MaxValue, false, false, default), 0));
            UpdateDisplayedNodes();
            while (Current())
            {
                var nextReadAt = _time.GetUtcNow().AddSeconds(3);
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(20));
                    var connection = NodeSession(target).Connection;
                    if (connection is null && _businessSession is { } session)
                        connection = await StartNodeAuthentication(target,
                            (client, cancellation) => client.UseBusinessSessionAsync(session, cancellation), session.Username).WaitAsync(deadline.Token);
                    if (!Current()) return;
                    if (connection is null) MarkLocalNodeUnavailable(target,
                        NodeSession(target).AuthenticationError is UnauthorizedAccessException);
                    else
                    {
                        var reply = await connection.LocalHighAvailabilityAsync(deadline.Token).WaitAsync(deadline.Token);
                        if (!Current()) return;
                        if (reply.StatusCode == HttpStatusCode.NotImplemented)
                        { _localNodeHealth.TryRemove(target.Key, out _); UpdateDisplayedNodes(); return; }
                        if (reply.IsUnauthorized || reply.Envelope.Error is not null || reply.Envelope.Result is not { } status ||
                            reply.Envelope.ServerId != target.ServerId || reply.Envelope.NodeId != target.NodeId)
                        {
                            MarkLocalNodeUnavailable(target, reply.IsUnauthorized);
                            if (reply.IsUnauthorized) MarkNodeSessionUnavailable(connection, true);
                        }
                        else
                        {
                            ValidateHighAvailabilityStatus(status, target);
                            if (status.State is not { } state || !state.Nodes.TryGetValue(target.NodeId, out var node) ||
                                node.ObservedAt == default || node.ObservedAt > _time.GetUtcNow().AddSeconds(5))
                                MarkLocalNodeUnavailable(target);
                            else
                            {
                                _localNodeHealth[target.Key] = new(node, state.ServerEpoch);
                                UpdateDisplayedNodes();
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch { if (Current()) MarkLocalNodeUnavailable(target); }
                var delay = nextReadAt - _time.GetUtcNow();
                if (Current() && delay > TimeSpan.Zero) await Task.Delay(delay, _time, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void MarkLocalNodeUnavailable(ManagementTarget target, bool authenticationRequired = false)
    {
        var prior = _localNodeHealth.GetValueOrDefault(target.Key);
        var role = _displayState?.Nodes.GetValueOrDefault(target.NodeId)?.Role ?? prior?.State.Role ?? DeploymentNodeRole.Isolated;
        _localNodeHealth[target.Key] = new(new(target.NodeId, role, false, role != DeploymentNodeRole.Primary,
            long.MaxValue, false, false, _time.GetUtcNow()), prior?.Epoch ?? _displayState?.ServerEpoch ?? 0, authenticationRequired);
        UpdateDisplayedNodes();
    }

    private LocalNodeObservation? LocalObservation(string nodeId)
    {
        var target = ConfiguredManagementTargets.FirstOrDefault(t => t.NodeId == nodeId);
        return target is null ? null : _localNodeHealth.GetValueOrDefault(target.Key);
    }

    private ManagementNodeRow LocalNodeRow(NodeState node, bool fresh, bool authenticationRequired = false) => new(node.NodeId,
        node.Role switch { DeploymentNodeRole.Primary => "主机", DeploymentNodeRole.Standby => "备机", DeploymentNodeRole.Isolated => "隔离", _ => "重建" },
        fresh ? authenticationRequired ? "待认证" : node.Reachable ? "可连接" : "不可连接" : "未核实",
        fresh && node.Reachable ? node.DatabaseInRecovery ? "备库" : "主库" : "未核实",
        fresh && node.Reachable && node.ReplayLagBytes != long.MaxValue ? node.ReplayLagBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) : "未核实",
        fresh && node.KeyMaterialReady ? "已核实" : "未核实", fresh && node.CertificateReady ? "已核实" : "未核实",
        node.ObservedAt == default ? "尚未返回" : node.ObservedAt.ToOffset(TimeSpan.FromHours(8)).ToString("HH:mm:ss") + (fresh ? "" : "（已过期）"),
        IsCurrent: fresh);
}
