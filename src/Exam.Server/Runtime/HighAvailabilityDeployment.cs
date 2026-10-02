using Exam.Contracts.HighAvailability;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.HighAvailability;
using Exam.Infrastructure.ServiceManagement;

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
        var resources = FileRecoveryDeploymentSelection.ResolveResources(k => configuration[k], identity.ServerId, identity.NodeId, identity.ServerEpoch) ?? "";
        var connection = FileRecoveryDeploymentSelection.ResolveConnection(k => configuration[k], identity.ServerId, identity.NodeId, identity.ServerEpoch);
        var cert = configuration["PackageSecurity:CertificateThumbprint"] ?? "";
        var service = new ServiceConfiguration(identity.ServerId, identity.NodeId, "127.0.0.1", 1, resources, true, 1);
        var certificate = await new NodeCertificateStartupReadinessProbe(cert).RunAsync(service, context.RequestAborted);
        var keys = await new ConfiguredRecoveryReadinessProbe(connection, cert).RunAsync(service, context.RequestAborted);
        return new(identity.ServerId, identity.NodeId, identity.ServerEpoch, keys.Count > 0 && keys.All(c => c.Passed || !c.BlocksWrites),
            certificate.Count > 0 && certificate.All(c => c.Passed || !c.BlocksWrites), DateTimeOffset.UtcNow);
    }
}
