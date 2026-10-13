using System.Net;
using System.Text;
using System.Text.Json;
using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class ReadyCheckedBusinessHostTests
{
    private static readonly Uri ReadyUri = new("https://exam.local/health/ready");

    [Fact]
    public async Task ReadinessObservationPreservesAnUnreachableEndpointAsUnconfirmed()
    {
        var process = new FakeProcessHost { Status = BusinessServiceStatus.Running };
        using var client = Client(() => throw new HttpRequestException("synthetic unreachable endpoint"));
        var host = new ReadyCheckedBusinessHost(process, client, ReadyUri, "server-a", "node-a", 1);
        await Assert.ThrowsAsync<HttpRequestException>(() => host.ObserveReadyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Start_WaitsForReadyWithMatchingIdentity()
    {
        var process = new FakeProcessHost();
        var calls = 0;
        using var client = Client(() => ++calls == 1
            ? Response(HttpStatusCode.ServiceUnavailable, "{}")
            : Response(HttpStatusCode.OK, Ready("server-a", "node-a")));
        var host = new ReadyCheckedBusinessHost(process, client, ReadyUri, "server-a", "node-a", 1,
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1));

        await host.StartAsync(CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(1, process.Starts);
        Assert.Equal(BusinessServiceStatus.Running, host.Status);
        await host.StopAsync(CancellationToken.None);
        Assert.Equal(BusinessServiceStatus.Stopped, host.Status);
    }

    [Fact]
    public async Task Start_RejectsWrongNodeEvenWhenEndpointClaimsReady()
    {
        var process = new FakeProcessHost();
        using var client = Client(() => Response(HttpStatusCode.OK, Ready("server-a", "other-node")));
        var host = new ReadyCheckedBusinessHost(process, client, ReadyUri, "server-a", "node-a", 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None));

        Assert.Equal(BusinessServiceStatus.Running, process.Status);
        Assert.Equal(BusinessServiceStatus.Recovering, host.Status);
    }

    [Fact]
    public async Task Start_RejectsStaleServiceEpoch()
    {
        var process = new FakeProcessHost();
        using var client = Client(() => Response(HttpStatusCode.OK, Ready("server-a", "node-a", 1)));
        var host = new ReadyCheckedBusinessHost(process, client, ReadyUri, "server-a", "node-a", 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None));

        Assert.Equal(BusinessServiceStatus.Recovering, host.Status);
    }

    [Fact]
    public async Task Start_DoesNotReportRunningWhenReadinessNeverPasses()
    {
        var process = new FakeProcessHost();
        using var client = Client(() => Response(HttpStatusCode.ServiceUnavailable, "{}"));
        var host = new ReadyCheckedBusinessHost(process, client, ReadyUri, "server-a", "node-a", 1,
            TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(2));

        await Assert.ThrowsAsync<TimeoutException>(() => host.StartAsync(CancellationToken.None));

        Assert.Equal(BusinessServiceStatus.Running, process.Status);
        Assert.Equal(BusinessServiceStatus.Recovering, host.Status);
    }

    [Fact]
    public async Task Start_FailsIfProcessExitsDuringReadyCheck()
    {
        var process = new FakeProcessHost();
        using var client = Client(() =>
        {
            process.Status = BusinessServiceStatus.Stopped;
            return Response(HttpStatusCode.OK, Ready("server-a", "node-a"));
        });
        var host = new ReadyCheckedBusinessHost(process, client, ReadyUri, "server-a", "node-a", 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None));

        Assert.Equal(BusinessServiceStatus.Stopped, host.Status);
    }

    [Fact]
    public async Task Start_BoundsAStalledHealthRequest()
    {
        var process = new FakeProcessHost();
        using var client = new HttpClient(new HangingHandler());
        var host = new ReadyCheckedBusinessHost(process, client, ReadyUri, "server-a", "node-a", 1,
            TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(5));

        await Assert.ThrowsAsync<TimeoutException>(() => host.StartAsync(CancellationToken.None));

        Assert.Equal(BusinessServiceStatus.Recovering, host.Status);
    }

    [Fact]
    public async Task ManagementService_DoesNotReportSuccessForRunningButUnreadyProcess()
    {
        var process = new FakeProcessHost();
        using var client = Client(() => Response(HttpStatusCode.ServiceUnavailable, "{}"));
        var host = new ReadyCheckedBusinessHost(process, client, ReadyUri, "server-a", "node-a", 1,
            TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(2));
        var configuration = new ServiceConfiguration("server-a", "node-a", "127.0.0.1", 443,
            Path.Combine(Path.GetTempPath(), "exam-f01"), false, 1);
        var manager = new ServiceManagementService(configuration, host, new PassingProbe(),
            new UnusedBackups(), new SystemClock(), new StaticConfigurationStore(configuration));

        var failure = await Assert.ThrowsAsync<DomainRuleException>(() => manager.StartAsync());

        Assert.Equal(ErrorCodes.PersistenceFailed, failure.Code);
        Assert.Equal(BusinessServiceStatus.Running, process.Status);
        Assert.Equal(BusinessServiceStatus.Faulted, manager.GetStatus().Status);
    }

    [Fact]
    public void Constructor_RejectsNonHttpsOrWrongHealthPath()
    {
        using var client = Client(() => Response(HttpStatusCode.OK, Ready("server-a", "node-a")));
        Assert.Throws<ArgumentException>(() => new ReadyCheckedBusinessHost(
            new FakeProcessHost(), client, new Uri("http://exam.local/health/ready"), "server-a", "node-a", 1));
        Assert.Throws<ArgumentException>(() => new ReadyCheckedBusinessHost(
            new FakeProcessHost(), client, new Uri("https://exam.local/other"), "server-a", "node-a", 1));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("address")]
    [InlineData("port")]
    [InlineData("directory")]
    [InlineData("protection")]
    public async Task ManagedStartRejectsMissingOrDifferentAppliedConfiguration(string difference)
    {
        var expected = new ServiceConfiguration("server-a", "node-a", "127.0.0.1", 9444,
            Path.Combine(Path.GetTempPath(), "synthetic-storage"), true, 2);
        var applied = difference switch
        {
            "version" => expected with { Version = 1 },
            "address" => expected with { BindAddress = "0.0.0.0" },
            "port" => expected with { HttpsPort = 8443 },
            "directory" => expected with { DataDirectory = expected.DataDirectory + "-old" },
            "protection" => expected with { HighAvailabilityEnabled = false },
            _ => null
        };
        using var client = Client(() => Response(HttpStatusCode.OK, JsonSerializer.Serialize(new
        { status = "ready", serverId = "server-a", nodeId = "node-a", serverEpoch = 1, serviceConfiguration = applied }, JsonDefaults.Create())),
            new Uri("https://exam.local:9444/health/ready"));
        var host = new ReadyCheckedBusinessHost(new FakeProcessHost(), client, ReadyUri, "server-a", "node-a", 1,
            expectedConfiguration: expected);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(default));
        Assert.Equal(BusinessServiceStatus.Recovering, host.Status);
    }

    [Fact]
    public async Task ManagedStartUsesSavedPortAndRequiresExactAppliedConfiguration()
    {
        var expected = new ServiceConfiguration("server-a", "node-a", "127.0.0.1", 9444,
            Path.Combine(Path.GetTempPath(), "synthetic-storage"), false, 2);
        using var client = Client(() => Response(HttpStatusCode.OK, JsonSerializer.Serialize(new
        { status = "ready", serverId = "server-a", nodeId = "node-a", serverEpoch = 1, serviceConfiguration = expected }, JsonDefaults.Create())),
            new Uri("https://exam.local:9444/health/ready"));
        var host = new ReadyCheckedBusinessHost(new FakeProcessHost(), client, ReadyUri, "server-a", "node-a", 1,
            expectedConfiguration: expected);

        await host.StartAsync(default);
        Assert.Equal(BusinessServiceStatus.Running, host.Status);
    }

    private static string Ready(string serverId, string nodeId, long epoch = 1) =>
        $"{{\"status\":\"ready\",\"serverId\":\"{serverId}\",\"nodeId\":\"{nodeId}\",\"serverEpoch\":{epoch}}}";

    private static HttpResponseMessage Response(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpClient Client(Func<HttpResponseMessage> respond, Uri? expectedEndpoint = null) =>
        new(new FakeHandler(respond, expectedEndpoint ?? ReadyUri));

    private sealed class FakeHandler(Func<HttpResponseMessage> respond, Uri expectedEndpoint) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(expectedEndpoint, request.RequestUri);
            return Task.FromResult(respond());
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("请求不应在未取消时完成。");
        }
    }

    private sealed class FakeProcessHost : IManagedBusinessHost
    {
        public BusinessServiceStatus Status { get; set; } = BusinessServiceStatus.Stopped;
        public int Starts { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            Status = BusinessServiceStatus.Running;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Status = BusinessServiceStatus.Stopped;
            return Task.CompletedTask;
        }
    }

    private sealed class PassingProbe : IStartupReadinessProbe
    {
        public Task<IReadOnlyList<StartupCheckResult>> RunAsync(
            ServiceConfiguration configuration, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StartupCheckResult>>(
                [new("synthetic-ready", true, "合成检查通过。", true)]);
    }

    private sealed class StaticConfigurationStore(ServiceConfiguration configuration) : IServiceConfigurationStore
    {
        public Task<ServiceConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(configuration);

        public Task<ServiceConfiguration> SaveAsync(
            ServiceConfiguration requested, long expectedVersion, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedBackups : IBackupCoordinator
    {
        public Task<BackupResult> CreateAsync(BackupRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RestoreAsync(RestoreRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
