using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;

namespace Exam.Application.ServiceManagement;

public sealed partial class ManagementOperationRunner(
    string serverId, string nodeId, IManagementOperationStore store, IClock clock,
    Func<ManagementOperation, CancellationToken, Task<ManagementOperation?>>? reconcile = null,
    Action<ManagementOperation, Exception>? recordFailure = null,
    Func<ManagementOperation, CancellationToken, Task<ManagementOperation?>>? cancel = null,
    Func<ManagementOperation, CancellationToken, Task<ManagementOperation?>>? completeChange = null)
{
    private readonly SemaphoreSlim _completionWrites = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _executing = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _reconcilingFailover = new(StringComparer.Ordinal);

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
            var resumeMissingConfirmation = resumeStagedRestore && kind == ManagementOperationKind.Restore &&
                previous is { Phase: ManagementOperationPhase.NeedsVerification,
                    Restore.IsolationStaged: true, RecoveryActivation: null } &&
                previous.Checks?.Any(check => check.Name == "recovery-confirmed-state" && !check.Passed && check.BlocksWrites) == true;
            if (!(resumeStagedRestore && kind == ManagementOperationKind.Restore &&
                previous.Phase == ManagementOperationPhase.Staged || resumeMissingConfirmation))
                return await ReconcileUnderLockAsync(previous, cancellationToken);
        }

        // Staged recovery and an interrupted mutation must never be bypassed by a different start id.
        if (previous is null && (kind is ManagementOperationKind.Start or ManagementOperationKind.Configure or ManagementOperationKind.Restore ||
            HighAvailability.DurableFailoverCoordinator.IsFailover(kind)) &&
            await store.HasUnverifiedOperationsAsync(cancellationToken))
            throw new DomainRuleException(ErrorCodes.ServiceRecovering, "存在待核对的本机操作或暂存恢复，禁止自动启动、修改配置或再次恢复。");

        var accepted = previous ?? new ManagementOperation(requestId, serverId, nodeId, actorId, kind, hash,
            ManagementOperationPhase.Accepted, clock.UtcNow, clock.UtcNow, RequestJson: requestJson);
        async Task PreserveCheckpointAsync()
        {
            var checkpoint = await store.FindAsync(requestId, CancellationToken.None);
            if (checkpoint is not null && (checkpoint.Phase is ManagementOperationPhase.Staged or ManagementOperationPhase.NeedsVerification ||
                HighAvailability.DurableFailoverCoordinator.IsFailover(kind) && checkpoint.FailoverPlan is not null) && checkpoint.RequestId == accepted.RequestId &&
                checkpoint.ServerId == serverId && checkpoint.NodeId == nodeId && checkpoint.ActorId == actorId &&
                checkpoint.Kind == kind && checkpoint.ContentHash == hash && checkpoint.RequestJson == accepted.RequestJson &&
                checkpoint.AcceptedAt == accepted.AcceptedAt)
                accepted = checkpoint;
        }
        await store.SaveAsync(accepted, cancellationToken); // No side effect before this durable write.
        // Once accepted, HA work belongs to the durable journal rather than an HTTP/page
        // lifetime. Process loss is reconciled from checkpoints; normal disconnects do not cancel it.
        using var durableDeadline = HighAvailability.DurableFailoverCoordinator.IsFailover(kind)
            ? new CancellationTokenSource(ManagementTimeouts.HighAvailabilityOperation) : null;
        var executionToken = durableDeadline?.Token ?? cancellationToken;
        _executing.TryAdd(requestId, 0);
        try
        {
            if (durableDeadline is not null)
            {
                _cancellations[requestId] = durableDeadline;
                if (await store.IsCancellationRequestedAsync(requestId, CancellationToken.None)) durableDeadline.Cancel();
            }
            ManagementOperation result;
            Exception? executionError = null;
            try
            {
                result = await execute(accepted, executionToken);
                var pendingConfirmation = kind == ManagementOperationKind.Restore &&
                    result is { Phase: ManagementOperationPhase.NeedsVerification, Restore.IsolationStaged: true,
                        RecoveryActivation: null } &&
                    result.Checks?.Any(check => check.Name == "recovery-confirmed-state" && !check.Passed && check.BlocksWrites) == true;
                if (result.RequestId != accepted.RequestId || result.ContentHash != hash ||
                    result.ServerId != serverId || result.NodeId != nodeId || result.ActorId != actorId || result.Kind != kind ||
                    result.Phase is not (ManagementOperationPhase.Completed or ManagementOperationPhase.Staged) && !pendingConfirmation)
                    throw new InvalidOperationException("管理执行结果不完整。");
            }
            catch (DomainRuleException error)
            {
                await PreserveCheckpointAsync();
                executionError = error;
                result = accepted with { Phase = accepted.Phase is ManagementOperationPhase.Staged or ManagementOperationPhase.NeedsVerification ||
                        accepted.FailoverPlan is not null || error.Code == ErrorCodes.PersistenceFailed
                        ? ManagementOperationPhase.NeedsVerification : ManagementOperationPhase.Failed,
                    Error = new ApiError(error.Code, error.Message, Retryable: error.Retryable) };
            }
            catch (ArgumentException)
            {
                await PreserveCheckpointAsync();
                result = accepted with { Phase = accepted.Phase is ManagementOperationPhase.Staged or ManagementOperationPhase.NeedsVerification || accepted.FailoverPlan is not null
                        ? ManagementOperationPhase.NeedsVerification : ManagementOperationPhase.Failed,
                    Error = new ApiError(ErrorCodes.InvalidInput, "管理请求字段无效。") };
            }
            catch (Exception error)
            {
                await PreserveCheckpointAsync();
                executionError = error;
                // Includes cancellation after acceptance. SCM/tools may have taken effect; never repeat automatically.
                result = accepted with { Phase = kind == ManagementOperationKind.Checks
                        ? ManagementOperationPhase.Failed : ManagementOperationPhase.NeedsVerification,
                    Error = new ApiError(ErrorCodes.ServiceRecovering, kind == ManagementOperationKind.Checks
                        ? "状态检测未完成，可在修复依赖后用新的 requestId 再次检查。"
                        : "操作未能确认完成，须核对实际服务、配置及隔离数据；不得自动重试。") };
            }
            if (result.Phase != ManagementOperationPhase.Completed &&
                await store.IsCancellationRequestedAsync(requestId, CancellationToken.None))
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                result = await CancellationResultAsync(result, cleanup.Token);
            }
            result = result with { UpdatedAt = clock.UtcNow };
            // Disconnected clients cannot cancel durable final recording. Failure leaves Accepted for restart diagnosis.
            result = await SaveFinalAsync(result, CancellationToken.None);
            if (executionError is not null && result.Phase is ManagementOperationPhase.Failed or ManagementOperationPhase.NeedsVerification &&
                !await store.IsCancellationRequestedAsync(requestId, CancellationToken.None))
                try { recordFailure?.Invoke(result, executionError); }
                catch { /* Diagnostic failure must not change the durable result. */ }
            return result;
        }
        finally { _cancellations.TryRemove(requestId, out _); _executing.TryRemove(requestId, out _); }
    }

    public async Task<ManagementOperation> FindAsync(string requestId, string actorId, CancellationToken cancellationToken)
    {
        var operation = await store.FindAsync(requestId, cancellationToken)
            ?? throw new DomainRuleException(ErrorCodes.NotFound, "未找到本机管理操作。");
        if (operation.ServerId != serverId || operation.NodeId != nodeId || operation.ActorId != actorId)
            throw new DomainRuleException(ErrorCodes.Forbidden, "操作记录身份与本机已认证身份不符。");
        operation = await CompleteChangeAsync(operation, cancellationToken);
        if (_executing.ContainsKey(requestId) || reconcile is null ||
            operation.Phase is not (ManagementOperationPhase.Accepted or ManagementOperationPhase.NeedsVerification))
            return await CancellationProjectionAsync(operation, cancellationToken);
        if (HighAvailability.DurableFailoverCoordinator.IsFailover(operation.Kind))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_reconcilingFailover.TryAdd(requestId, 0))
                _ = ReconcileFailoverInBackgroundAsync(operation);
            return await CancellationProjectionAsync(operation, cancellationToken);
        }
        await using var guard = await store.AcquireAsync(cancellationToken);
        operation = await store.FindAsync(requestId, cancellationToken)
            ?? throw new DomainRuleException(ErrorCodes.NotFound, "未找到本机管理操作。");
        if (operation.ServerId != serverId || operation.NodeId != nodeId || operation.ActorId != actorId)
            throw new DomainRuleException(ErrorCodes.Forbidden, "操作记录身份与本机已认证身份不符。");
        return await ReconcileUnderLockAsync(operation, cancellationToken);
    }

    private async Task ReconcileFailoverInBackgroundAsync(ManagementOperation original)
    {
        // Finishing an interrupted installation may outlast any status request. The
        // original durable intent owns this work, just as it owns initial execution.
        // Repeated readers share it; disconnecting cannot restart/abort installation.
        await Task.Yield();
        using var deadline = new CancellationTokenSource(ManagementTimeouts.HighAvailabilityOperation);
        _cancellations[original.RequestId] = deadline;
        try
        {
            await using var guard = await store.AcquireAsync(deadline.Token);
            var operation = await store.FindAsync(original.RequestId, deadline.Token);
            if (operation is null || operation.ServerId != serverId || operation.NodeId != nodeId ||
                operation.ActorId != original.ActorId || operation.Kind != original.Kind || operation.ContentHash != original.ContentHash)
                throw new InvalidDataException("原主备请求的持久绑定发生变化。");
            await ReconcileUnderLockAsync(operation, deadline.Token);
        }
        catch (Exception error)
        {
            var cancellationRequested = await store.IsCancellationRequestedAsync(original.RequestId, CancellationToken.None);
            var latest = await store.FindAsync(original.RequestId, CancellationToken.None);
            if (!cancellationRequested && latest?.Phase != ManagementOperationPhase.Completed)
                try { recordFailure?.Invoke(original, error); } catch { }
            if (cancellationRequested)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                    await using var guard = await store.AcquireAsync(cleanup.Token);
                    var current = await store.FindAsync(original.RequestId, cleanup.Token);
                    if (current is not null && current.Phase is ManagementOperationPhase.Accepted or ManagementOperationPhase.NeedsVerification)
                        await SaveFinalAsync(await CancellationResultAsync(current, cleanup.Token), CancellationToken.None);
                }
                catch (Exception cleanupError) { try { recordFailure?.Invoke(original, cleanupError); } catch { } }
            }
            // Preserve the checkpoint; a later query/restart can continue the same intent.
        }
        finally { _cancellations.TryRemove(original.RequestId, out _); _reconcilingFailover.TryRemove(original.RequestId, out _); }
    }

    private async Task<ManagementOperation> ReconcileUnderLockAsync(ManagementOperation operation, CancellationToken token)
    {
        if (reconcile is null || _executing.ContainsKey(operation.RequestId) ||
            operation.Phase is not (ManagementOperationPhase.Accepted or ManagementOperationPhase.NeedsVerification))
            return ForQuery(operation);
        if (await store.IsCancellationRequestedAsync(operation.RequestId, token))
        {
            var canceled = await CancellationResultAsync(operation, token);
            await SaveFinalAsync(canceled, CancellationToken.None);
            return await CancellationProjectionAsync(canceled, token);
        }
        ManagementOperation? observed;
        try { observed = await reconcile(operation, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return ForQuery(operation); }
        if (observed is null) return ForQuery(operation);
        var failedWithoutIntent = HighAvailability.DurableFailoverCoordinator.IsFailover(operation.Kind) &&
            observed.Phase == ManagementOperationPhase.Failed && observed.Error is not null &&
            (operation.FailoverPlan is null || operation.Kind == ManagementOperationKind.Rebuild &&
                observed.FailoverFailureVerified && observed.FailoverPlan == operation.FailoverPlan);
        if (observed.RequestId != operation.RequestId || observed.ServerId != operation.ServerId ||
            observed.NodeId != operation.NodeId || observed.ActorId != operation.ActorId || observed.Kind != operation.Kind ||
            observed.ContentHash != operation.ContentHash || observed.RequestJson != operation.RequestJson ||
            observed.RecoveryWorkingDirectory != operation.RecoveryWorkingDirectory ||
            observed.AcceptedAt != operation.AcceptedAt || observed.Error is not null && !failedWithoutIntent ||
            (operation.Kind != ManagementOperationKind.Restore && observed.RecoveryActivation != operation.RecoveryActivation) ||
            (operation.Kind == ManagementOperationKind.Restore && observed.Phase == ManagementOperationPhase.Completed &&
             observed.Restore is not { IsolationStaged: true, RecoveryVerified: true, BusinessReady: true }) ||
            (observed.Phase != ManagementOperationPhase.Completed && !failedWithoutIntent &&
             !(operation.Kind == ManagementOperationKind.Restore && observed.Phase == ManagementOperationPhase.Staged &&
               observed.Restore is { IsolationStaged: true, BusinessReady: false })))
            throw new InvalidOperationException("操作核对结果未保留原请求绑定。");
        observed = observed with { UpdatedAt = clock.UtcNow };
        return await SaveFinalAsync(observed, token);
    }

    private async Task<ManagementOperation> CompleteChangeAsync(ManagementOperation operation, CancellationToken token)
    {
        if (completeChange is null || operation.Kind is not (ManagementOperationKind.Takeover or ManagementOperationKind.Rebuild) || operation.FailoverPlan is null ||
            operation.Phase is ManagementOperationPhase.Completed or ManagementOperationPhase.Failed) return operation;
        var completed = await completeChange(operation, token);
        return completed is null ? operation : await SaveFinalAsync(completed with
            { UpdatedAt = operation.Phase == ManagementOperationPhase.Canceled ? operation.UpdatedAt : clock.UtcNow }, token);
    }

    private async Task<ManagementOperation> SaveFinalAsync(ManagementOperation result, CancellationToken token)
    {
        await _completionWrites.WaitAsync(token);
        try
        {
            // A late startup failure or canceled HTTP wait cannot overwrite a proven takeover.
            var current = await store.FindAsync(result.RequestId, token);
            if (current is { Phase: ManagementOperationPhase.Completed, Kind: ManagementOperationKind.Takeover or ManagementOperationKind.Rebuild }) return current;
            await store.SaveAsync(result, token);
            return result;
        }
        finally { _completionWrites.Release(); }
    }

    private ManagementOperation ForQuery(ManagementOperation operation)
    {
        if (operation.Phase == ManagementOperationPhase.Accepted && !_executing.ContainsKey(operation.RequestId))
            operation = operation with { Phase = ManagementOperationPhase.NeedsVerification,
                Error = new ApiError(ErrorCodes.ServiceRecovering, "原操作尚未确认完成，先核对实际状态；不会重复执行。") };
        return operation with { FailoverWorkInProgress = HighAvailability.DurableFailoverCoordinator.IsFailover(operation.Kind) &&
            operation.Phase is ManagementOperationPhase.Accepted or ManagementOperationPhase.NeedsVerification &&
            (_executing.ContainsKey(operation.RequestId) || _reconcilingFailover.ContainsKey(operation.RequestId)) };
    }
}
