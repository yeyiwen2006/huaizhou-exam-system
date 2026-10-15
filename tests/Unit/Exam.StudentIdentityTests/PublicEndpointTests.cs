using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Student.Core;

namespace Exam.StudentIdentityTests;

public sealed class PublicEndpointTests
{
    private static ActivatedServerTopology InternalTopology() => new("logical", [
        new("26.1.1.1", 443, "primary", new string('1', 64)),
        new("26.1.1.2", 443, "standby", new string('2', 64))]);
    private static PublicServerEndpoints Routes() => new("logical", [
        new("primary", "primary.example.com", 443, "26.1.1.1", 443),
        new("standby", "standby.example.com", 443, "26.1.1.2", 443)]);
    private static DeviceRecord Device() => new("device", "synthetic-secret", "202610150001",
        "primary.example.com", 443, DateTimeOffset.UtcNow, "logical", ActivationStatus.Active, ServerEpoch: 1);

    [Fact]
    public void PublicLoginUsesExplicitRoutesAndPreservesNodeIdentity()
    {
        var topology = TrustedServerTopology.FromActivation(InternalTopology(), Device(), "logical", Routes())!;
        Assert.Equal("primary.example.com", topology.Nodes[0].Host);
        Assert.Equal(new Uri("https://26.1.1.1/"), topology.Nodes[0].OriginAddress);
        Assert.True(topology.Nodes[0].UsesPublicHttps);
        Assert.Equal("primary", topology.Nodes[0].NodeId);
        Assert.Equal(new string('1', 64), topology.Nodes[0].CertificateSha256);
        Assert.True(topology.Equivalent(TrustedServerTopology.FromActivation(InternalTopology(),
            Device() with { Topology = topology }, "logical", Routes())!));
    }

    [Fact]
    public void DirectConnectionKeepsOriginalCertificatePins()
    {
        var topology = TrustedServerTopology.FromActivation(InternalTopology(), Device() with { Host = "26.1.1.1" }, "logical", Routes())!;
        Assert.Equal("26.1.1.1", topology.Nodes[0].Host);
        Assert.False(topology.Nodes[0].UsesPublicHttps);
    }

    [Fact]
    public void UnexpectedClusterOrOriginIsRejected()
    {
        Assert.Throws<StudentIdentityException>(() => TrustedServerTopology.FromActivation(
            InternalTopology() with { ServerId = "other" }, Device(), "other", Routes()));
        var other = Routes() with { Nodes = [Routes().Nodes[0] with { OriginHost = "26.9.9.9" }, Routes().Nodes[1]] };
        Assert.Throws<StudentIdentityException>(() => TrustedServerTopology.FromActivation(InternalTopology(), Device(), "logical", other));
    }

    [Fact]
    public void ChangedPublicEndpointCannotReplaceProtectedRegistration()
    {
        var topology = TrustedServerTopology.FromActivation(InternalTopology(), Device(), "logical", Routes())!;
        var changed = Routes() with { Nodes = [Routes().Nodes[0], Routes().Nodes[1] with { Host = "other.example.com" }] };
        Assert.Throws<StudentIdentityException>(() => TrustedServerTopology.FromActivation(InternalTopology(),
            Device() with { Topology = topology }, "logical", changed));
    }

    [Fact]
    public async Task AuthenticatedRequestsStayOnPublicEndpointAfterLogin()
    {
        var device = Device() with
        {
            Topology = TrustedServerTopology.FromActivation(InternalTopology(), Device(), "logical", Routes()),
            ActiveNodeId = "primary"
        };
        using var api = new HttpStudentApi(new Handler(request =>
        {
            Assert.Equal("primary.example.com", request.RequestUri!.Host);
            Assert.Equal("device", request.Headers.GetValues("X-Exam-Device-Id").Single());
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ApiEnvelope<ActivatedServerTopology>("logical", "primary", 1,
                    "test", InternalTopology(), null), options: JsonDefaults.Create())
            };
        }));
        api.ConfigureConnection(device);
        Assert.True((await api.GetTrustedTopologyAsync()).IsSuccess);
        Assert.Equal("primary.example.com", api.CurrentTrustedEndpoint!.Value.Device.Host);
    }

    [Fact]
    public async Task PublicTimeProbeAllowsGatewayLatencyBeyondTheDirectConnectionDeadline()
    {
        var device = Device() with
        {
            Topology = TrustedServerTopology.FromActivation(InternalTopology(), Device(), "logical", Routes()),
            ActiveNodeId = "primary"
        };
        using var api = new HttpStudentApi(new DelayedTimeHandler());
        api.ConfigureConnection(device);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var result = await api.GetServerTimeAsync(deadline.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal("primary", result.NodeId);
    }

    private sealed class DelayedTimeHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("primary.example.com", request.RequestUri!.Host);
            await Task.Delay(TimeSpan.FromMilliseconds(2500), ct);
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ApiEnvelope<ServerTimeSnapshot>("logical", "primary", 1, "test",
                    new(DateTimeOffset.UtcNow, new("logical", "primary", 1)), null), options: JsonDefaults.Create())
            };
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(handle(request));
    }
}
