using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Exam.Application.ServiceManagement;
using Exam.Contracts.ServiceManagement;

namespace Exam.Infrastructure.ServiceManagement;

public enum ManagedWindowsServiceState
{
    Stopped,
    StartPending,
    Running,
    StopPending,
    Faulted
}

public interface IWindowsServiceControl
{
    ManagedWindowsServiceState GetState(string serviceName);

    void Start(string serviceName);

    void Stop(string serviceName);
}

/// <summary>
/// Controls only the configured Windows service. The manager process can remain available when
/// that service and its database are stopped. No command line or executable path comes from a request.
/// </summary>
public sealed class WindowsServiceBusinessHost : IManagedBusinessHost
{
    private readonly string _serviceName;
    private readonly IWindowsServiceControl _control;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pollInterval;
    private readonly TimeProvider _timeProvider;

    public WindowsServiceBusinessHost(
        string serviceName,
        IWindowsServiceControl? control = null,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(serviceName) || serviceName.Length > 80 ||
            serviceName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.'))
        {
            throw new ArgumentException("受控 Windows 服务名称无效。", nameof(serviceName));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        _serviceName = serviceName;
        _control = control ?? new ScmWindowsServiceControl();
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        if (_timeout <= TimeSpan.Zero || _pollInterval <= TimeSpan.Zero || _pollInterval > _timeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "服务等待时间必须为正，轮询间隔不得超过等待时间。");
        }
    }

    public BusinessServiceStatus Status => GetState() switch
    {
        ManagedWindowsServiceState.Stopped => BusinessServiceStatus.Stopped,
        ManagedWindowsServiceState.StartPending => BusinessServiceStatus.Starting,
        ManagedWindowsServiceState.Running => BusinessServiceStatus.Running,
        ManagedWindowsServiceState.StopPending => BusinessServiceStatus.Stopping,
        _ => BusinessServiceStatus.Faulted
    };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = GetState();
        if (state == ManagedWindowsServiceState.Running)
        {
            return;
        }

        if (state == ManagedWindowsServiceState.Stopped)
        {
            _control.Start(_serviceName);
        }
        else if (state != ManagedWindowsServiceState.StartPending)
        {
            throw new InvalidOperationException("业务服务不处于可启动状态。");
        }

        await WaitForAsync(ManagedWindowsServiceState.Running, cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = GetState();
        if (state == ManagedWindowsServiceState.Stopped)
        {
            return;
        }

        if (state == ManagedWindowsServiceState.Running)
        {
            _control.Stop(_serviceName);
        }
        else if (state != ManagedWindowsServiceState.StopPending)
        {
            throw new InvalidOperationException("业务服务不处于可停止状态。");
        }

        await WaitForAsync(ManagedWindowsServiceState.Stopped, cancellationToken);
    }

    private ManagedWindowsServiceState GetState()
    {
        try
        {
            return _control.GetState(_serviceName);
        }
        catch (Win32Exception)
        {
            return ManagedWindowsServiceState.Faulted;
        }
    }

    private async Task WaitForAsync(ManagedWindowsServiceState target, CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        var sawTransition = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = GetState();
            if (state == target)
            {
                return;
            }

            sawTransition |= state is ManagedWindowsServiceState.StartPending or ManagedWindowsServiceState.StopPending;
            if (state == ManagedWindowsServiceState.Faulted ||
                (sawTransition && target == ManagedWindowsServiceState.Running && state == ManagedWindowsServiceState.Stopped) ||
                (sawTransition && target == ManagedWindowsServiceState.Stopped && state == ManagedWindowsServiceState.Running))
            {
                throw new InvalidOperationException("业务服务在等待期间退出或进入意外状态。");
            }

            var remaining = _timeout - _timeProvider.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("等待业务服务状态超时，须核对实际服务状态。");
            }

            await Task.Delay(remaining < _pollInterval ? remaining : _pollInterval, _timeProvider, cancellationToken);
        }
    }
}

/// <summary>Windows Service Control Manager adapter. The service name is fixed at deployment.</summary>
public sealed class ScmWindowsServiceControl : IWindowsServiceControl
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x00000001;

    public ManagedWindowsServiceState GetState(string serviceName)
    {
        using var service = Open(serviceName, ServiceQueryStatus);
        var size = Marshal.SizeOf<ServiceStatusProcess>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryServiceStatusEx(service, 0, buffer, (uint)size, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var status = Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
            return status.CurrentState switch
            {
                1 => ManagedWindowsServiceState.Stopped,
                2 => ManagedWindowsServiceState.StartPending,
                3 => ManagedWindowsServiceState.StopPending,
                4 => ManagedWindowsServiceState.Running,
                _ => ManagedWindowsServiceState.Faulted
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Start(string serviceName)
    {
        using var service = Open(serviceName, ServiceStart);
        if (!StartService(service, 0, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Stop(string serviceName)
    {
        using var service = Open(serviceName, ServiceStop);
        if (!ControlService(service, ServiceControlStop, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static SafeServiceHandle Open(string serviceName, uint access)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows 服务控制仅支持 Windows。");
        }

        using var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var service = OpenService(manager, serviceName, access);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            service.Dispose();
            throw new Win32Exception(error);
        }

        return service;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    private sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeServiceHandle() : base(true) { }

        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenService(SafeServiceHandle manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceStatusEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(SafeServiceHandle service, int infoLevel, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(SafeServiceHandle service, uint argumentCount, IntPtr arguments);

    [DllImport("advapi32.dll", EntryPoint = "ControlService", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(SafeServiceHandle service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", EntryPoint = "CloseServiceHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
