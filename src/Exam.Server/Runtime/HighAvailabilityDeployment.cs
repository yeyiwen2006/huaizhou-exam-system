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
        var selection = FileFailoverDeploymentSelection.ReadConfigured(k => configuration[k], serverId, nodeId);
        if (selection is null) return;
        string Node(string id, string key) => configuration[$"HighAvailability:Nodes:{id}:{key}"]
            ?? throw new IOException("主备部署选择所引用的受保护节点配置不完整。");
        _ = Node(nodeId, "ConfirmationDirectory");
        _ = Node(selection.OtherNodeId, "ConnectionString");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["ExamServer:ServerEpoch"] = selection.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ConnectionStrings:Exam"] = Node(nodeId, "ConnectionString"),
            ["Backup:VersionedResourcesDirectory"] = Node(nodeId, "ResourcesDirectory"),
            ["PackageSecurity:CertificateThumbprint"] = Node(nodeId, "CertificateThumbprint"),
            ["Confirmation:Directory"] = selection.ConfirmationDirectory,
            ["Confirmation:PrimaryNodeId"] = selection.ActiveNodeId,
            ["Confirmation:StandbyNodeId"] = selection.ActiveNodeId == nodeId ? selection.OtherNodeId : nodeId,
            ["Confirmation:StandbyHttpsUrl"] = Node(selection.ActiveNodeId == nodeId ? selection.OtherNodeId : nodeId, "BusinessHttpsUrl") });
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
