using System.Net;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class LocalManagementClientTests
{
    [Theory]
    [InlineData("http://localhost:9443")]
    [InlineData("https://example.com:9443")]
    [InlineData("https://127.0.0.1:9443/business")]
    [InlineData("https://user@localhost:9443")]
    [InlineData("https://localhost:9443?token=x")]
    [InlineData("https://localhost:9443#x")]
    public void InvalidEndpointIsRejectedBeforeSendingCredentials(string endpoint) =>
        Assert.Throws<ArgumentException>(() => new LocalManagementClient(new Uri(endpoint), "server", "node", () => "synthetic-token"));

    [Theory]
    [InlineData("wrong-server", "node", null, false, null)]
    [InlineData("server", "wrong-node", null, false, null)]
    [InlineData("server", "node", 0L, false, null)]
    [InlineData("server", "node", 1L, false, null)]
    [InlineData("server", "node", null, true, "2026-09-27T00:00:00Z")]
    [InlineData("server", "node", 0L, true, "2026-09-27T00:00:00Z")]
    [InlineData("server", "node", 1L, true, null)]
    public async Task WrongIdentityOrEpochCannotBeAccepted(string server, string node, long? epoch, bool verified, string? time)
    {
        var envelope = new ManagementEnvelope<ManagementStatus>(server, node, epoch, verified,
            time is null ? null : DateTimeOffset.Parse(time), "correlation", new(BusinessServiceStatus.Stopped, BusinessReadiness.Unknown, null, []), null);
        using var transport = new ResponseTransport(_ => Json(HttpStatusCode.OK, envelope));
        using var client = Client(transport);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStatusAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.Found, "redirect")]
    [InlineData(HttpStatusCode.OK, "html")]
    [InlineData(HttpStatusCode.OK, "oversized")]
    public async Task InvalidTransportResponseNeverPrintsCredentialsOrClaimsSuccess(HttpStatusCode status, string response)
    {
        using var transport = new ResponseTransport(_ => new(status)
        { Content = new StringContent(response == "oversized" ? new string('x', 1024 * 1024 + 1) : "<html>synthetic-secret</html>") });
        using var client = Client(transport);
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(5, await new LocalManagementCliHandler(client).RunAsync("status", null, null, output, errors));
        Assert.Equal("", output.ToString());
        Assert.DoesNotContain("synthetic-secret", errors.ToString());
        Assert.Equal(1, transport.Calls);
    }

    [Fact]
    public async Task MissingCredentialsNeverContactService()
    {
        using var transport = new ResponseTransport(_ => throw new InvalidOperationException("must not send"));
        using var client = new LocalManagementClient(new Uri("https://localhost:9443"), "server", "node", () => null, transport);
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(3, await new LocalManagementCliHandler(client).RunAsync("status", null, null, output, errors));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task OperationResultMustMatchRequestBinding()
    {
        var operation = new ManagementOperation("other-request", "server", "node", "operator", ManagementOperationKind.Stop,
            "synthetic-hash", ManagementOperationPhase.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        using var transport = new ResponseTransport(request =>
        {
            Assert.Equal("/management/v1/operations/original", request.RequestUri!.AbsolutePath);
            Assert.Equal("server", Assert.Single(request.Headers.GetValues(ManagementHeaders.ServerId)));
            return Json(HttpStatusCode.OK, new ManagementEnvelope<ManagementOperation>("server", "node", null, false, null, "correlation", operation, null));
        });
        using var client = Client(transport);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetOperationAsync("original"));
    }

    [Theory]
    [InlineData(ManagementOperationKind.Start)]
    [InlineData(ManagementOperationKind.Stop)]
    [InlineData(ManagementOperationKind.Checks)]
    [InlineData(ManagementOperationKind.Restore)]
    public async Task CompletedWithoutActualResultCannotBeReportedAsSuccess(ManagementOperationKind kind)
    {
        var operation = new ManagementOperation("original", "server", "node", "operator", kind,
            "synthetic-hash", ManagementOperationPhase.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            Restore: kind == ManagementOperationKind.Restore ? new("backup", true, false, false) : null);
        using var transport = new ResponseTransport(_ => Json(HttpStatusCode.OK,
            new ManagementEnvelope<ManagementOperation>("server", "node", null, false, null, "correlation", operation, null)));
        using var client = Client(transport);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetOperationAsync("original"));
    }

    [Theory]
    [InlineData(false, false, 5)]
    [InlineData(true, false, 130)]
    [InlineData(false, true, 5)]
    public async Task BodyDeadlineCancellationAndReadFailureNeverRepeatMutation(bool cancelCaller, bool brokenStream, int expectedExit)
    {
        using var body = new InterruptedBodyStream(brokenStream);
        using var transport = new ResponseTransport(_ => new(HttpStatusCode.OK) { Content = new StreamContent(body) });
        using var client = new LocalManagementClient(new Uri("https://localhost:9443"), "server", "node", () => "synthetic-token", transport,
            requestTimeout: cancelCaller ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(150));
        using var cancellation = new CancellationTokenSource();
        if (cancelCaller) cancellation.CancelAfter(TimeSpan.FromMilliseconds(150));
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(expectedExit, await new LocalManagementCliHandler(client).RunAsync("stop",
            "{\"requestId\":\"same-request\",\"serverId\":\"server\",\"nodeId\":\"node\"}", null, output, errors, cancellation.Token));
        Assert.Equal(1, transport.Calls);
        Assert.Equal("", output.ToString());
        Assert.Contains("requestId", errors.ToString());
        Assert.DoesNotContain("synthetic-stream-secret", errors.ToString());
    }

    private sealed class InterruptedBodyStream(bool broken) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (broken) throw new IOException("synthetic-stream-secret");
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static LocalManagementClient Client(HttpMessageHandler handler) => new(new Uri("https://localhost:9443"), "server", "node", () => "synthetic-token", handler);
    private static HttpResponseMessage Json<T>(HttpStatusCode status, T value) => new(status)
    { Content = new StringContent(JsonSerializer.Serialize(value, JsonDefaults.Create()), Encoding.UTF8, "application/json") };
    private sealed class ResponseTransport(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return Task.FromResult(respond(request)); }
    }
}
