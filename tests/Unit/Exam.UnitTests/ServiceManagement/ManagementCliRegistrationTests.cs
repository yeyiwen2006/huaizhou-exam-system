using System.Net;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class ManagementCliRegistrationTests
{
    private static readonly string[] Target = ["--management-url", "https://localhost:9443", "--server-id", "server", "--node-id", "node"];

    [Fact]
    public async Task RegisteredReadUsesIndependentManagementRouteAndChecksTarget()
    {
        using var transport = new ResponseTransport(request =>
        {
            Assert.Equal("/management/v1/service/status", request.RequestUri!.AbsolutePath);
            Assert.Equal("server", Assert.Single(request.Headers.GetValues(ManagementHeaders.ServerId)));
            Assert.Equal("node", Assert.Single(request.Headers.GetValues(ManagementHeaders.NodeId)));
            return Json(new ManagementEnvelope<ManagementStatus>("server", "node", null, false, null, "correlation",
                new(BusinessServiceStatus.Stopped, BusinessReadiness.Unknown, null, []), null));
        });
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var exit = await ManagementCli.RunAsync("status", Target, default,
            (uri, server, node) => new LocalManagementClient(uri, server, node, () => "synthetic-token", transport), output, errors);
        Assert.Equal(0, exit);
        Assert.Contains("stopped", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", errors.ToString());
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task RegisteredLogsReadUsesAuthenticatedBoundedRoute()
    {
        using var transport = new ResponseTransport(request =>
        {
            Assert.Equal("/management/v1/service/logs?limit=50", request.RequestUri!.PathAndQuery);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return Json(new ManagementEnvelope<IReadOnlyList<ManagementAuditEntry>>("server", "node", null,
                false, null, "correlation", [new("synthetic-id", ManagementOperationKind.Stop,
                    ManagementOperationPhase.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null)], null));
        });
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(0, await ManagementCli.RunAsync("logs", Target, default,
            (uri, server, node) => new LocalManagementClient(uri, server, node, () => "synthetic-token", transport), output, errors));
        Assert.Contains("synthetic-id", output.ToString());
        Assert.Equal("", errors.ToString());
    }

    [Fact]
    public async Task RegisteredRuntimeLogsReadUsesManagementRoute()
    {
        using var transport = new ResponseTransport(request =>
        {
            Assert.Equal("/management/v1/service/runtime-logs?limit=50", request.RequestUri!.PathAndQuery);
            return Json(new ManagementEnvelope<IReadOnlyList<ProtectedRuntimeLogEntry>>("server", "node", null,
                false, null, "correlation", [new(DateTimeOffset.UtcNow, "business", "Error", "Exam.Server.Runtime.GradingWorker", 1)], null));
        });
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(0, await ManagementCli.RunAsync("runtime-logs", Target, default,
            (uri, server, node) => new LocalManagementClient(uri, server, node, () => "synthetic-token", transport), output, errors));
        Assert.Contains("GradingWorker", output.ToString());
        Assert.Equal("", errors.ToString());
    }

    [Fact]
    public async Task RegisteredWritePreservesOriginalRequestIdAndPendingResult()
    {
        var path = Path.Combine(Path.GetTempPath(), "synthetic-management-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "{\"requestId\":\"stable-id\",\"serverId\":\"server\",\"nodeId\":\"node\"}");
        try
        {
            using var transport = new ResponseTransport(request =>
            {
                Assert.Equal("/management/v1/service/stop", request.RequestUri!.AbsolutePath);
                Assert.Equal("stable-id", JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                    .RootElement.GetProperty("requestId").GetString());
                var operation = new ManagementOperation("stable-id", "server", "node", "operator",
                    ManagementOperationKind.Stop, new string('A', 64), ManagementOperationPhase.NeedsVerification,
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                return Json(new ManagementEnvelope<ManagementOperation>("server", "node", null, false, null,
                    "correlation", operation, null));
            });
            using var output = new StringWriter();
            using var errors = new StringWriter();
            var exit = await ManagementCli.RunAsync("stop", [.. Target, "--input", path], default,
                (uri, server, node) => new LocalManagementClient(uri, server, node, () => "synthetic-token", transport), output, errors);
            Assert.Equal(6, exit);
            Assert.Equal("", output.ToString());
            Assert.Contains("stable-id", errors.ToString());
            Assert.Equal(1, transport.Calls);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("http://localhost:9443")]
    [InlineData("https://example.com:9443")]
    public async Task UnsafeAddressIsRejectedBeforeSendingCredential(string endpoint)
    {
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var exit = await ManagementCli.RunAsync("status",
            ["--management-url", endpoint, "--server-id", "server", "--node-id", "node"], default,
            output: output, errors: errors);
        Assert.Equal(2, exit);
        Assert.Equal("", output.ToString());
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value, JsonDefaults.Create()), Encoding.UTF8, "application/json") };

    private sealed class ResponseTransport(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(response(request)); }
    }
}
