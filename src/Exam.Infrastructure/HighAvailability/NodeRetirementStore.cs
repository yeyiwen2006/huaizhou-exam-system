using System.Text.Json;
using System.Runtime.InteropServices;
using Exam.Contracts.Common;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Infrastructure.HighAvailability;

public sealed record RetiredNodeEpoch(string ServerId, string NodeId, long Epoch, string ActiveNodeId, long ActiveEpoch);

public sealed class NodeRetirementStore(string directory)
{
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
