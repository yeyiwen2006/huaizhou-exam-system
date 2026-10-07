using System.Runtime.InteropServices;

namespace Exam.Student.Core;

/// <summary>霸屏状态下对给定按键的处理决策。</summary>
public enum KioskKeyDecision
{
    /// <summary>放行该按键。</summary>
    Allow,
    /// <summary>吞掉该按键，不传递给系统或窗口。</summary>
    Suppress,
    /// <summary>吞掉按键并立即把窗口带回前台。</summary>
    SuppressAndRefocus,
}

/// <summary>
/// 普通权限霸屏：低级键盘钩子（WH_KEYBOARD_LL）在标准用户会话内即可安装，
/// 拦截 Win、Ctrl+Esc、Alt+Tab、Alt+Esc、Alt+F4 等离开考试界面的快捷键。
/// Ctrl+Alt+Del 由 Windows 安全注意画面处理，用户态无法拦截，属文档注明的行为边界；
/// 失焦由 FocusTracker 做会话内统计，不依赖钩子。钩子回调须保持极简，不做任何阻塞操作。
/// </summary>
public sealed class KioskGuard
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;

    private const int VkTab = 0x09;
    private const int VkEscape = 0x1B;
    private const int VkF4 = 0x73;
    private const int VkLwin = 0x5B;
    private const int VkRwin = 0x5C;

    private static readonly object InstallLock = new();
    private static KioskGuard? _active;

    private static readonly Native.HookProc HookProcedure = HookCallback;
    private readonly WeakReference _window = new(null);
    private nint _hook;
    private bool _disposed;
    private readonly HashSet<int> _suppressedKeys = [];

    private KioskGuard() { }
    public bool IsInstalled => _hook != nint.Zero && !_disposed;
    public int InstallationError { get; private set; }

    /// <summary>当前是否有霸屏守卫处于激活状态（同进程同时只有一个）。</summary>
    public static KioskGuard? Active
    {
        get { lock (InstallLock) return _active; }
    }

    /// <summary>为窗口启用霸屏快捷键拦截；普通用户权限即可安装低级键盘钩子。</summary>
    public static KioskGuard Enable(System.Windows.Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        lock (InstallLock)
        {
            if (_active is { } existing)
            {
                existing._window.Target = window;
                return existing;
            }

            var guard = new KioskGuard();
            guard._window.Target = window;
            var hook = Native.SetWindowsHookExW(WhKeyboardLl, HookProcedure, nint.Zero, 0);
            if (hook == nint.Zero)
            {
                guard.InstallationError = Marshal.GetLastWin32Error();
                // 安装失败（例如受限会话）时不阻断考试：窗口仍全屏置顶，
                // 快捷键拦截降级为文档说明的行为边界。
                return guard;
            }

            guard._hook = hook;
            _active = guard;
            return guard;
        }
    }

    /// <summary>切换霸屏目标窗口（导航换页时窗口实例不变，此方法仅作保险）。</summary>
    public void Retarget(System.Windows.Window window)
    {
        lock (InstallLock)
        {
            if (_disposed || _active != this)
            {
                return;
            }
            _window.Target = window;
        }
    }

    /// <summary>停止拦截并卸载钩子；仅显式退出系统时调用。</summary>
    public void Disable()
    {
        nint hook;
        lock (InstallLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_active == this)
            {
                _active = null;
            }
            hook = _hook;
            _hook = nint.Zero;
        }

        if (hook != nint.Zero)
        {
            Native.UnhookWindowsHookEx(hook);
        }
    }

    private static nint HookCallback(int code, nint wParam, nint lParam)
    {
        KioskGuard? guard;
        lock (InstallLock) { guard = _active; }
        if (guard is null || code < 0)
        {
            return Native.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }

        var message = wParam.ToInt32();
        if (message is not (WmKeydown or WmSyskeydown or WmKeyup or WmSyskeyup))
        {
            return Native.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<Native.KbdLlHookStruct>(lParam);
        var released = message is WmKeyup or WmSyskeyup;
        if (released && guard._suppressedKeys.Remove(data.VkCode))
        {
            return (nint)1;
        }

        // Ctrl 是另一个已经按下的修饰键；flags 的 0x02 是 LOWER_IL_INJECTED，不是 Ctrl。
        var controlDown = (Native.GetAsyncKeyState(0x11) & 0x8000) != 0;
        var decision = Decide(data.VkCode, data.Flags, message is WmSyskeydown or WmSyskeyup, controlDown);
        if (decision == KioskKeyDecision.Allow)
        {
            return Native.CallNextHookEx(nint.Zero, code, wParam, lParam);
        }

        if (!released) guard._suppressedKeys.Add(data.VkCode);
        if (!released &&
            guard._window.Target is System.Windows.Window { } window)
        {
            window.Dispatcher.BeginInvoke(() =>
            {
                if (window.WindowState != System.Windows.WindowState.Maximized)
                {
                    window.WindowState = System.Windows.WindowState.Maximized;
                }
                window.Activate();
            });
        }

        // 返回 1 表示吞掉该按键，不继续传递。
        return (nint)1;
    }

    /// <summary>纯判定函数：霸屏状态下给定按键是否拦截，独立于钩子可测。</summary>
    public static KioskKeyDecision Decide(int vkCode, int flags, bool isSystemKey, bool controlDown = false)
    {
        const int LlkhfAltDown = 0x00000020;
        var alt = (flags & LlkhfAltDown) != 0;
        var ctrl = controlDown;

        if (vkCode is VkLwin or VkRwin)
        {
            return KioskKeyDecision.SuppressAndRefocus;
        }
        if (alt && vkCode == VkTab)
        {
            return KioskKeyDecision.SuppressAndRefocus;
        }
        if (alt && vkCode == VkEscape)
        {
            return KioskKeyDecision.Suppress;
        }
        if (ctrl && !alt && vkCode == VkEscape)
        {
            return KioskKeyDecision.Suppress;
        }
        if (isSystemKey && alt && vkCode == VkF4)
        {
            return KioskKeyDecision.Suppress;
        }
        return KioskKeyDecision.Allow;
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct KbdLlHookStruct
        {
            public int VkCode;
            public int ScanCode;
            public int Flags;
            public int Time;
            public nint ExtraInfo;
        }

        public delegate nint HookProc(int code, nint wParam, nint lParam);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern nint SetWindowsHookExW(int idHook, HookProc lpfn, nint hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(nint hhk);

        [DllImport("user32.dll")]
        public static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);
    }
}
