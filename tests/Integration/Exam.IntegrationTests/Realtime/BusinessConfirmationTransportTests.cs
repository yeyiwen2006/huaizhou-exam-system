using System.Net;
using System.Security.Claims;
using Exam.Application.HighAvailability;
using Exam.Contracts.Common;
using Exam.Domain.Common;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Exam.IntegrationTests.Realtime;

/// <summary>Transport ordering/failure tests; these substitutes are not PostgreSQL recovery evidence.</summary>
public sealed class BusinessConfirmationTransportTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpWithholdsSuccessBodyAndHeadersWhenConfirmationFails(bool binary)
    {
        var gate = new TransportGate();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IBusinessConfirmation>(gate);
        await using var app = builder.Build();
        app.UseMiddleware<ApiExceptionMiddleware>(); app.UseMiddleware<BusinessConfirmationMiddleware>();
        var endpoint = app.MapGet("/api/v1/test", (HttpContext context) =>
        {
            gate.HandlerCompleted = true;
            context.Response.Headers["X-Success-Only"] = "confirmed";
            return Results.Bytes("private-success-payload"u8.ToArray(), "application/octet-stream");
        });
        if (binary) endpoint.WithMetadata(new ConfirmationBeforeResult()).AddEndpointFilter<ConfirmationBeforeResult>();
        await app.StartAsync(); using var client = app.GetTestClient();
        var failure = await client.GetAsync("/api/v1/test");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        Assert.False(failure.Headers.Contains("X-Success-Only"));
        var body = await failure.Content.ReadAsStringAsync();
        Assert.Contains(ErrorCodes.ServiceRecovering, body); Assert.DoesNotContain("private-success-payload", body);
        gate.Fail = false; gate.HandlerCompleted = false;
        var retry = await client.GetAsync("/api/v1/test");
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("private-success-payload", await retry.Content.ReadAsStringAsync());
        Assert.Equal(2, gate.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignalRDoesNotReturnMethodResultUntilConfirmationCompletes(bool fails)
    {
        var gate = new TransportGate { Fail = fails };
        var hub = new TestHub();
        var invocation = new HubInvocationContext(new Caller(), new ServiceCollection().BuildServiceProvider(), hub,
            typeof(TestHub).GetMethod(nameof(TestHub.Result))!, []);
        var filter = new ConfirmationHubFilter(gate);
        async ValueTask<object?> Method(HubInvocationContext _)
        { await Task.Yield(); gate.HandlerCompleted = true; return "persisted-result"; }
        if (fails) await Assert.ThrowsAsync<HubException>(() => filter.InvokeMethodAsync(invocation, Method).AsTask());
        else Assert.Equal("persisted-result", await filter.InvokeMethodAsync(invocation, Method));
        Assert.Equal(1, gate.Calls);
    }

    [Theory]
    [InlineData("http", true, true)]
    [InlineData("https", false, true)]
    [InlineData("https", true, false)]
    public async Task IndependentJournalEndpointRejectsUntrustedTransportOrTargetBeforeResolvingWitness(string scheme, bool validToken, bool validNode)
    {
        const string token = "synthetic-independent-journal-peer-token";
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Configuration["Confirmation:PeerToken"] = token;
        builder.Configuration["ExamServer:ServerId"] = "server"; builder.Configuration["ExamServer:NodeId"] = "standby";
        await using var app = builder.Build(); app.MapStandbyConfirmation(); await app.StartAsync();
        using var client = app.GetTestClient(); client.BaseAddress = new Uri(scheme + "://localhost");
        client.DefaultRequestHeaders.Authorization = new("Bearer", validToken ? token : "wrong-token");
        client.DefaultRequestHeaders.Add("X-Exam-Server-Id", "server");
        client.DefaultRequestHeaders.Add("X-Exam-Node-Id", validNode ? "standby" : "another-node");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/replication/v1/confirmation")).StatusCode);
    }

    private sealed class TransportGate : IBusinessConfirmation
    {
        public bool Fail { get; set; } = true;
        public bool HandlerCompleted { get; set; }
        public int Calls { get; private set; }
        public Task ConfirmAsync(CancellationToken token)
        {
            Assert.True(HandlerCompleted); Calls++;
            if (Fail) throw new DomainRuleException(ErrorCodes.ServiceRecovering, "确认未完成。", retryable: true);
            return Task.CompletedTask;
        }
    }
    public sealed class TestHub : Hub { public string Result() => "persisted-result"; }
    private sealed class Caller : HubCallerContext
    {
        public override string ConnectionId => "synthetic";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override Microsoft.AspNetCore.Http.Features.IFeatureCollection Features { get; } = new Microsoft.AspNetCore.Http.Features.FeatureCollection();
        public override CancellationToken ConnectionAborted => default;
        public override void Abort() { }
    }
}
