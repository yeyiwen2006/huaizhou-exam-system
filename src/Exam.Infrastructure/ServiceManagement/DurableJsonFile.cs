using System.Runtime.InteropServices;
using System.Text.Json;
using Exam.Contracts.Common;

namespace Exam.Infrastructure.ServiceManagement;

internal static class DurableJsonFile
{
    internal static async Task WriteAsync<T>(string path, T value, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        PathBoundary.RejectExistingReparsePoints(directory, path);
        var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, value, JsonDefaults.Create(), token);
                await output.FlushAsync(token); output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            if (OperatingSystem.IsWindows())
            {
                if (!MoveFileEx(temporary, path, 1 | 8))
                    throw new IOException("持久阶段记录替换失败。", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
            }
            else File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static T? Read<T>(string path, int maximumBytes = 1024 * 1024) where T : class
    {
        PathBoundary.RejectExistingReparsePoints(Path.GetDirectoryName(Path.GetFullPath(path))!, path);
        if (!File.Exists(path)) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length is <= 0 || input.Length > maximumBytes) throw new IOException("持久阶段记录大小无效。");
        var bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        var value = JsonSerializer.Deserialize<T>(bytes, JsonDefaults.Create()) ?? throw new IOException("持久阶段记录缺失。");
        if (!bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Create())))
            throw new IOException("持久阶段记录内容不规范。");
        return value;
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string target, int flags);
}
