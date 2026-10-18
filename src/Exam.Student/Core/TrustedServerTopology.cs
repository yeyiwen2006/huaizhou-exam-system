using System.Net;
using Exam.Contracts.Activation;

namespace Exam.Student.Core;

public sealed record TrustedServerNode(string Host, int Port, string NodeId, string CertificateSha256)
{
    public string? OriginHost { get; init; }
    public int? OriginPort { get; init; }
    public string? PublicCertificateSha256 { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool UsesPublicHttps => OriginHost is not null;
    [System.Text.Json.Serialization.JsonIgnore]
    public Uri? OriginAddress => OriginHost is null ? null : new UriBuilder(Uri.UriSchemeHttps, OriginHost, OriginPort!.Value).Uri;
    public Uri Address => new UriBuilder(Uri.UriSchemeHttps, Host, Port).Uri;
}

/// <summary>Allow-list received through the trusted activation connection and saved with the device identity.</summary>
public sealed record TrustedServerTopology(string ServerId, IReadOnlyList<TrustedServerNode> Nodes)
{
    public static TrustedServerTopology? FromActivation(ActivatedServerTopology? value, DeviceRecord record, string serverId,
        PublicServerEndpoints? publicEndpoints = null, string? connectedNodeId = null)
    {
        if (value is null) return record.Topology;
        if (value.Nodes is null || value.Nodes.Any(node => node is null))
            throw new StudentIdentityException("主备信任信息不完整，不能替换设备登记。");
        var topology = new TrustedServerTopology(value.ServerId, value.Nodes.Select(node =>
            new TrustedServerNode(node.Host, node.Port, node.NodeId,
                ServerEndpointConfig.NormalizeThumbprint(node.CertificateSha256) ?? node.CertificateSha256)).ToArray());
        try { topology.Validate(); }
        catch (FormatException) { throw new StudentIdentityException("主备信任信息无效，原设备登记已保留。"); }
        topology = topology with { Nodes = topology.Nodes.Select(node =>
            node with { Host = DeviceStore.NormalizeHost(node.Host) }).ToArray() };
        if (connectedNodeId is not null && !topology.Nodes.Any(node => node.NodeId == connectedNodeId))
            throw new StudentIdentityException("登录响应的节点不在已核对的主备信息中，原登记已保留。");
        if (publicEndpoints is not null) topology = publicEndpoints.Apply(topology, record);
        var registeredAddress = new UriBuilder(Uri.UriSchemeHttps, record.Host, record.Port).Uri;
        if (record.Topology is { } saved)
        {
            // Public routes are already trusted local state. A response containing the origin
            // addresses must not erase them when the deployment file is absent or refreshed.
            topology = topology with { Nodes = topology.Nodes.Select(node =>
            {
                var previous = saved.Nodes.SingleOrDefault(item => item.NodeId == node.NodeId);
                return previous is { UsesPublicHttps: true } && (previous.OriginAddress == node.Address ||
                    previous.Address == node.Address && previous.OriginAddress == node.OriginAddress) &&
                    previous.CertificateSha256 == node.CertificateSha256 ? previous : node;
            }).ToArray() };
            if (topology.ServerId != saved.ServerId || topology.Nodes.Any(node =>
                    saved.Nodes.Any(previous => (previous.NodeId == node.NodeId || previous.Address == node.Address) &&
                        !SameNode(previous, node))))
                throw new StudentIdentityException("主备节点身份或证书与原登记不一致，原记录已保留。");
            // A temporarily single-node deployment need not remove an already trusted peer.
            topology = topology with { Nodes = saved.Nodes.Concat(topology.Nodes.Where(node =>
                !saved.Nodes.Any(previous => previous.NodeId == node.NodeId))).ToArray() };
        }
        else if (!topology.Nodes.Any(node => node.Address == registeredAddress) &&
            connectedNodeId is not null &&
            Uri.CheckHostName(record.Host) == UriHostNameType.Dns)
        {
            // Only the user-selected endpoint of this successful TLS connection may
            // be an alias. Never learn another public route from a server response.
            topology = topology with { Nodes = topology.Nodes.Select(node => node.NodeId == connectedNodeId
                ? node with { Host = DeviceStore.NormalizeHost(record.Host), Port = record.Port,
                    OriginHost = node.Host, OriginPort = node.Port,
                    PublicCertificateSha256 = ServerTrust.PinnedCertificateThumbprint }
                : node).ToArray() };
        }
        try { topology.Validate(); }
        catch (FormatException) { throw new StudentIdentityException("主备信任信息冲突，原设备登记已保留。"); }
        if (topology.ServerId != serverId ||
            !topology.Nodes.Any(node => node.Address == registeredAddress) ||
            record.Topology is { } originalTopology && !topology.Extends(originalTopology))
            throw new StudentIdentityException("批准的主备信任信息与原设备登记不一致，原记录已保留。");
        var original = topology.Nodes.Single(node => node.Address == registeredAddress);
        if (record.Topology is null && !original.UsesPublicHttps &&
            ServerTrust.PinnedCertificateThumbprint is { } pin && original.CertificateSha256 != pin)
            throw new StudentIdentityException("批准的证书指纹与已核对的激活连接不一致，原记录已保留。");
        return topology;
    }

    // An authenticated existing node may add the second pre-approved endpoint; existing pins and identities stay fixed.
    public bool Extends(TrustedServerTopology other) => ServerId == other.ServerId &&
        other.Nodes.All(node => Nodes.Any(candidate => SameNode(candidate, node)));

    public bool Equivalent(TrustedServerTopology other) => Nodes.Count == other.Nodes.Count && Extends(other);

    private static bool SameNode(TrustedServerNode left, TrustedServerNode right) =>
        left.NodeId == right.NodeId && left.Address == right.Address &&
        left.CertificateSha256 == right.CertificateSha256 && left.OriginAddress == right.OriginAddress &&
        left.PublicCertificateSha256 == right.PublicCertificateSha256;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServerId) || Nodes is null || Nodes.Count is < 1 or > 2 ||
            Nodes.Any(node => node is null || Uri.CheckHostName(node.Host) == UriHostNameType.Unknown || node.Port is < 1 or > 65535 ||
                string.IsNullOrWhiteSpace(node.NodeId) || node.CertificateSha256 is null ||
                ServerEndpointConfig.NormalizeThumbprint(node.CertificateSha256) != node.CertificateSha256 ||
                node.PublicCertificateSha256 is { } publicPin && (!node.UsesPublicHttps ||
                    ServerEndpointConfig.NormalizeThumbprint(publicPin) != publicPin) ||
                (node.OriginHost is null) != (node.OriginPort is null) ||
                node.UsesPublicHttps && (Uri.CheckHostName(node.Host) != UriHostNameType.Dns ||
                    Uri.CheckHostName(node.OriginHost) == UriHostNameType.Unknown || node.OriginPort is < 1 or > 65535)) ||
            Nodes.Select(node => node.Address).Distinct().Count() != Nodes.Count ||
            Nodes.Select(node => DeviceStore.NormalizeHost(node.Host)).Distinct().Count() != Nodes.Count ||
            Nodes.Select(node => node.NodeId).Distinct(StringComparer.Ordinal).Count() != Nodes.Count)
            throw new FormatException("主备可信配置无效或地址/节点重复。");
    }
}
