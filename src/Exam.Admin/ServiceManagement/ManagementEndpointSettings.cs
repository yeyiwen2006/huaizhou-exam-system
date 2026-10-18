using System.IO;
using System.Text.Json;
using Exam.Contracts.Common;

namespace Exam.Admin.ServiceManagement;

public sealed class ManagementEndpointSettings(string path, string? deploymentPath = null)
{
    public ManagementTarget? Load()
    {
        if (!File.Exists(path))
            return deploymentPath is not null && File.Exists(deploymentPath) ? LoadFile(deploymentPath) : null;
        var saved = LoadFile(path);
        if (saved.PeerTarget is not null || deploymentPath is null || !File.Exists(deploymentPath)) return saved;
        // Upgrade an existing single-node user setting only when its exact trusted endpoint
        // is one of the endpoints in the deployment file. Unrelated targets keep their identity.
        ManagementTarget deployment;
        try { deployment = LoadFile(deploymentPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return saved; }
        if (deployment.ServerId != saved.ServerId) return saved;
        var matched = deployment.NodeId == saved.NodeId && deployment.Validate() == saved.Validate() ? deployment :
            deployment.PeerTarget is { } peer && peer.NodeId == saved.NodeId && peer.Validate() == saved.Validate() ? peer : null;
        return matched is null ? saved : saved with { PeerAddress = matched.PeerAddress, PeerNodeId = matched.PeerNodeId };
    }

    public static ManagementTarget LoadFile(string path)
    {
        if (new FileInfo(path).Length > 8192 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("管理连接配置无效。");
        var target = JsonSerializer.Deserialize<ManagementTarget>(File.ReadAllText(path), JsonDefaults.Create())
            ?? throw new InvalidDataException("管理连接配置为空。");
        if (string.IsNullOrWhiteSpace(target.Address) || string.IsNullOrWhiteSpace(target.ServerId) || string.IsNullOrWhiteSpace(target.NodeId))
            throw new InvalidDataException("管理连接配置缺少地址或服务器标识。");
        target = target with { Address = target.Address.Trim(), ServerId = target.ServerId.Trim(), NodeId = target.NodeId.Trim(),
            PeerAddress = string.IsNullOrWhiteSpace(target.PeerAddress) ? null : target.PeerAddress.Trim(),
            PeerNodeId = string.IsNullOrWhiteSpace(target.PeerNodeId) ? null : target.PeerNodeId.Trim() };
        target.Validate();
        return target;
    }
    public void Save(ManagementTarget target)
    {
        target.Validate();
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        for (var node = new DirectoryInfo(parent); node is not null; node = node.Parent)
            if (node.Exists && (node.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("管理连接目录不能经过链接。");
        Directory.CreateDirectory(parent);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("管理连接配置不能是链接。");
        var temporary = Path.Combine(parent, ".endpoint-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Connection files contain only public routing information, never credentials or imported extra fields.
            var fields = new Dictionary<string, string>
            { ["address"] = target.Address, ["serverId"] = target.ServerId, ["nodeId"] = target.NodeId };
            if (target.PeerTarget is { } peer)
            { fields["peerAddress"] = peer.Address; fields["peerNodeId"] = peer.NodeId; }
            File.WriteAllText(temporary, JsonSerializer.Serialize(fields, JsonDefaults.Create()));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
