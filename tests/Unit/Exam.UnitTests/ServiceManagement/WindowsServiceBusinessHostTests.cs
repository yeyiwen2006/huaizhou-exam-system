using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class WindowsServiceBusinessHostTests
{
    [Fact]
    public async Task Start_WaitsForActualWindowsServiceState()
    {
        var control = new FakeControl(
            ManagedWindowsServiceState.Stopped,
            ManagedWindowsServiceState.Stopped,
            ManagedWindowsServiceState.StartPending,
            ManagedWindowsServiceState.Running);
        var host = CreateHost(control);

        await host.StartAsync(CancellationToken.None);

        Assert.Equal("ExamBusiness", control.StartedService);
        Assert.Equal(BusinessServiceStatus.Running, host.Status);
    }

    [Fact]
    public async Task Stop_WaitsForActualWindowsServiceState()
    {
        var control = new FakeControl(
            ManagedWindowsServiceState.Running,
            ManagedWindowsServiceState.Running,
            ManagedWindowsServiceState.StopPending,
            ManagedWindowsServiceState.Stopped);
        var host = CreateHost(control);

        await host.StopAsync(CancellationToken.None);

        Assert.Equal("ExamBusiness", control.StoppedService);
        Assert.Equal(BusinessServiceStatus.Stopped, host.Status);
    }

    [Fact]
    public async Task Start_TimesOutWhenServiceNeverStarts()
    {
        var control = new FakeControl(ManagedWindowsServiceState.Stopped);
        var host = new WindowsServiceBusinessHost(
            "ExamBusiness", control, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(1));

        await Assert.ThrowsAsync<TimeoutException>(() => host.StartAsync(CancellationToken.None));
        Assert.Equal(BusinessServiceStatus.Stopped, host.Status);
    }

    [Fact]
    public void Constructor_RejectsUncontrolledServiceName()
    {
        Assert.Throws<ArgumentException>(() => CreateHost(new FakeControl(), "ExamBusiness & other"));
    }

    private static WindowsServiceBusinessHost CreateHost(FakeControl control, string name = "ExamBusiness") =>
        new(name, control, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), new PollingTimeProvider());

    [Fact]
    public async Task Start_UsesConfiguredDeadlineWithInjectedMonotonicTime()
    {
        var host = new WindowsServiceBusinessHost("ExamBusiness", new FakeControl(ManagedWindowsServiceState.Stopped),
            TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(1), new PollingTimeProvider());
        await Assert.ThrowsAsync<TimeoutException>(() => host.StartAsync(CancellationToken.None));
    }

    // 逻辑轮询每次推进 1ms；线程池争用不替代本测试所模拟的服务状态或时间。
    private sealed class PollingTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Increment(ref _timestamp);
    }

    private sealed class FakeControl(params ManagedWindowsServiceState[] states) : IWindowsServiceControl
    {
        private readonly Queue<ManagedWindowsServiceState> _states = new(states);
        private ManagedWindowsServiceState _last = states.Length > 0 ? states[^1] : ManagedWindowsServiceState.Stopped;

        public string? StartedService { get; private set; }

        public string? StoppedService { get; private set; }

        public ManagedWindowsServiceState GetState(string serviceName)
        {
            if (_states.TryDequeue(out var state))
            {
                _last = state;
            }

            return _last;
        }

        public void Start(string serviceName) => StartedService = serviceName;

        public void Stop(string serviceName) => StoppedService = serviceName;
    }
}
