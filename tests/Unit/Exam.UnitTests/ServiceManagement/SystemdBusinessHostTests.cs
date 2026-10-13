using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class SystemdBusinessHostTests
{
    [Theory]
    [InlineData("start", "inactive", "activating", "active")]
    [InlineData("stop", "active", "deactivating", "inactive")]
    public async Task OperationWaitsForTheSelectedUnit(string action, string initial, string transition, string final)
    {
        var runner = new FakeRunner(State(initial), new(0, "", ""), State(transition), State(final));
        var host = new SystemdBusinessHost("exam-business", runner);
        if (action == "start") await host.StartAsync(CancellationToken.None);
        else await host.StopAsync(CancellationToken.None);
        Assert.Equal(new[] { action, "--no-block", "exam-business.service" }, runner.Calls[1]);
        Assert.All(runner.Calls, args => Assert.Equal("exam-business.service", args[^1]));
        Assert.Equal(4, runner.Calls.Count);
    }

    [Fact]
    public void MissingUnitIsFaultedRatherThanStopped()
    {
        var host = new SystemdBusinessHost("missing", new FakeRunner(new ExternalProcessResult(0, "LoadState=not-found\nActiveState=inactive\n", "")));
        Assert.Equal(BusinessServiceStatus.Faulted, host.Status);
    }

    [Fact]
    public async Task PermissionFailureDoesNotReportSuccessfulStart()
    {
        var runner = new FakeRunner(State("inactive"), new(1, "", "Access denied"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SystemdBusinessHost("exam-business.service", runner).StartAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("--all")]
    [InlineData("exam; reboot")]
    [InlineData("../other")]
    public void UnitCannotSupplyOptionsOrCommands(string value) =>
        Assert.Throws<ArgumentException>(() => new SystemdBusinessHost(value));

    private static ExternalProcessResult State(string value) => new(0, $"LoadState=loaded\nActiveState={value}\n", "");

    private sealed class FakeRunner(params ExternalProcessResult[] results) : IExternalProcessRunner
    {
        private readonly Queue<ExternalProcessResult> _results = new(results);
        public List<string[]> Calls { get; } = [];
        public Task<ExternalProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken = default,
            string? workingDirectory = null, ExternalProcessLimits? limits = null)
        {
            Calls.Add(arguments.ToArray());
            return Task.FromResult(_results.Dequeue());
        }
    }
}
