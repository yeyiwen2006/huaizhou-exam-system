using Exam.Contracts.HighAvailability;
using Exam.Contracts.ServiceManagement;

namespace Exam.Infrastructure.HighAvailability;

public sealed partial class PostgresHighAvailabilityManagementPlatform
{
    public async Task<bool> IsChangeCommittedAsync(FailoverExecutionPlan plan, CancellationToken token)
    {
        if (plan.Kind == ManagementOperationKind.Rebuild)
            return HasRebuildCompletion(plan) && await VerifyRebuiltLocalAsync(plan, token);
        if (plan.Kind != ManagementOperationKind.Takeover || plan.After.ActiveNodeId != Local.NodeId) return false;
        var selected = _readSelection();
        if (selected is null || selected.ServerId != plan.After.ServerId ||
            selected.Epoch < plan.After.ServerEpoch) return false;
        var actual = await ReadAsync(Local, token);
        return actual is not null && actual.Value.System == plan.SystemIdentifier &&
            actual.Value.Database == plan.DatabaseName && FailoverStateHistory.Covers(actual.Value.State, plan.After) &&
            (actual.Value.State.ServerEpoch > plan.After.ServerEpoch ||
                !actual.Value.Recovery && selected.ActiveNodeId == Local.NodeId);
    }

    public async Task<bool> CancelAsync(FailoverExecutionPlan plan, CancellationToken token)
    {
        if (plan.Kind == ManagementOperationKind.Rebuild)
        {
            if (await VerifyFailedBeforeInstallationAsync(plan, token)) return true;
            // A directory swap already in progress is finished in place. Never restore
            // a writable old primary or launch a fresh backup while canceling.
            return await ReconcileAsync(plan, token);
        }
        if (plan.Kind != ManagementOperationKind.Takeover) return false;
        var actual = await ReadAsync(Local, token);
        if (actual is null || actual.Value.System != plan.SystemIdentifier || actual.Value.Database != plan.DatabaseName)
            return false;
        if (actual.Value.Recovery && SameState(actual.Value.State, plan.Before))
        {
            // Preparation paused this read-only replica's confirmation listener.
            // Canceling before promotion must leave it available as the same standby.
            try { await _business.StartAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            { try { _recordProbeFailure?.Invoke("Exam.Management.Takeover.StandbyListenerRestoreFailed", error); } catch { } }
            return true;
        }
        // Promotion is irreversible. Publish/select only the promotion already made;
        // this path cannot issue pg_promote or automatically switch the primary back.
        return false; // A promoted primary cannot be reported as a canceled takeover.
    }
}
