using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;

namespace Exam.Application.ServiceManagement;

public interface IRecoveryDeploymentSwitch
{
    Task SelectAsync(ManagementOperation operation, CancellationToken token);
    Task RollbackAsync(ManagementOperation operation, CancellationToken token);
}

/// <summary>Each mutation has a durable intent; rollback never automatically starts the original service.</summary>
public sealed class RecoveryActivationRunner(IManagedBusinessHost process, Func<IManagedBusinessHost> readyHost,
    IRecoveryDeploymentSwitch deployment, IManagementOperationStore journal)
{
    public async Task<ManagementOperation> ActivateAsync(ManagementOperation operation, CancellationToken token)
    {
        if (operation.Kind != ManagementOperationKind.Restore || operation.Restore is not
            { IsolationStaged: true, RecoveryVerified: true, BusinessReady: false } ||
            operation.RecoveryWorkingDirectory is null || process.Status != BusinessServiceStatus.Stopped)
            throw new InvalidOperationException("尚未具备恢复业务的核对条件。");
        var progress = operation;
        async Task Record(RecoveryActivationPhase phase)
        {
            progress = progress with { Phase = ManagementOperationPhase.NeedsVerification,
                RecoveryActivation = phase, Error = null, UpdatedAt = DateTimeOffset.UtcNow };
            await journal.SaveAsync(progress, CancellationToken.None);
        }
        try
        {
            token.ThrowIfCancellationRequested();
            await Record(RecoveryActivationPhase.SelectionPending);
            await deployment.SelectAsync(progress, token);
            await Record(RecoveryActivationPhase.Selected);
            if (process.Status != BusinessServiceStatus.Stopped) throw new IOException("部署切换期间业务宿主状态改变。");
            await Record(RecoveryActivationPhase.StartingBusiness);
            var host = readyHost();
            await host.StartAsync(token);
            token.ThrowIfCancellationRequested();
            if (process.Status != BusinessServiceStatus.Running || host.Status != BusinessServiceStatus.Running)
                throw new IOException("恢复业务尚未就绪。");
            return progress with { Phase = ManagementOperationPhase.Completed, RecoveryActivation = RecoveryActivationPhase.BusinessReady,
                Restore = operation.Restore with { BusinessReady = true }, Error = null };
        }
        catch (Exception)
        {
            try
            {
                await process.StopAsync(CancellationToken.None);
                if (process.Status != BusinessServiceStatus.Stopped) throw new IOException("不能核实业务已停止。");
                await Record(RecoveryActivationPhase.RollbackPending);
                await deployment.RollbackAsync(progress, CancellationToken.None);
                await Record(RecoveryActivationPhase.RolledBack);
                return progress with { Phase = ManagementOperationPhase.Staged,
                    Error = new(ErrorCodes.ServiceRecovering, "恢复业务未完成，已停止业务并恢复原部署引用；原库没有自动启动。") };
            }
            catch (Exception)
            {
                throw new IOException("恢复业务结果或回退未能核实，保留原操作阶段，不得自动重试。");
            }
        }
    }
}
