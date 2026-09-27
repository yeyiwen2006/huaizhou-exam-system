using Exam.Application.ServiceManagement;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.Postgres;
using Exam.Infrastructure.Postgres.Resources;

namespace Exam.Infrastructure.ServiceManagement;

public sealed class ConfiguredRecoveryReadinessProbe(string? connectionString, string certificateThumbprint) : IStartupReadinessProbe
{
    public async Task<IReadOnlyList<StartupCheckResult>> RunAsync(ServiceConfiguration configuration, CancellationToken cancellationToken)
    {
        try
        {
            await using var source = new PostgresConnectionOptions(connectionString ?? "", CommandTimeoutSeconds: 10,
                MaximumPoolSize: 2, MinimumPoolSize: 0, ConnectionTimeoutSeconds: 3).CreateDataSource();
            return await new PostgresRecoveryReadinessProbe(source,
                new CertificateStorePackageKeyUnwrapper(configuration.NodeId, certificateThumbprint)).RunAsync(configuration, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return [new("recovery-snapshot", false, "业务恢复核对配置或依赖不可用。", true)]; }
    }
}
