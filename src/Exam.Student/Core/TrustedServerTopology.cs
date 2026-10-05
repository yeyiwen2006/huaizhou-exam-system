using System.Net;
using Exam.Contracts.Activation;

namespace Exam.Student.Core;

public sealed record TrustedServerNode(string Host, int Port, string NodeId, string CertificateSha256)
{
    public Uri Address => new UriBuilder(Uri.UriSchemeHttps, Host, Port).Uri;
}

/// <summary>Allow-list received through the trusted activation connection and saved with the device identity.</summary>
public sealed record TrustedServerTopology(string ServerId, IReadOnlyList<TrustedServerNode> Nodes)
{
    public static TrustedServerTopology? FromActivation(ActivatedServerTopology? value, DeviceRecord record, string serverId)
    {
        if (value is null) return record.Topology;
        if (value.Nodes is null || value.Nodes.Any(node => node is null))
            throw new StudentIdentityException("主备信任信息不完整，不能替换设备登记。");
        var topology = new TrustedServerTopology(value.ServerId, value.Nodes.Select(node =>
            new TrustedServerNode(node.Host, node.Port, node.NodeId, node.CertificateSha256)).ToArray());
        try { topology.Validate(); }
        catch (FormatException) { throw new StudentIdentityException("主备信任信息无效，原设备登记已保留。"); }
        if (topology.ServerId != serverId ||
            !topology.Nodes.Any(node => node.Address == new UriBuilder(Uri.UriSchemeHttps, record.Host, record.Port).Uri) ||
            record.Topology is { } previous && !topology.Equivalent(previous))
            throw new StudentIdentityException("批准的主备信任信息与原设备登记不一致，原记录已保留。");
        var original = topology.Nodes.Single(node => node.Address == new UriBuilder(Uri.UriSchemeHttps, record.Host, record.Port).Uri);
        if (record.Topology is null && ServerTrust.PinnedCertificateThumbprint is { } pin && original.CertificateSha256 != pin)
            throw new StudentIdentityException("批准的证书指纹与已核对的激活连接不一致，原记录已保留。");
        return topology;
    }

    public bool Equivalent(TrustedServerTopology other) => ServerId == other.ServerId &&
        Nodes.OrderBy(node => node.NodeId, StringComparer.Ordinal).SequenceEqual(other.Nodes.OrderBy(node => node.NodeId, StringComparer.Ordinal));

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServerId) || Nodes is null || Nodes.Count != 2 ||
            Nodes.Any(node => node is null || !IPAddress.TryParse(node.Host, out _) || node.Port is < 1 or > 65535 ||
                string.IsNullOrWhiteSpace(node.NodeId) || node.CertificateSha256 is null ||
                ServerEndpointConfig.NormalizeThumbprint(node.CertificateSha256) != node.CertificateSha256) ||
            Nodes.Select(node => node.Address).Distinct().Count() != 2 ||
            Nodes.Select(node => IPAddress.Parse(node.Host).ToString()).Distinct().Count() != 2 ||
            Nodes.Select(node => node.NodeId).Distinct(StringComparer.Ordinal).Count() != 2)
            throw new FormatException("主备可信配置无效或地址/节点重复。");
    }
}
