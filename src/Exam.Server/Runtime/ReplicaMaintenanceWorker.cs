using Exam.Application.HighAvailability;

namespace Exam.Server.Runtime;

/// <summary>Retries replication even when no candidate or operator is sending requests.</summary>
public sealed class ReplicaMaintenanceWorker(IBusinessConfirmation confirmation, ActiveNodeGuard active,
    BusinessRecoveryGuard recovery, NodeAuthorityGuard authority) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await authority.CheckAsync(stoppingToken)) return;
                if (recovery.Snapshot.Ready && (await active.CheckAsync(stoppingToken)).Allowed &&
                    confirmation is IBusinessReplicationMaintenance maintenance)
                    await maintenance.MaintainReplicationAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { /* Local confirmation and the management status retain the degraded state. */ }
            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
