using System.Net;
using Exam.Contracts.Activation;

namespace Exam.Server.Runtime;

/// <summary>Only operator-controlled deployment configuration can distribute trusted peers.</summary>
public sealed class StudentFailoverConfiguration
{
    public ActivatedServerNode[] Nodes { get; init; } = [];

    public static ActivatedServerTopology? Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("StudentFailover");
        if (!section.Exists()) return null;
        var options = section.Get<StudentFailoverConfiguration>() ?? throw new InvalidOperationException("主备信任配置缺失。");
        var identity = configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>() ?? new();
        var nodes = options.Nodes;
        if (!identity.IsValid() || nodes is null || nodes.Length != 2 || nodes.Any(node => node is null ||
                !IPAddress.TryParse(node.Host, out _) || node.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(node.NodeId) ||
                node.CertificateSha256?.Length != 64 || !node.CertificateSha256.All(Uri.IsHexDigit)) ||
            nodes.Select(node => IPAddress.Parse(node.Host).ToString()).Distinct(StringComparer.Ordinal).Count() != 2 ||
            nodes.Select(node => node.NodeId).Distinct(StringComparer.Ordinal).Count() != 2 ||
            !nodes.Any(node => node.NodeId == identity.NodeId))
            throw new InvalidOperationException("主备信任配置须包含两个独立 IP、有效端口、节点标识与证书 SHA-256 指纹，并包含本机节点。");
        var required = configuration.GetSection("HighAvailability:RequiredNodeIds").Get<string[]>() ?? [];
        if (required.Length != 0 && !required.Order(StringComparer.Ordinal).SequenceEqual(nodes.Select(node => node.NodeId).Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("学生端可信节点与双机确认节点不一致。");
        return new(identity.ServerId, nodes.Select(node => node with
        { Host = IPAddress.Parse(node.Host).ToString(), CertificateSha256 = node.CertificateSha256.ToLowerInvariant() }).ToArray());
    }
}
