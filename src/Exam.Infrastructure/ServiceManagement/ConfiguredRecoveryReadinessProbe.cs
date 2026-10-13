using Exam.Application.ServiceManagement;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.Postgres;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;

namespace Exam.Infrastructure.ServiceManagement;

public sealed class ConfiguredRecoveryReadinessProbe(string? connectionString, ServerCertificateSource certificateSource) : IStartupReadinessProbe
{
    public ConfiguredRecoveryReadinessProbe(string? connectionString, string certificateThumbprint)
        : this(connectionString, new ServerCertificateSource(certificateThumbprint)) { }

    public async Task<IReadOnlyList<StartupCheckResult>> RunAsync(ServiceConfiguration configuration, CancellationToken cancellationToken)
    {
        try
        {
            await using var source = ManagementProbeConnection.Create(connectionString ?? "", 10);
            return await new PostgresRecoveryReadinessProbe(source,
                new CertificateStorePackageKeyUnwrapper(configuration.NodeId, certificateSource)).RunAsync(configuration, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return [new("recovery-snapshot", false, "业务恢复核对配置或依赖不可用。", true)]; }
    }
}
