using System.Net;
using System.Security.Claims;
using Exam.Application.HighAvailability;
using Exam.Application.Commands;
using Exam.Contracts.Common;
using Exam.Domain.Common;
using Exam.Server.Runtime;
using Exam.Server.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Exam.IntegrationTests.Realtime;

/// <summary>Transport ordering/failure tests; these substitutes are not PostgreSQL recovery evidence.</summary>
public sealed class BusinessConfirmationTransportTests
{
    [Fact]
    public async Task OnlyDisplayRoutesOptOutOfDurableConfirmation()
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ReliableCommandService>(_ => throw new InvalidOperationException("Metadata inspection only."));
        await using var app = builder.Build();
        var api = app.MapGroup("/api/v1");
        api.MapExamCatalogApi(); api.MapReliableCommandApi();
        await app.StartAsync();
        var observations = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Where(endpoint => endpoint.Metadata.GetMetadata<ReadOnlyObservation>() is not null).ToArray();
        Assert.Equal(new[] { "/api/v1/commands/time", "/api/v1/exams", "/api/v1/exams/{examId}" },
            observations.Select(endpoint => endpoint.RoutePattern.RawText!.TrimEnd('/')).Order().ToArray());
        Assert.All(observations, endpoint => Assert.Equal("GET",
            Assert.Single(endpoint.Metadata.GetRequiredMetadata<HttpMethodMetadata>().HttpMethods)));
    }

    [Fact]
    public async Task DisplayQueryReturnsWhileWriteIsWaitingForStandby()
    {
        var gate = new WaitingGate();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IBusinessConfirmation>(gate);
        await using var app = builder.Build();
        app.UseMiddleware<ApiExceptionMiddleware>(); app.UseMiddleware<BusinessConfirmationMiddleware>();
        app.MapPost("/api/v1/write", () => Results.Ok("saved"));
        app.MapGet("/api/v1/display", () => Results.Ok("current-settings"))
            .WithMetadata(new ReadOnlyObservation());
        await app.StartAsync(); using var client = app.GetTestClient();
        var write = client.PostAsync("/api/v1/write", null);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var read = await client.GetAsync("/api/v1/display").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Contains("current-settings", await read.Content.ReadAsStringAsync());
            Assert.False(write.IsCompleted);
            Assert.Equal(1, gate.Calls);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await write).StatusCode);
    }

    [Theory]
    [InlineData("POST", false)]
    [InlineData("GET", true)]
    public async Task DisplayMetadataCannotBypassWritesOrExplicitResourceConfirmation(string method, bool binary)
    {
        var gate = new TransportGate { HandlerCompleted = true };
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IBusinessConfirmation>(gate);
        await using var app = builder.Build();
        app.UseMiddleware<ApiExceptionMiddleware>(); app.UseMiddleware<BusinessConfirmationMiddleware>();
        var endpoint = app.MapMethods("/api/v1/test", [method], () => Results.Ok("private-success-payload"))
            .WithMetadata(new ReadOnlyObservation());
        if (binary) endpoint.WithMetadata(new ConfirmationBeforeResult()).AddEndpointFilter<ConfirmationBeforeResult>();
        await app.StartAsync(); using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/v1/test");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("private-success-payload", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, gate.Calls);
    }

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

    [Fact]
    public async Task ManualResend_PushesOnlyAfterConfirmationAndPreservesOriginalCommand()
    {
        var gate = new TransportGate { HandlerCompleted = true };
        var proxy = new CommandProxy();
        var clients = System.Reflection.DispatchProxy.Create<IHubClients, ClientsProxy>();
        ((ClientsProxy)(object)clients).Target = proxy;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IBusinessConfirmation>(gate);
        builder.Services.AddSingleton<IHubContext<Exam.Server.Realtime.ExamEventHub>>(new CommandHub(clients));
        await using var app = builder.Build();
        app.UseMiddleware<ApiExceptionMiddleware>();
        app.UseMiddleware<BusinessConfirmationMiddleware>();
        var delivery = new Exam.Contracts.Commands.CommandDelivery(new("server", "node", 1), "event", "exam", "part", 1,
            CommandEventType.End, 2, "device", 1, 1, null, new Dictionary<string,string>(), DateTimeOffset.UtcNow, 2);
        app.MapPost("/api/v1/manual-resend", () => new Exam.Server.Realtime.ManualCommandDeliveryResult(Results.Ok(), delivery))
            .WithMetadata(new ConfirmationBeforeResult());
        await app.StartAsync();
        using var client = app.GetTestClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/api/v1/manual-resend", null)).StatusCode);
        Assert.Empty(proxy.Deliveries);
        gate.Fail = false;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/manual-resend", null)).StatusCode);
        Assert.Same(delivery, Assert.Single(proxy.Deliveries));
        Assert.Equal(2, gate.Calls);
    }

    public class ClientsProxy : System.Reflection.DispatchProxy
    {
        public IClientProxy Target { get; set; } = null!;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            Assert.Equal("Group", method!.Name);
            Assert.Equal("participation:part", args![0]);
            return Target;
        }
    }
    private sealed class CommandProxy : IClientProxy
    {
        public List<object?> Deliveries { get; } = [];
        public Task SendCoreAsync(string method, object?[] args, CancellationToken ct = default)
        { Assert.Equal("Command", method); Deliveries.Add(Assert.Single(args)); return Task.CompletedTask; }
    }
    private sealed class CommandHub(IHubClients clients) : IHubContext<Exam.Server.Realtime.ExamEventHub>
    {
        public IHubClients Clients => clients;
        public IGroupManager Groups => throw new NotSupportedException();
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
    private sealed class WaitingGate : IBusinessConfirmation
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public async Task ConfirmAsync(CancellationToken token)
        {
            Calls++;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
            throw new DomainRuleException(ErrorCodes.ServiceRecovering, "备机确认未完成。", retryable: true);
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
