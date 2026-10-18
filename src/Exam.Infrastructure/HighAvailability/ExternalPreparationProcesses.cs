using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Exam.Infrastructure.HighAvailability;

internal static class ExternalPreparationProcesses
{
    internal static bool IsRunning(string binaryDirectory, string workingDirectory)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var name in new[] { "pg_basebackup", "pg_verifybackup" })
        foreach (var process in Process.GetProcessesByName(name))
        using (process)
        {
            try
            {
                if (process.HasExited) continue;
                var expected = Path.Combine(binaryDirectory, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
                if (!string.Equals(process.MainModule?.FileName, expected, comparison)) continue;
                foreach (var argument in Arguments(process.Id))
                {
                    if (!Path.IsPathFullyQualified(argument)) continue;
                    var path = Path.GetFullPath(argument).TrimEnd(Path.DirectorySeparatorChar);
                    if (path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison)) return true;
                }
            }
            catch (InvalidOperationException) when (process.HasExited) { }
            catch (Exception error) { throw new IOException("无法核实原重建外部进程已经终止。", error); }
        }
        return false;
    }

    private static string[] Arguments(int id)
    {
        if (!OperatingSystem.IsWindows())
            return Encoding.UTF8.GetString(File.ReadAllBytes($"/proc/{id}/cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        using var handle = OpenProcess(0x1000, false, id);
        if (handle.IsInvalid) throw new IOException("无法读取重建进程身份。");
        _ = NtQueryInformationProcess(handle, 60, IntPtr.Zero, 0, out var size);
        if (size <= 0 || size > 1024 * 1024) throw new IOException("无法读取重建进程目标。");
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryInformationProcess(handle, 60, buffer, size, out _) != 0) throw new IOException("无法读取重建进程目标。");
            var text = Marshal.PtrToStructure<UnicodeString>(buffer);
            var command = Marshal.PtrToStringUni(text.Buffer, text.Length / 2) ?? throw new IOException("重建进程目标为空。");
            var arguments = CommandLineToArgvW(command, out var count);
            if (arguments == IntPtr.Zero) throw new IOException("重建进程参数无效。");
            try { return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, i * IntPtr.Size)) ?? "").ToArray(); }
            finally { _ = LocalFree(arguments); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, IntPtr information, int length, out int resultLength);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
