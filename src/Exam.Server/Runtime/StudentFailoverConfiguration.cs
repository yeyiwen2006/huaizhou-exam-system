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
        if (!identity.IsValid() || nodes is null || nodes.Length is < 1 or > 2 || nodes.Any(node => node is null ||
                Uri.CheckHostName(node.Host) == UriHostNameType.Unknown || node.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(node.NodeId) ||
                node.CertificateSha256?.Length != 64 || !node.CertificateSha256.All(Uri.IsHexDigit)) ||
            nodes.Select(node => NormalizeHost(node.Host)).Distinct(StringComparer.Ordinal).Count() != nodes.Length ||
            nodes.Select(node => node.NodeId).Distinct(StringComparer.Ordinal).Count() != nodes.Length ||
            !nodes.Any(node => node.NodeId == identity.NodeId))
            throw new InvalidOperationException("可信节点须为一个节点或两个独立节点，地址、端口、身份及证书指纹有效，并包含本机。");
        var required = configuration.GetSection("HighAvailability:RequiredNodeIds").Get<string[]>() ?? [];
        if (required.Length != 0 && !required.Order(StringComparer.Ordinal).SequenceEqual(nodes.Select(node => node.NodeId).Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("学生端可信节点与受管节点不一致。");
        return new(identity.ServerId, nodes.Select(node => node with
        { Host = NormalizeHost(node.Host), CertificateSha256 = node.CertificateSha256.ToLowerInvariant() }).ToArray());
    }

    private static string NormalizeHost(string host) => IPAddress.TryParse(host, out var address)
        ? address.ToString() : host.TrimEnd('.').ToLowerInvariant();
}
