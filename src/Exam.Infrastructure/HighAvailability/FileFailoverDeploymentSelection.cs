using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Infrastructure.HighAvailability;

public sealed record FailoverDeploymentSelection(string ServerId, string NodeId, string ActiveNodeId,
    string OtherNodeId, long Epoch, long StateVersion, ProtectionMode Mode, string RequestId,
    string ConfirmationDirectory);

/// <summary>Protected deployment selection. PostgreSQL still verifies this identity before business writes.</summary>
public sealed class FileFailoverDeploymentSelection(string path)
{
    private string FullPath()
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("主备部署选择路径须为绝对路径。");
        var full = Path.GetFullPath(path);
        PathBoundary.RejectExistingReparsePoints(Path.GetDirectoryName(full)!, full);
        return full;
    }
    public FailoverDeploymentSelection? Read()
    {
        var full = FullPath();
        if (!File.Exists(full)) return null;
        using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length is <= 0 or > 16384) throw new IOException("主备部署选择记录大小无效。");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        var value = JsonSerializer.Deserialize<FailoverDeploymentSelection>(bytes, JsonDefaults.Create())
            ?? throw new IOException("主备部署选择记录不完整。");
        Validate(value);
        if (!bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Create())))
            throw new IOException("主备部署选择记录字段或内容不规范。");
        return value;
    }
    public async Task SaveAsync(FailoverDeploymentSelection value, CancellationToken token)
    {
        Validate(value); var full = FullPath();
        var previous = Read();
        if (previous is not null && (previous.ServerId != value.ServerId || previous.NodeId != value.NodeId ||
            previous.Epoch > value.Epoch || previous.StateVersion > value.StateVersion ||
            previous.Epoch == value.Epoch && previous.StateVersion == value.StateVersion && previous != value))
            throw new IOException("主备部署选择身份变化或版本回退，禁止覆盖。");
        var directory = Path.GetDirectoryName(full)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("主备部署选择目录须由部署人员预建。");
        PathBoundary.RejectExistingReparsePoints(value.ConfirmationDirectory, value.ConfirmationDirectory);
        Directory.CreateDirectory(value.ConfirmationDirectory);
        var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            { await JsonSerializer.SerializeAsync(output, value, JsonDefaults.Create(), token); await output.FlushAsync(token); output.Flush(true); }
            token.ThrowIfCancellationRequested(); FullPath(); File.Move(temporary, full, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Validate(FailoverDeploymentSelection value)
    {
        if (new[] { value.ServerId, value.NodeId, value.ActiveNodeId, value.OtherNodeId, value.RequestId }
                .Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 200) || value.NodeId == value.OtherNodeId ||
            value.ActiveNodeId != value.NodeId && value.ActiveNodeId != value.OtherNodeId ||
            value.Epoch <= 0 || value.StateVersion <= 0 || !Enum.IsDefined(value.Mode) ||
            !Path.IsPathFullyQualified(value.ConfirmationDirectory))
            throw new IOException("主备部署选择记录身份、代次或目录无效。");
    }

    public static FailoverDeploymentSelection? ReadConfigured(Func<string, string?> configuration,
        string serverId, string nodeId)
    {
        var path = configuration("HighAvailability:SelectionPath");
        if (string.IsNullOrWhiteSpace(path)) return null;
        var value = new FileFailoverDeploymentSelection(path).Read();
        if (value is not null && (value.ServerId != serverId || value.NodeId != nodeId))
            throw new IOException("主备部署选择与受保护节点身份不符。");
        if (value is not null)
        {
            var root = configuration($"HighAvailability:Nodes:{nodeId}:ConfirmationDirectory");
            if (string.IsNullOrWhiteSpace(root)) throw new IOException("持久部署选择缺少节点确认根目录配置。");
            if (!Path.IsPathFullyQualified(root)) throw new IOException("节点确认根目录须为绝对路径。");
            var expected = Path.Combine(root, "epochs", value.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture), nodeId);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!Path.GetFullPath(value.ConfirmationDirectory).Equals(Path.GetFullPath(expected), comparison))
                throw new IOException("持久部署选择的确认目录与受保护节点配置不一致。");
            PathBoundary.ResolveWithinRoot(root, expected, "持久部署确认目录超出节点根目录。");
        }
        return value;
    }
}
