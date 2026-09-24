using System.ComponentModel;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class ScmAdapterBoundaryTests
{
    [Fact]
    public void NativeAdapterRejectsMissingServiceWithoutTouchingExistingServices()
    {
        var name = "ExamF01Missing-" + Guid.NewGuid().ToString("N");
        var control = new ScmWindowsServiceControl();
        if (!OperatingSystem.IsWindows())
        {
            Assert.Throws<PlatformNotSupportedException>(() => control.GetState(name));
            return;
        }
        Assert.Equal(1060, Assert.Throws<Win32Exception>(() => control.GetState(name)).NativeErrorCode);
        Assert.Equal(1060, Assert.Throws<Win32Exception>(() => control.Start(name)).NativeErrorCode);
        Assert.Equal(1060, Assert.Throws<Win32Exception>(() => control.Stop(name)).NativeErrorCode);
        Assert.Equal(BusinessServiceStatus.Faulted, new WindowsServiceBusinessHost(name).Status);
    }

    [Fact]
    public async Task CancellationDuringPendingStartDoesNotReportRunning()
    {
        using var cancel = new CancellationTokenSource();
        var control = new PendingControl();
        var host = new WindowsServiceBusinessHost("SyntheticOnly", control, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));
        var pending = host.StartAsync(cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(BusinessServiceStatus.Starting, host.Status);
    }

    private sealed class PendingControl : IWindowsServiceControl
    {
        public ManagedWindowsServiceState GetState(string name) => ManagedWindowsServiceState.StartPending;
        public void Start(string name) => throw new InvalidOperationException("Do not resend start while already pending.");
        public void Stop(string name) => throw new NotSupportedException();
    }
}
