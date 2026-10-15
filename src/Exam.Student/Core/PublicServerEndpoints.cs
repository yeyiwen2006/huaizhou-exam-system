namespace Exam.Student.Core;

/// <summary>Operator-provided HTTPS routes to the existing nodes; never learned from a response.</summary>
public sealed record PublicServerNode(string NodeId, string Host, int Port, string OriginHost, int OriginPort);

public sealed record PublicServerEndpoints(string ServerId, IReadOnlyList<PublicServerNode> Nodes)
{
    public bool Contains(Uri endpoint) => Nodes.Any(node =>
        new UriBuilder(Uri.UriSchemeHttps, node.Host, node.Port).Uri == endpoint);
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServerId) || Nodes is null || Nodes.Count != 2 ||
            Nodes.Any(node => node is null || string.IsNullOrWhiteSpace(node.NodeId) ||
                Uri.CheckHostName(node.Host) != UriHostNameType.Dns || node.Port is < 1 or > 65535 ||
                Uri.CheckHostName(node.OriginHost) == UriHostNameType.Unknown || node.OriginPort is < 1 or > 65535) ||
            Nodes.Select(node => node.NodeId).Distinct(StringComparer.Ordinal).Count() != 2 ||
            Nodes.Select(node => DeviceStore.NormalizeHost(node.Host)).Distinct().Count() != 2 ||
            Nodes.Select(node => DeviceStore.NormalizeHost(node.OriginHost)).Distinct().Count() != 2)
            throw new FormatException("公网连接配置须明确指定同一主备环境的两个节点和各自的 HTTPS 域名。");
    }

    public TrustedServerTopology Apply(TrustedServerTopology topology, DeviceRecord record)
    {
        Validate();
        var endpoint = new UriBuilder(Uri.UriSchemeHttps, record.Host, record.Port).Uri;
        if (!Contains(endpoint))
            return topology;
        if (topology.ServerId != ServerId || Nodes.Any(route => !topology.Nodes.Any(node =>
                node.NodeId == route.NodeId && node.Address ==
                new UriBuilder(Uri.UriSchemeHttps, route.OriginHost, route.OriginPort).Uri)))
            throw new StudentIdentityException("公网入口返回的主备身份与部署配置不一致，原登记已保留。");
        return topology with
        {
            Nodes = topology.Nodes.Select(node =>
            {
                var route = Nodes.Single(item => item.NodeId == node.NodeId);
                return node with
                {
                    Host = DeviceStore.NormalizeHost(route.Host), Port = route.Port,
                    OriginHost = node.Host, OriginPort = node.Port
                };
            }).ToArray()
        };
    }
}
