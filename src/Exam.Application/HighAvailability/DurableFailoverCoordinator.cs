using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;

namespace Exam.Application.HighAvailability;

public interface IHighAvailabilityManagementPlatform
{
    Task<HighAvailabilityStatus> ObserveAsync(CancellationToken token);
    Task<HighAvailabilityStatus> ObserveLocalAsync(CancellationToken token) => ObserveAsync(token);
    Task<FailoverExecutionPlan> PrepareAsync(ManagementOperation operation, HighAvailabilityRequest request,
        ServerDeploymentState before, ServerDeploymentState after, CancellationToken token);
    Task ExecuteAsync(FailoverExecutionPlan plan, CancellationToken token);
    // Reconciliation can finish an already-proven publication, but must not repeat promotion/rebuild.
    Task<bool> ReconcileAsync(FailoverExecutionPlan plan, CancellationToken token);
    Task<bool> VerifyFailedBeforeInstallationAsync(FailoverExecutionPlan plan, CancellationToken token) => Task.FromResult(false);
}

/// <summary>Uses the same cross-process durable journal as local management. No production state lives in memory.</summary>
public sealed class DurableFailoverCoordinator(string serverId, string nodeId,
    IHighAvailabilityManagementPlatform platform, IManagementOperationStore journal, IClock clock) : IDisposable
{
    public static bool IsFailover(ManagementOperationKind kind) => kind is ManagementOperationKind.Takeover or
        ManagementOperationKind.Emergency or ManagementOperationKind.Rebuild or ManagementOperationKind.Reprotect;

    public Task<HighAvailabilityStatus> GetStatusAsync(CancellationToken token) => platform.ObserveAsync(token);
    public Task<HighAvailabilityStatus> GetLocalStatusAsync(CancellationToken token) => platform.ObserveLocalAsync(token);

    public async Task<ManagementOperation> ExecuteAsync(ManagementOperation operation,
        HighAvailabilityRequest request, CancellationToken token)
    {
        if (!IsFailover(operation.Kind) || request.ServerId != serverId || request.NodeId != nodeId ||
            request.ActorId != operation.ActorId || request.RequestId != operation.RequestId ||
            request.Reason is null || request.Reason.Length > 500 ||
            string.IsNullOrWhiteSpace(request.TargetNodeId) || request.TargetNodeId.Length > 200)
            throw new DomainRuleException(ErrorCodes.InvalidInput, "主备请求的目标、操作者或备注无效。");
        var observed = await platform.ObserveAsync(token);
        var before = observed.State;
        if (observed.ServerId != serverId || observed.NodeId != nodeId || before is null ||
            observed.VerifiedAt is null || clock.UtcNow - observed.VerifiedAt > HighAvailabilityStatus.ObservationLifetime ||
            observed.VerifiedAt - clock.UtcNow > TimeSpan.FromSeconds(5))
            throw new DomainRuleException(ErrorCodes.ReplicaUnavailable, "当前主备状态无法核实，请重新查询。", true);
        if (request.ExpectedEpoch != before.ServerEpoch)
            throw new DomainRuleException(ErrorCodes.StaleServerEpoch, "主备代次已变化，须重新查询并确认。", true);
        if (request.ExpectedVersion != before.Version || before.Version == long.MaxValue || before.ServerEpoch == long.MaxValue)
            throw new DomainRuleException(ErrorCodes.VersionConflict, "主备状态版本已变化，须重新查询并确认。", true);
        if (!before.Nodes.TryGetValue(request.TargetNodeId, out var target))
            throw new DomainRuleException(ErrorCodes.InvalidInput, "主备目标不属于本次部署。");

        var nodes = new Dictionary<string, NodeState>(before.Nodes, StringComparer.Ordinal);
        var active = before.ActiveNodeId; var epoch = before.ServerEpoch;
        var mode = ProtectionMode.EmergencySingleNode;
        switch (operation.Kind)
        {
            case ManagementOperationKind.Takeover:
                if (request.TargetNodeId != nodeId || target.Role != DeploymentNodeRole.Standby ||
                    request.Isolation is not { IsSufficient: true } isolation || isolation.IsolatedNodeId != active ||
                    isolation.ConfirmedBy != operation.ActorId ||
                    string.IsNullOrWhiteSpace(isolation.Reason) || isolation.Reason.Length > 500)
                    throw new DomainRuleException(ErrorCodes.NotActiveServer, "须确认原主机断电或业务网络隔离，且仅可接管本机备节点。");
                if (!target.Reachable || !target.DatabaseInRecovery ||
                    !target.KeyMaterialReady || !target.CertificateReady ||
                    clock.UtcNow - target.ObservedAt > HighAvailabilityStatus.ObservationLifetime || target.ObservedAt > clock.UtcNow.AddSeconds(5))
                    throw new DomainRuleException(ErrorCodes.ReplicaUnavailable, "备节点复制、解锁材料或证书未核实。", true);
                nodes[active] = nodes[active] with { Role = DeploymentNodeRole.Isolated, Reachable = false };
                nodes[nodeId] = target with { Role = DeploymentNodeRole.Primary, DatabaseInRecovery = false };
                active = nodeId; epoch++;
                break;
            case ManagementOperationKind.Emergency:
                throw new DomainRuleException(ErrorCodes.InvalidInput,
                    "健康主节点已自动支持本机持久保存，无需单独启用单机应急。");
            case ManagementOperationKind.Rebuild:
                if (request.TargetNodeId != nodeId || nodeId == active || !request.ConfirmDestructiveRebuild ||
                    !(target.Role is DeploymentNodeRole.Isolated or DeploymentNodeRole.Rebuilding ||
                      target.Role == DeploymentNodeRole.Standby))
                    throw new DomainRuleException(ErrorCodes.InvalidInput, "只能明确确认后重建本机隔离旧节点；禁止重建活动主节点。");
                nodes[nodeId] = target with { Role = DeploymentNodeRole.Standby, Reachable = false };
                // The active server remains available while this non-active node is rebuilt.
                // Actual role/replay/resource checks keep it unprotected until it is ready.
                mode = ProtectionMode.EmergencySingleNode;
                break;
            case ManagementOperationKind.Reprotect:
                if (active != nodeId || target.NodeId == active ||
                    !(target.Role == DeploymentNodeRole.Rebuilding ||
                      target.Role == DeploymentNodeRole.Standby))
                    throw new DomainRuleException(ErrorCodes.InvalidInput, "须在当前主节点核对已重建备节点后恢复同步保护。");
                nodes[target.NodeId] = target with { Role = DeploymentNodeRole.Standby };
                mode = ProtectionMode.SynchronousDualNode;
                break;
        }
        var audit = before.Audit.Append(new FailoverAuditEntry(Action(operation), operation.ActorId, request.Reason,
            request.TargetNodeId, epoch, clock.UtcNow)).ToArray();
        var after = before with { ActiveNodeId = active, ServerEpoch = epoch, Version = before.Version + 1,
            ProtectionMode = mode, Nodes = nodes, Audit = audit };
        var plan = await platform.PrepareAsync(operation, request, before, after, token);
        if (plan.RequestId != operation.RequestId || plan.ContentHash != operation.ContentHash || plan.Kind != operation.Kind ||
            plan.Before != before || plan.After != after)
            throw new InvalidOperationException("主备平台未保留本次请求绑定。");
        operation = operation with { FailoverPlan = plan, UpdatedAt = clock.UtcNow };
        await journal.SaveAsync(operation, token); // Durable intent precedes every external effect.
        await platform.ExecuteAsync(plan, token);
        // A restarted pool or promotion checkpoint can lag the completed external
        // action briefly. Reconcile the same durable intent; never repeat promotion.
        using (var verification = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            verification.CancelAfter(TimeSpan.FromSeconds(45));
            while (!await platform.ReconcileAsync(plan, verification.Token))
                await Task.Delay(TimeSpan.FromSeconds(1), verification.Token);
        }
        return await CompletedAsync(operation, plan, token);
    }

    public async Task<ManagementOperation?> ReconcileAsync(ManagementOperation operation, CancellationToken token)
    {
        if (!IsFailover(operation.Kind)) return null;
        if (operation.FailoverPlan is not { } plan)
            return operation with { Phase = ManagementOperationPhase.Failed,
                Error = new(ErrorCodes.ServiceRecovering, "主备持久意图尚未写入，未执行主备副作用；请重新读取状态后确认。") };
        if (plan.RequestId != operation.RequestId || plan.ContentHash != operation.ContentHash || plan.Kind != operation.Kind ||
            plan.Before.ServerId != serverId || plan.After.ServerId != serverId ||
            plan.After.Version != plan.Before.Version + 1 ||
            !plan.After.Audit.Any(a => a.Action == Action(operation) && a.ActorId == operation.ActorId))
            throw new InvalidDataException("主备持久意图的请求绑定损坏。");
        if (operation.Kind == ManagementOperationKind.Rebuild && await platform.VerifyFailedBeforeInstallationAsync(plan, token))
        {
            return operation with { Phase = ManagementOperationPhase.Failed, FailoverFailureVerified = true,
                Error = new(ErrorCodes.ServiceRecovering, "重建准备已安全终结；已发布的权威状态、旧数据及审计保留。请刷新状态后发起新的重建请求。") };
        }
        if (!await platform.ReconcileAsync(plan, token)) return null;
        return await CompletedAsync(operation, plan, token);
    }

    private async Task<ManagementOperation> CompletedAsync(ManagementOperation operation, FailoverExecutionPlan plan, CancellationToken token)
    {
        var observed = await platform.ObserveAsync(token);
        if (observed.State is not { } state || observed.VerifiedAt is not { } time ||
            clock.UtcNow - time > HighAvailabilityStatus.ObservationLifetime || time > clock.UtcNow.AddSeconds(5) ||
            !FailoverStateHistory.Covers(state, plan.After) ||
            state.Version == plan.After.Version && plan.Kind == ManagementOperationKind.Reprotect && !observed.SynchronousProtectionVerified)
            throw new IOException("主备操作完成状态的最新实际证据尚未核实。");
        return operation with { Phase = ManagementOperationPhase.Completed, Error = null, HighAvailability = observed,
            FailoverCompletedState = plan.After };
    }

    public static string Action(ManagementOperation operation) => $"F02:{operation.Kind}:{operation.RequestId}";
    public void Dispose() { if (platform is IDisposable disposable) disposable.Dispose(); }
}
