using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Exam.Student.Core;

/// <summary>
/// Starts the UI on a Win32 desktop without Explorer/Task View. Precision-touchpad
/// shell gestures therefore cannot reveal other applications or virtual desktops.
/// The original process stays on the original desktop and restores it even when
/// the UI process crashes. No registry, touchpad driver or system shell is changed.
/// </summary>
internal static class ExamDesktopSession
{
    private const string ChildArgument = "--exam-desktop";
    private const string Prefix = "HuaizhouExam-";
    private const uint DesktopAccess = 0x01ff;
    private static EventWaitHandle? _ready;
    private static SafeProcessHandle? _guardian;
    private static System.Windows.Threading.DispatcherTimer? _guardianTimer;

    internal static int Run(string[] args, Func<int> application)
    {
        if (args.Length == 4 && args[0] == ChildArgument)
            return RunChild(args, application);
        if (args.Length != 0) throw new InvalidOperationException("不支持的学生端启动参数。");
        using var singleton = new Mutex(true, "Local\\HuaizhouExam.StudentDesktop", out var first);
        if (!first) return 0;
        try { return RunGuardian(); }
        finally { singleton.ReleaseMutex(); }
    }

    private static int RunChild(string[] args, Func<int> application)
    {
        var desktopName = args[1];
        if (!ValidName(desktopName) || CurrentDesktopName() != desktopName ||
            !int.TryParse(args[2], out var parentId) || parentId <= 0 || parentId == Environment.ProcessId ||
            string.IsNullOrWhiteSpace(args[3]) || args[3].Contains('\\') || args[3].StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("考试桌面启动身份不匹配。");
        using var original = Native.OpenDesktopW(args[3], 0, false, 0x0101);
        _guardian = Native.OpenProcess(0x00100000, false, parentId);
        if (original.IsInvalid || _guardian.IsInvalid || Native.WaitForSingleObject(_guardian, 0) != 0x102)
            throw new InvalidOperationException("桌面恢复进程未就绪。");
        _ready = EventWaitHandle.OpenExisting("Local\\" + desktopName);
        try { return application(); }
        finally
        {
            _guardianTimer?.Stop();
            _ready?.Dispose(); _ready = null;
            _guardian.Dispose(); _guardian = null;
            // The guardian also restores this after termination, including abnormal exits.
            Native.SwitchDesktop(original);
        }
    }

    internal static void WindowReady()
    {
        if (_ready is null || _guardian is null) return;
        _ready.Set();
        _guardianTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        _guardianTimer.Tick += (_, _) =>
        {
            if (_guardian is not null && Native.WaitForSingleObject(_guardian, 0) != 0x102)
            {
                _guardianTimer.Stop();
                if (System.Windows.Application.Current.MainWindow is Exam.Student.MainWindow window)
                    window.PrepareForApplicationExit();
                System.Windows.Application.Current.Shutdown(1);
            }
        };
        _guardianTimer.Start();
    }

    private static int RunGuardian()
    {
        using var original = Native.OpenInputDesktop(0, false, 0x0101);
        if (original.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var originalName = ObjectName(original.DangerousGetHandle());
        if (originalName.StartsWith(Prefix, StringComparison.Ordinal)) throw new InvalidOperationException("考试桌面已经开启。");
        var name = Prefix + Guid.NewGuid().ToString("N");
        using var desktop = Native.CreateDesktopW(name, null, nint.Zero, 0, DesktopAccess, nint.Zero);
        if (desktop.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, "Local\\" + name);
        using var child = StartOnDesktop(name, Environment.ProcessId, originalName);
        var activated = false;
        try
        {
            var timeout = Stopwatch.StartNew();
            while (!ready.WaitOne(100))
            {
                if (Native.WaitForSingleObject(child, 0) != 0x102)
                    throw new IOException("考试界面启动失败。");
                if (timeout.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("考试界面启动超时。");
            }
            if (!Native.SwitchDesktop(desktop)) throw new Win32Exception(Marshal.GetLastWin32Error());
            activated = true;
            while (Native.WaitForSingleObject(child, 250) == 0x102)
            {
                // Secure desktops (lock/sign-in/UAC) remain controlled by Windows.
                // Restore our UI only if Windows returned to this session's original desktop.
                using var input = Native.OpenInputDesktop(0, false, 0x0001);
                if (!input.IsInvalid && ObjectName(input.DangerousGetHandle()) == originalName)
                    Native.SwitchDesktop(desktop);
            }
            if (!Native.GetExitCodeProcess(child, out var code)) return 1;
            return unchecked((int)code);
        }
        finally
        {
            // Only terminate the exact UI process created by this guardian, never a
            // process selected by name. Before activation it has no exam interaction.
            if (!activated && Native.WaitForSingleObject(child, 0) == 0x102)
            {
                Native.TerminateProcess(child, 1);
                Native.WaitForSingleObject(child, 5000);
            }
            if (activated)
            {
                // Unlock may happen after the UI exits. Keep the restore handle alive
                // while Windows owns a secure desktop; do not leave an empty exam desktop.
                while (!Native.SwitchDesktop(original)) Thread.Sleep(250);
            }
        }
    }

    private static SafeProcessHandle StartOnDesktop(string desktop, int parentId, string originalName)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("学生端启动路径不可用。");
        var assembly = typeof(ExamDesktopSession).Assembly.Location;
        var command = Quote(executable);
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            command += " " + Quote(assembly);
        command += $" {ChildArgument} {desktop} {parentId} " + Quote(originalName);
        var startup = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfo>(), Desktop = desktop };
        if (!Native.CreateProcessW(executable, new StringBuilder(command), nint.Zero, nint.Zero, false,
                0x08000000, nint.Zero, AppContext.BaseDirectory, ref startup, out var process))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        Native.CloseHandle(process.Thread);
        return new SafeProcessHandle(process.Process, true);
    }

    private static string Quote(string value)
    {
        if (value.Contains('"') || value.Contains('\r') || value.Contains('\n') || value.EndsWith('\\'))
            throw new IOException("启动参数格式无效。");
        return "\"" + value + "\"";
    }
    internal static bool ValidName(string name) => name.StartsWith(Prefix, StringComparison.Ordinal) &&
        name.Length == Prefix.Length + 32 && name[Prefix.Length..].All(char.IsAsciiHexDigit);
    private static string CurrentDesktopName() => ObjectName(Native.GetThreadDesktop(Native.GetCurrentThreadId()));
    private static string ObjectName(nint handle)
    {
        var name = new StringBuilder(256);
        if (!Native.GetUserObjectInformationW(handle, 2, name, name.Capacity * 2, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return name.ToString();
    }

    private sealed class DesktopHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public DesktopHandle() : base(true) { }
        protected override bool ReleaseHandle() => Native.CloseDesktop(handle);
    }
    private static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            public int Size;
            public string? Reserved, Desktop, Title;
            public uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
            public ushort ShowWindow, ReservedSize;
            public nint ReservedPointer, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern DesktopHandle CreateDesktopW(string name, string? device, nint mode, uint flags, uint access, nint security);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern DesktopHandle OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern DesktopHandle OpenDesktopW(string name, uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SwitchDesktop(DesktopHandle desktop);
        [DllImport("user32.dll")] internal static extern bool CloseDesktop(nint desktop);
        [DllImport("user32.dll")] internal static extern nint GetThreadDesktop(uint thread);
        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool GetUserObjectInformationW(nint handle, int index, StringBuilder value, int length, out int needed);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool CreateProcessW(string application, StringBuilder command, nint processSecurity,
            nint threadSecurity, bool inherit, uint flags, nint environment, string directory,
            ref StartupInfo startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
        [DllImport("kernel32.dll")] internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
        [DllImport("kernel32.dll")] internal static extern bool TerminateProcess(SafeProcessHandle process, uint code);
        [DllImport("kernel32.dll")] internal static extern bool CloseHandle(nint handle);
    }
}
