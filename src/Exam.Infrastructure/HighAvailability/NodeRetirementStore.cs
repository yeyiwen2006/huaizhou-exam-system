using System.Text.Json;
using System.Runtime.InteropServices;
using Exam.Contracts.Common;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Infrastructure.HighAvailability;

public sealed record RetiredNodeEpoch(string ServerId, string NodeId, long Epoch, string ActiveNodeId, long ActiveEpoch);

public sealed class NodeRetirementStore(string directory)
{
    // Retirement belongs to the node across role changes. Older deployments placed
    // it beside the selected epoch's confirmation head; keep reading that evidence.
    public static string? ResolveDirectory(Func<string, string?> configuration, string nodeId) =>
        configuration($"HighAvailability:Nodes:{nodeId}:ConfirmationDirectory") is { Length: > 0 } root
            ? root : configuration("Confirmation:Directory");

    public static RetiredNodeEpoch? ReadConfigured(Func<string, string?> configuration, string nodeId) =>
        ReadLatest(ResolveDirectory(configuration, nodeId), configuration("Confirmation:Directory"));

    public static RetiredNodeEpoch? ReadLatest(string? directory, string? previousDirectory = null)
    {
        RetiredNodeEpoch? latest = null;
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var path in new[] { directory, previousDirectory }.Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(comparison))
        {
            var current = new NodeRetirementStore(path!).Read();
            if (current is null) continue;
            if (latest is not null && (latest.ServerId != current.ServerId || latest.NodeId != current.NodeId ||
                latest.ActiveEpoch == current.ActiveEpoch && latest.ActiveNodeId != current.ActiveNodeId))
                throw new IOException("旧节点隔离记录的身份或活动代次冲突。");
            if (latest is null || current.ActiveEpoch > latest.ActiveEpoch ||
                current.ActiveEpoch == latest.ActiveEpoch && current.Epoch > latest.Epoch) latest = current;
        }
        return latest;
    }

    private string PathName
    {
        get
        {
            if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
                throw new IOException("旧节点隔离记录目录不可用。");
            var path = Path.Combine(directory, "retired-epoch.json");
            PathBoundary.RejectExistingReparsePoints(directory, path);
            return path;
        }
    }

    public RetiredNodeEpoch? Read()
    {
        var path = PathName;
        if (!File.Exists(path)) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length is <= 0 or > 16384) throw new IOException("旧节点隔离记录无效。");
        var result = JsonSerializer.Deserialize<RetiredNodeEpoch>(input, JsonDefaults.Create())
            ?? throw new IOException("旧节点隔离记录缺失。");
        if (new[] { result.ServerId, result.NodeId, result.ActiveNodeId }.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 200) ||
            result.NodeId == result.ActiveNodeId || result.Epoch <= 0 || result.ActiveEpoch <= result.Epoch)
            throw new IOException("旧节点隔离身份无效。");
        return result;
    }

    public async Task SaveAsync(RetiredNodeEpoch value, CancellationToken token)
    {
        var path = PathName;
        var previous = Read();
        if (previous is not null && (previous.ServerId != value.ServerId || previous.NodeId != value.NodeId ||
            value.ActiveEpoch < previous.ActiveEpoch)) throw new IOException("旧节点隔离记录禁止回退。");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, value, JsonDefaults.Create(), token);
                await output.FlushAsync(token); output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows())
            {
                if (!MoveFileEx(temporary, path, 1 | 8)) throw new IOException("旧节点隔离记录持久替换失败。");
            }
            else File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, int flags);
}
