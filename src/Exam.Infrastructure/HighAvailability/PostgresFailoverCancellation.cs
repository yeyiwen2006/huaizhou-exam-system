using Exam.Contracts.HighAvailability;
using Exam.Contracts.ServiceManagement;

namespace Exam.Infrastructure.HighAvailability;

public sealed partial class PostgresHighAvailabilityManagementPlatform
{
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
        if (actual.Value.Recovery && SameState(actual.Value.State, plan.Before)) return true;
        // Promotion is irreversible. Publish/select only the promotion already made;
        // this path cannot issue pg_promote or automatically switch the primary back.
        return !actual.Value.Recovery && await ReconcileAsync(plan, token);
    }
}
