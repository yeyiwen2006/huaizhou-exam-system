using System.Collections.Concurrent;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;

namespace Exam.Application.ServiceManagement;

public sealed partial class ManagementOperationRunner
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private static bool Cancelable(ManagementOperation operation) => operation.Kind is
        ManagementOperationKind.Takeover or ManagementOperationKind.Rebuild;
    private static bool Settled(ManagementOperation operation) => operation.Phase is
        ManagementOperationPhase.Completed or ManagementOperationPhase.Failed or ManagementOperationPhase.Canceled;

    public async Task<ManagementOperation> CancelAsync(string requestId, string actorId, CancellationToken token)
    {
        var operation = await store.FindAsync(requestId, token)
            ?? throw new DomainRuleException(ErrorCodes.NotFound, "未找到本机管理操作。");
        if (operation.ServerId != serverId || operation.NodeId != nodeId || operation.ActorId != actorId)
            throw new DomainRuleException(ErrorCodes.Forbidden, "只能取消当前账号在此节点发起的操作。");
        if (!Cancelable(operation)) throw new DomainRuleException(ErrorCodes.InvalidInput, "仅接管和重建支持取消。");
        if (Settled(operation)) return operation;
        // This small, independent intent does not wait for the execution journal lock.
        // It also survives a lost HTTP response or a management host restart.
        await store.RequestCancellationAsync(requestId, actorId, token);
        if (_cancellations.TryGetValue(requestId, out var execution))
        {
            try { execution.Cancel(); } catch (ObjectDisposedException) { }
        }
        return await FindAsync(requestId, actorId, token);
    }

    private async Task<ManagementOperation> CancellationProjectionAsync(ManagementOperation operation, CancellationToken token) =>
        ForQuery(operation) with { CancellationRequested = !Settled(operation) && Cancelable(operation) &&
            await store.IsCancellationRequestedAsync(operation.RequestId, token) };

    private async Task<ManagementOperation> CancellationResultAsync(ManagementOperation operation, CancellationToken token)
    {
        ManagementOperation? result = null;
        try
        {
            result = operation.FailoverPlan is null ? operation with { Phase = ManagementOperationPhase.Canceled, Error = null }
                : cancel is null ? null : await cancel(operation, token);
        }
        catch (Exception error) { try { recordFailure?.Invoke(operation, error); } catch { } }
        if (result is not null && (result.RequestId != operation.RequestId || result.ActorId != operation.ActorId ||
            result.ServerId != serverId || result.NodeId != nodeId || result.ContentHash != operation.ContentHash ||
            result.Kind != operation.Kind || result.Phase != ManagementOperationPhase.Canceled))
            throw new InvalidOperationException("取消结果未保留原操作身份。");
        return (result ?? operation with { Phase = ManagementOperationPhase.NeedsVerification,
            Error = new ApiError(ErrorCodes.ServiceRecovering, "正在结束本次操作，已生效的角色和数据保留。") }) with
            { UpdatedAt = clock.UtcNow, FailoverWorkInProgress = false, CancellationRequested = false };
    }
}
