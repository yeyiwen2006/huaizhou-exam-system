using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;

namespace Exam.Application.ServiceManagement;

public sealed class ManagementOperationRunner(
    string serverId, string nodeId, IManagementOperationStore store, IClock clock,
    Func<ManagementOperation, CancellationToken, Task<ManagementOperation?>>? reconcile = null)
{
    private readonly ConcurrentDictionary<string, byte> _executing = new(StringComparer.Ordinal);

    public async Task<ManagementOperation> ExecuteAsync<T>(
        string requestId, string actorId, ManagementOperationKind kind, T content,
        Func<ManagementOperation, CancellationToken, Task<ManagementOperation>> execute,
        CancellationToken cancellationToken = default, bool resumeStagedRestore = false)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 200 || string.IsNullOrWhiteSpace(actorId))
            throw new DomainRuleException(ErrorCodes.InvalidInput, "请求标识或已认证操作者无效。");
        var requestJson = JsonSerializer.Serialize(content, JsonDefaults.Create());
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(requestJson)));
        await using var guard = await store.AcquireAsync(cancellationToken);
        var previous = await store.FindAsync(requestId, cancellationToken);
        if (previous is not null)
        {
            if (previous.ServerId != serverId || previous.NodeId != nodeId || previous.ActorId != actorId ||
                previous.Kind != kind || previous.ContentHash != hash)
                throw new DomainRuleException(ErrorCodes.VersionConflict, "requestId 已绑定不同的节点、操作者或操作内容。");
            if (!(resumeStagedRestore && kind == ManagementOperationKind.Restore && previous.Phase == ManagementOperationPhase.Staged))
                return await ReconcileUnderLockAsync(previous, cancellationToken);
        }

        // Staged recovery and an interrupted mutation must never be bypassed by a different start id.
        if (previous is null && kind is (ManagementOperationKind.Start or ManagementOperationKind.Configure or ManagementOperationKind.Restore) &&
            await store.HasUnverifiedOperationsAsync(cancellationToken))
            throw new DomainRuleException(ErrorCodes.ServiceRecovering, "存在待核对的本机操作或暂存恢复，禁止自动启动、修改配置或再次恢复。");

        var accepted = previous ?? new ManagementOperation(requestId, serverId, nodeId, actorId, kind, hash,
            ManagementOperationPhase.Accepted, clock.UtcNow, clock.UtcNow, RequestJson: requestJson);
        async Task PreserveCheckpointAsync()
        {
            var checkpoint = await store.FindAsync(requestId, CancellationToken.None);
            if (checkpoint is { Phase: ManagementOperationPhase.Staged or ManagementOperationPhase.NeedsVerification } && checkpoint.RequestId == accepted.RequestId &&
                checkpoint.ServerId == serverId && checkpoint.NodeId == nodeId && checkpoint.ActorId == actorId &&
                checkpoint.Kind == kind && checkpoint.ContentHash == hash && checkpoint.RequestJson == accepted.RequestJson &&
                checkpoint.AcceptedAt == accepted.AcceptedAt)
                accepted = checkpoint;
        }
        await store.SaveAsync(accepted, cancellationToken); // No side effect before this durable write.
        _executing.TryAdd(requestId, 0);
        try
        {
            ManagementOperation result;
            try
            {
                result = await execute(accepted, cancellationToken);
                if (result.RequestId != accepted.RequestId || result.ContentHash != hash ||
                    result.ServerId != serverId || result.NodeId != nodeId || result.ActorId != actorId || result.Kind != kind ||
                    result.Phase is not (ManagementOperationPhase.Completed or ManagementOperationPhase.Staged))
                    throw new InvalidOperationException("管理执行结果不完整。");
            }
            catch (DomainRuleException error)
            {
                await PreserveCheckpointAsync();
                result = accepted with { Phase = accepted.Phase is ManagementOperationPhase.Staged or ManagementOperationPhase.NeedsVerification || error.Code == ErrorCodes.PersistenceFailed
                        ? ManagementOperationPhase.NeedsVerification : ManagementOperationPhase.Failed,
                    Error = new ApiError(error.Code, error.Message, Retryable: error.Retryable) };
            }
            catch (ArgumentException)
            {
                await PreserveCheckpointAsync();
                result = accepted with { Phase = accepted.Phase is ManagementOperationPhase.Staged or ManagementOperationPhase.NeedsVerification
                        ? ManagementOperationPhase.NeedsVerification : ManagementOperationPhase.Failed,
                    Error = new ApiError(ErrorCodes.InvalidInput, "管理请求字段无效。") };
            }
            catch (Exception)
            {
                await PreserveCheckpointAsync();
                // Includes cancellation after acceptance. SCM/tools may have taken effect; never repeat automatically.
                result = accepted with { Phase = ManagementOperationPhase.NeedsVerification,
                    Error = new ApiError(ErrorCodes.ServiceRecovering, "操作未能确认完成，须核对实际服务、配置及隔离数据；不得自动重试。") };
            }
            result = result with { UpdatedAt = clock.UtcNow };
            // Disconnected clients cannot cancel durable final recording. Failure leaves Accepted for restart diagnosis.
            await store.SaveAsync(result, CancellationToken.None);
            return result;
        }
        finally { _executing.TryRemove(requestId, out _); }
    }

    public async Task<ManagementOperation> FindAsync(string requestId, string actorId, CancellationToken cancellationToken)
    {
        var operation = await store.FindAsync(requestId, cancellationToken)
            ?? throw new DomainRuleException(ErrorCodes.NotFound, "未找到本机管理操作。");
        if (operation.ServerId != serverId || operation.NodeId != nodeId || operation.ActorId != actorId)
            throw new DomainRuleException(ErrorCodes.Forbidden, "操作记录身份与本机已认证身份不符。");
        if (_executing.ContainsKey(requestId) || reconcile is null ||
            operation.Phase is not (ManagementOperationPhase.Accepted or ManagementOperationPhase.NeedsVerification))
            return ForQuery(operation);
        await using var guard = await store.AcquireAsync(cancellationToken);
        operation = await store.FindAsync(requestId, cancellationToken)
            ?? throw new DomainRuleException(ErrorCodes.NotFound, "未找到本机管理操作。");
        if (operation.ServerId != serverId || operation.NodeId != nodeId || operation.ActorId != actorId)
            throw new DomainRuleException(ErrorCodes.Forbidden, "操作记录身份与本机已认证身份不符。");
        return await ReconcileUnderLockAsync(operation, cancellationToken);
    }

    private async Task<ManagementOperation> ReconcileUnderLockAsync(ManagementOperation operation, CancellationToken token)
    {
        if (reconcile is null || _executing.ContainsKey(operation.RequestId) ||
            operation.Phase is not (ManagementOperationPhase.Accepted or ManagementOperationPhase.NeedsVerification))
            return ForQuery(operation);
        ManagementOperation? observed;
        try { observed = await reconcile(operation, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return ForQuery(operation); }
        if (observed is null) return ForQuery(operation);
        if (observed.RequestId != operation.RequestId || observed.ServerId != operation.ServerId ||
            observed.NodeId != operation.NodeId || observed.ActorId != operation.ActorId || observed.Kind != operation.Kind ||
            observed.ContentHash != operation.ContentHash || observed.RequestJson != operation.RequestJson ||
            observed.RecoveryWorkingDirectory != operation.RecoveryWorkingDirectory ||
            observed.AcceptedAt != operation.AcceptedAt || observed.Error is not null ||
            (operation.Kind != ManagementOperationKind.Restore && observed.RecoveryActivation != operation.RecoveryActivation) ||
            (operation.Kind == ManagementOperationKind.Restore && observed.Phase == ManagementOperationPhase.Completed &&
             observed.Restore is not { IsolationStaged: true, RecoveryVerified: true, BusinessReady: true }) ||
            (observed.Phase != ManagementOperationPhase.Completed &&
             !(operation.Kind == ManagementOperationKind.Restore && observed.Phase == ManagementOperationPhase.Staged &&
               observed.Restore is { IsolationStaged: true, BusinessReady: false })))
            throw new InvalidOperationException("操作核对结果未保留原请求绑定。");
        observed = observed with { UpdatedAt = clock.UtcNow };
        await store.SaveAsync(observed, token);
        return observed;
    }

    private ManagementOperation ForQuery(ManagementOperation operation) =>
        operation.Phase == ManagementOperationPhase.Accepted && !_executing.ContainsKey(operation.RequestId)
            ? operation with { Phase = ManagementOperationPhase.NeedsVerification,
                Error = new ApiError(ErrorCodes.ServiceRecovering, "原操作尚未确认完成，先核对实际状态；不会重复执行。") }
            : operation;
}
