using System.Collections.ObjectModel;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Domain.Common;

namespace Exam.Application.HighAvailability;

public interface IFailoverPlatform
{
    Task PromoteStandbyAsync(string nodeId, CancellationToken cancellationToken);

    Task BeginRebuildAsync(string nodeId, string sourceNodeId, CancellationToken cancellationToken);
}

public sealed class FailoverCoordinator
{
    private readonly IFailoverPlatform _platform;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ServerDeploymentState> _completedRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NodeState> _nodes;
    private readonly List<FailoverAuditEntry> _audit = [];
    private string _activeNodeId;
    private long _serverEpoch;
    private long _version;
    private ProtectionMode _protectionMode;

    public FailoverCoordinator(
        string serverId,
        string activeNodeId,
        long serverEpoch,
        IEnumerable<NodeState> nodes,
        IFailoverPlatform platform,
        IClock clock)
    {
        if (string.IsNullOrWhiteSpace(serverId))
        {
            throw new ArgumentException("serverId 不能为空。", nameof(serverId));
        }

        ServerId = serverId;
        _nodes = nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        if (!_nodes.TryGetValue(activeNodeId, out var active) || active.Role != DeploymentNodeRole.Primary)
        {
            throw new ArgumentException("activeNodeId 必须指向主节点。", nameof(activeNodeId));
        }

        _activeNodeId = activeNodeId;
        _serverEpoch = serverEpoch;
        _version = 1;
        _protectionMode = ProtectionMode.SynchronousDualNode;
        _platform = platform;
        _clock = clock;
    }

    public string ServerId { get; }

    public ServerDeploymentState GetState() => Snapshot();

    public void ObserveNode(NodeState state)
    {
        lock (_nodes)
        {
            if (!_nodes.TryGetValue(state.NodeId, out var existing) || state.ObservedAt <= existing.ObservedAt)
            {
                return;
            }

            _nodes[state.NodeId] = state with { Role = existing.Role };
            _version++;
        }
    }

    public async Task<ServerDeploymentState> TakeOverAsync(
        TakeoverRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_completedRequests.TryGetValue(request.RequestId, out var replay))
            {
                return replay;
            }

            EnsureEpoch(request.ExpectedEpoch);
            if (!request.Isolation.IsSufficient ||
                !string.Equals(request.Isolation.IsolatedNodeId, _activeNodeId, StringComparison.Ordinal))
            {
                throw new DomainRuleException(ErrorCodes.NotActiveServer, "无法确认当前原主机已停止或已从业务网络隔离，禁止接管。");
            }

            NodeState? target;
            lock (_nodes)
            {
                _nodes.TryGetValue(request.TargetNodeId, out target);
            }

            if (target is null || target.Role != DeploymentNodeRole.Standby)
            {
                throw new DomainRuleException(ErrorCodes.ReplicaUnavailable, "目标节点不是可接管的备用节点。");
            }

            if (!target.Reachable || !target.DatabaseInRecovery || target.ReplayLagBytes != 0)
            {
                throw new DomainRuleException(ErrorCodes.ReplicaUnavailable, "备用节点未追平或复制状态不可确认。", retryable: true);
            }

            var observationAge = _clock.UtcNow - target.ObservedAt;
            if (observationAge > TimeSpan.FromSeconds(10) || observationAge < TimeSpan.FromSeconds(-5))
            {
                throw new DomainRuleException(ErrorCodes.ReplicaUnavailable, "备用节点状态已过期或时间异常，须重新检查。", retryable: true);
            }

            if (!target.KeyMaterialReady || !target.CertificateReady)
            {
                throw new DomainRuleException(ErrorCodes.ServiceRecovering, "备用节点缺少解锁材料或服务证书。", retryable: true);
            }

            await _platform.PromoteStandbyAsync(target.NodeId, cancellationToken);

            lock (_nodes)
            {
                var oldPrimary = _nodes[_activeNodeId];
                _nodes[_activeNodeId] = oldPrimary with { Role = DeploymentNodeRole.Isolated, Reachable = false };
                _nodes[target.NodeId] = target with { Role = DeploymentNodeRole.Primary, DatabaseInRecovery = false };
                _activeNodeId = target.NodeId;
                _serverEpoch++;
                _version++;
                _protectionMode = ProtectionMode.EmergencySingleNode;
                _audit.Add(new FailoverAuditEntry(
                    "TAKEOVER",
                    request.Isolation.ConfirmedBy,
                    request.Isolation.Reason,
                    target.NodeId,
                    _serverEpoch,
                    _clock.UtcNow));
            }

            var result = Snapshot();
            _completedRequests.Add(request.RequestId, result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServerDeploymentState> EnterEmergencyModeAsync(
        EmergencyModeRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_completedRequests.TryGetValue(request.RequestId, out var replay))
            {
                return replay;
            }

            EnsureEpoch(request.ExpectedEpoch);
            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                throw new DomainRuleException(ErrorCodes.InvalidInput, "进入单机应急模式必须记录原因。");
            }

            lock (_nodes)
            {
                _protectionMode = ProtectionMode.EmergencySingleNode;
                _version++;
                _audit.Add(new FailoverAuditEntry(
                    "ENTER_EMERGENCY_MODE",
                    request.ActorId,
                    request.Reason,
                    _activeNodeId,
                    _serverEpoch,
                    _clock.UtcNow));
            }
            var result = Snapshot();
            _completedRequests.Add(request.RequestId, result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServerDeploymentState> BeginRebuildAsync(
        RebuildNodeRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_completedRequests.TryGetValue(request.RequestId, out var replay))
            {
                return replay;
            }

            EnsureEpoch(request.ExpectedEpoch);
            NodeState? node;
            lock (_nodes)
            {
                _nodes.TryGetValue(request.NodeId, out node);
            }

            if (request.NodeId == _activeNodeId || node is null)
            {
                throw new DomainRuleException(ErrorCodes.InvalidInput, "只能从当前主节点重建非活动节点。");
            }

            await _platform.BeginRebuildAsync(request.NodeId, _activeNodeId, cancellationToken);
            lock (_nodes)
            {
                _nodes[request.NodeId] = node with { Role = DeploymentNodeRole.Rebuilding, Reachable = false };
                _protectionMode = ProtectionMode.Recovering;
                _version++;
                _audit.Add(new FailoverAuditEntry(
                    "BEGIN_REBUILD",
                    request.ActorId,
                    "旧节点隔离后从当前主节点重建",
                    request.NodeId,
                    _serverEpoch,
                    _clock.UtcNow));
            }
            var result = Snapshot();
            _completedRequests.Add(request.RequestId, result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServerDeploymentState> CompleteRebuildAsync(
        CompleteRebuildRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_completedRequests.TryGetValue(request.RequestId, out var replay))
            {
                return replay;
            }

            EnsureEpoch(request.ExpectedEpoch);
            lock (_nodes)
            {
                if (!_nodes.TryGetValue(request.NodeId, out var node) ||
                    node.Role != DeploymentNodeRole.Rebuilding)
                {
                    throw new DomainRuleException(ErrorCodes.InvalidInput, "只有正在重建的非活动节点可以完成重建。");
                }

                if (!node.Reachable || !node.DatabaseInRecovery || node.ReplayLagBytes != 0 ||
                    !node.KeyMaterialReady || !node.CertificateReady)
                {
                    throw new DomainRuleException(ErrorCodes.ReplicaUnavailable, "重建节点尚未追平或恢复材料不完整。", retryable: true);
                }

                _nodes[request.NodeId] = node with { Role = DeploymentNodeRole.Standby };
                _protectionMode = ProtectionMode.SynchronousDualNode;
                _version++;
                _audit.Add(new FailoverAuditEntry(
                    "COMPLETE_REBUILD",
                    request.ActorId,
                    "备用节点已追平并恢复同步保护",
                    request.NodeId,
                    _serverEpoch,
                    _clock.UtcNow));
            }

            var result = Snapshot();
            _completedRequests.Add(request.RequestId, result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void EnsureEpoch(long expectedEpoch)
    {
        if (expectedEpoch != _serverEpoch)
        {
            throw new DomainRuleException(ErrorCodes.StaleServerEpoch, "服务代次已变化，请重新读取主备状态。", retryable: true);
        }
    }

    private ServerDeploymentState Snapshot()
    {
        lock (_nodes)
        {
            return new ServerDeploymentState(
                ServerId,
                _activeNodeId,
                _serverEpoch,
                _protectionMode,
                new ReadOnlyDictionary<string, NodeState>(
                    new Dictionary<string, NodeState>(_nodes, StringComparer.Ordinal)),
                _version,
                Array.AsReadOnly(_audit.ToArray()));
        }
    }
}
