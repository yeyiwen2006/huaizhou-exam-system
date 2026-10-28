using Exam.Contracts.HighAvailability;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.HighAvailability;
using Exam.Infrastructure.ServiceManagement;
using Exam.Infrastructure.Security;

namespace Exam.Server.Runtime;

public static class HighAvailabilityDeployment
{
    public static void Apply(WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var serverId = configuration["ExamServer:ServerId"] ?? ""; var nodeId = configuration["ExamServer:NodeId"] ?? "";
        builder.Configuration.AddInMemoryCollection(
            FileFailoverDeploymentSelection.ReadConfigurationOverrides(k => configuration[k], serverId, nodeId));
    }

    public static async Task<HighAvailabilityMaterialProof> ReadMaterialsAsync(HttpContext context, IConfiguration configuration)
    {
        var identity = configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>() ?? new();
        var cert = new ServerCertificateSource(configuration["PackageSecurity:CertificateThumbprint"],
            configuration["PackageSecurity:CertificatePath"], configuration["PackageSecurity:CertificatePassword"]);
        var service = new ServiceConfiguration(identity.ServerId, identity.NodeId, "127.0.0.1", 1, "startup-snapshot", true, 1);
        var certificate = await new NodeCertificateStartupReadinessProbe(cert).RunAsync(service, context.RequestAborted);
        var startup = context.RequestServices.GetRequiredService<BusinessRecoveryGuard>().Snapshot;
        return new(identity.ServerId, identity.NodeId, identity.ServerEpoch, startup.MaterialsReady,
            certificate.Count > 0 && certificate.All(c => c.Passed || !c.BlocksWrites), DateTimeOffset.UtcNow)
        { ServiceConfiguration = context.RequestServices.GetService<BusinessServiceConfiguration>()?.Value };
    }
}
