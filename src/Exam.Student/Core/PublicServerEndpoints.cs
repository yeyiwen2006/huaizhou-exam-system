namespace Exam.Student.Core;

/// <summary>Operator-provided HTTPS routes to the existing nodes; never learned from a response.</summary>
public sealed record PublicServerNode(string NodeId, string Host, int Port, string OriginHost, int OriginPort);

public sealed record PublicServerEndpoints(string ServerId, IReadOnlyList<PublicServerNode> Nodes)
{
    public bool Contains(Uri endpoint) => Nodes.Any(node =>
        new UriBuilder(Uri.UriSchemeHttps, node.Host, node.Port).Uri == endpoint);
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServerId) || Nodes is null || Nodes.Count is < 1 or > 2 ||
            Nodes.Any(node => node is null || string.IsNullOrWhiteSpace(node.NodeId) ||
                Uri.CheckHostName(node.Host) != UriHostNameType.Dns || node.Port is < 1 or > 65535 ||
                Uri.CheckHostName(node.OriginHost) == UriHostNameType.Unknown || node.OriginPort is < 1 or > 65535) ||
            Nodes.Select(node => node.NodeId).Distinct(StringComparer.Ordinal).Count() != Nodes.Count ||
            Nodes.Select(node => DeviceStore.NormalizeHost(node.Host)).Distinct().Count() != Nodes.Count ||
            Nodes.Select(node => DeviceStore.NormalizeHost(node.OriginHost)).Distinct().Count() != Nodes.Count)
            throw new FormatException("公网连接配置须明确指定同一环境的一至两个节点，以及各自的 HTTPS 域名和原节点地址。");
    }

    public TrustedServerTopology Apply(TrustedServerTopology topology, DeviceRecord record)
    {
        Validate();
        var endpoint = new UriBuilder(Uri.UriSchemeHttps, record.Host, record.Port).Uri;
        var selected = Nodes.SingleOrDefault(route =>
            new UriBuilder(Uri.UriSchemeHttps, route.Host, route.Port).Uri == endpoint);
        if (selected is null)
            return topology;
        if (topology.ServerId != ServerId || !topology.Nodes.Any(node => node.NodeId == selected.NodeId) ||
            topology.Nodes.Any(node => Nodes.Any(route => node.NodeId == route.NodeId && node.Address !=
                new UriBuilder(Uri.UriSchemeHttps, route.OriginHost, route.OriginPort).Uri)))
            throw new StudentIdentityException("公网入口返回的主备身份与部署配置不一致，原登记已保留。");
        return topology with
        {
            Nodes = topology.Nodes.Select(node =>
            {
                var route = Nodes.SingleOrDefault(item => item.NodeId == node.NodeId);
                // A missing public route does not revoke an already approved peer.
                if (route is null) return node;
                return node with
                {
                    Host = DeviceStore.NormalizeHost(route.Host), Port = route.Port,
                    OriginHost = node.Host, OriginPort = node.Port
                };
            }).ToArray()
        };
    }

    internal TrustedServerTopology RebindSavedRoutes(TrustedServerTopology topology)
    {
        Validate();
        if (topology.ServerId != ServerId || topology.Nodes.Any(node => Nodes.Any(route =>
                route.NodeId == node.NodeId && new UriBuilder(Uri.UriSchemeHttps, route.OriginHost, route.OriginPort).Uri !=
                (node.OriginAddress ?? node.Address))))
            throw new StudentIdentityException("新的公网部署配置与原节点身份不一致，原设备登记已保留。");
        return topology with { Nodes = topology.Nodes.Select(node =>
        {
            var route = Nodes.SingleOrDefault(item => item.NodeId == node.NodeId);
            if (route is null) return node;
            return node with
            {
                Host = DeviceStore.NormalizeHost(route.Host), Port = route.Port,
                OriginHost = node.OriginHost ?? node.Host, OriginPort = node.OriginPort ?? node.Port
            };
        }).ToArray() };
    }
}
