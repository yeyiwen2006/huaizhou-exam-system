using Exam.Application.ServiceManagement;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.Postgres.Resources;
using Npgsql;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>Management observations reuse resource validation by its database revision.
/// Startup and actual restore continue to run the complete recovery probe.</summary>
internal sealed class ConfirmationMaterialReadinessProbe(NpgsqlDataSource source, IPackageKeyUnwrapper unwrapper) : IStartupReadinessProbe
{
    private readonly PostgresRecoveryReadinessProbe _probe = new(source, unwrapper);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<StartupCheckResult>> RunAsync(ServiceConfiguration configuration, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var ready = await _probe.VerifyConfirmationResourcesAsync(token);
            return [new("confirmation-materials", ready, ready ? "资源版本与解密材料已核实。" : "资源材料尚未核实。", true)];
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return [new("confirmation-materials", false, "资源材料读取失败。", true)]; }
        finally { _gate.Release(); }
    }
}
