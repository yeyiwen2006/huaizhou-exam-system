using System.Net;
using Exam.Contracts.ServiceManagement;

namespace Exam.Admin.ServiceManagement;

public sealed partial class ServiceManagementViewModel
{
    private ManagementOperation? _restoreOperation;

    private async Task<BackupInfo?> ReadRestoreBackupAsync(IManagementConnection connection, long generation)
    {
        var directory = RequirePath(RestoreSource);
        using var read = BeginRead();
        try
        {
            var reply = await connection.BackupInfoAsync(directory, read.Token);
            if (read.IsCancellationRequested || !Accept(connection, generation, reply.IsUnauthorized)) return null;
            if (reply.Envelope.Error is not null || reply.Envelope.Result is not { } backup)
            {
                RestoreText = "备份信息未能确认";
                Message = "无法读取该服务器目录中的备份信息，请核对备份来源目录和备份材料。";
                return null;
            }
            if (string.IsNullOrWhiteSpace(backup.BackupId) || backup.CreatedAt == default)
                throw new System.IO.InvalidDataException("备份信息无效。");
            RequirePath(backup.SourceDirectory);
            RestoreBackupId = backup.BackupId;
            return backup;
        }
        catch
        {
            if (Current(connection, generation))
            {
                RestoreText = "备份信息未能确认";
                Message = "备份信息读取未完成，请核对服务器连接和备份来源目录后重新操作。";
            }
            return null;
        }
        finally { EndRead(read); }
    }

    private static string RestoreConfirmationBody(PendingManagementRequest request, BackupInfo backup) =>
        $"是否让服务器回到此备份所保存的状态？\n\n目标服务器：{request.Target.ServerId} / {request.Target.NodeId}" +
        $"\n服务器上的备份来源目录：{backup.SourceDirectory}" +
        $"\n备份时间：{backup.CreatedAt.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm:ss}（北京时间）" +
        "\n\n恢复前须停止目标服务器。系统会准备恢复副本、核对恢复条件，并检测恢复后的业务状态。" +
        "旧备份缺少后续已确认的数据时，恢复条件核对会阻止服务器开放，不会丢弃这些后续数据来强行回退。";

    private async Task ExecuteRestoreAsync(PendingManagementRequest request, IManagementConnection connection, long generation)
    {
        if (_restoreOperation?.RequestId != request.RequestId) _restoreOperation = null;
        _restoreWriteInFlight = request.RequestId;
        RestoreText = RestoreStatus(_restoreOperation, true);
        using var progress = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _restoreProgress = progress;
        try
        {
            // Progress cancellation only ends reads. The original write remains a single request.
            var write = connection.ExecuteAsync(request.Kind, request.Body, _lifetime.Token);
            _ = PollRestoreProgressAsync(request, connection, generation, progress.Token);
            var reply = await write;
            progress.Cancel();
            if (Current(connection, generation)) Apply(reply, request, connection, generation);
        }
        catch
        {
            progress.Cancel();
            if (Current(connection, generation))
            {
                RestoreText = RestoreStatus(_restoreOperation, false);
                Message = "恢复结果尚未确认。原请求已保留，请查询请求处理状态；服务器操作不会因停止等待而撤销。";
            }
        }
        finally
        {
            progress.Cancel();
            if (ReferenceEquals(_restoreProgress, progress)) _restoreProgress = null;
            if (Current(connection, generation))
            {
                _restoreWriteInFlight = null;
                RestoreText = RestoreStatus(_restoreOperation, false);
            }
        }
        if (Current(connection, generation) && RestoreCompleted(_restoreOperation))
            await RefreshRestoredServerAsync(connection, generation);
    }

    private async Task PollRestoreProgressAsync(PendingManagementRequest request, IManagementConnection connection,
        long generation, CancellationToken token)
    {
        try
        {
            while (Current(connection, generation) && _restoreWriteInFlight == request.RequestId)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _time, token);
                var reply = await connection.FindAsync(request.RequestId, token);
                if (token.IsCancellationRequested || !Current(connection, generation) || _restoreWriteInFlight != request.RequestId) return;
                // The journal may not yet contain the just-submitted request. Never resubmit it.
                if (reply.StatusCode == HttpStatusCode.NotFound) continue;
                Apply(reply, request, connection, generation, restoreInProgress: true);
                if (reply.Envelope.Result?.Phase is ManagementOperationPhase.Completed or ManagementOperationPhase.Failed) return;
                if (reply.Envelope.Result is null)
                {
                    RestoreText = RestoreStatus(_restoreOperation, false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch
        {
            if (!token.IsCancellationRequested && Current(connection, generation))
            {
                RestoreText = RestoreStatus(_restoreOperation, false);
                Message = "恢复进度暂未确认，正在等待原请求返回；不会重复提交恢复。";
            }
        }
    }

    private async Task RefreshRestoredServerAsync(IManagementConnection connection, long generation)
    {
        if (!Current(connection, generation)) return;
        if (_reads is { } previousRead) { previousRead.Cancel(); EndRead(previousRead); }
        try { await RefreshAsync(); }
        catch
        {
            if (Current(connection, generation))
            {
                MarkUnknown();
                Message = "恢复已完成；当前服务器状态查询未完成，请刷新状态。";
            }
        }
    }

    private static bool RestoreCompleted(ManagementOperation? operation) =>
        operation is { Kind: ManagementOperationKind.Restore, Phase: ManagementOperationPhase.Completed,
            Restore: { IsolationStaged: true, RecoveryVerified: true, BusinessReady: true },
            RecoveryActivation: RecoveryActivationPhase.BusinessReady };

    private static string RestoreStatus(ManagementOperation? operation, bool inProgress)
    {
        if (RestoreCompleted(operation)) return "恢复完毕，服务器已就绪";
        var activation = operation?.RecoveryActivation;
        var stage = operation?.Restore?.RecoveryVerified == true || activation is not null
            ? "恢复后业务状态检测"
            : operation?.Checks?.Any(c => c.Name == "recovery-working-copy" && c.Passed) == true ||
              operation?.Checks?.Any(c => c.Name.StartsWith("recovery-", StringComparison.Ordinal) && c.Name != "recovery-working-copy") == true
                ? "恢复条件核对" : "恢复副本准备";
        var failed = operation?.Phase == ManagementOperationPhase.Failed ||
            operation?.Checks?.Any(c => !c.Passed && c.BlocksWrites) == true ||
            activation is RecoveryActivationPhase.RollbackPending or RecoveryActivationPhase.RolledBack;
        if (failed) return stage + "未通过";
        return stage + (inProgress && operation?.Phase is not ManagementOperationPhase.NeedsVerification ? "中" : "未确认");
    }
}
