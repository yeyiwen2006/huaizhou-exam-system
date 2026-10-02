using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Application.HighAvailability;
using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Contracts.Operators;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;
using Exam.Infrastructure.Postgres.Operators;
using Exam.Infrastructure.ServiceManagement;
using Exam.Management;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Exam.IntegrationTests.HighAvailability;

public sealed class ManagementFailoverApiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "synthetic-ha-api-" + Guid.NewGuid().ToString("N"));
    private readonly Platform _platform = new();
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();
    private HighAvailabilityRequest Request => new("stable-failover", "server", "node-b", "operator", 1, 3, "node-b", "synthetic isolation",
        new("node-a", true, false, "operator", "synthetic isolation"), true);

    [Theory]
    [InlineData("takeover", ManagementOperationKind.Takeover)]
    [InlineData("emergency", ManagementOperationKind.Emergency)]
    [InlineData("rebuild", ManagementOperationKind.Rebuild)]
    [InlineData("reprotect", ManagementOperationKind.Reprotect)]
    public async Task FormalRoutesAuthenticateBindAndPersist_AllFourOperations(string route, ManagementOperationKind kind)
    {
        var request = Request;
        if (kind is ManagementOperationKind.Emergency or ManagementOperationKind.Reprotect) _platform.MakeLocalPrimary();
        if (kind == ManagementOperationKind.Rebuild) _platform.SetRole("node-b", DeploymentNodeRole.Isolated);
        if (kind == ManagementOperationKind.Reprotect) { _platform.SetRole("node-a", DeploymentNodeRole.Rebuilding); request = request with { TargetNodeId = "node-a" }; }
        await using var host = await ManagementFailoverTestHost.CreateAsync(_directory, _platform);
        using var client = host.Client();
        using var response = await client.PostAsJsonAsync("/management/v1/high-availability/" + route, request, Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("failoverPlan", raw);
        var envelope = JsonSerializer.Deserialize<ManagementEnvelope<ManagementOperation>>(raw, Json)!;
        Assert.True(envelope.EpochVerified); Assert.NotNull(envelope.EpochVerifiedAt);
        Assert.Equal(kind, envelope.Result!.Kind); Assert.Equal(ManagementOperationPhase.Completed, envelope.Result.Phase);
        Assert.Equal("operator", envelope.Result.ActorId); Assert.Equal(1, _platform.Effects);
        using var replay = await client.PostAsJsonAsync("/management/v1/high-availability/" + route, request, Json);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode); Assert.Equal(1, _platform.Effects);
        using var transport = new LocalManagementClient(new("https://localhost:9443"), "server", "node-b", () => host.Token, host.App.GetTestServer().CreateHandler());
        Assert.Equal(kind, (await transport.GetOperationAsync(request.RequestId)).Envelope.Result!.Kind);
        Assert.NotNull((await new FileManagementOperationStore(Path.Combine(_directory, "operations")).FindAsync(request.RequestId, default))!.FailoverPlan);
    }

    [Theory]
    [InlineData("actor", HttpStatusCode.Forbidden)]
    [InlineData("target", HttpStatusCode.BadRequest)]
    [InlineData("epoch", HttpStatusCode.Conflict)]
    [InlineData("version", HttpStatusCode.Conflict)]
    [InlineData("isolation", HttpStatusCode.ServiceUnavailable)]
    public async Task PrincipalTargetEpochVersionAndIsolationFailuresHaveNoEffect(string field, HttpStatusCode expected)
    {
        var request = field switch { "actor" => Request with { ActorId = "forged" }, "target" => Request with { NodeId = "other" },
            "epoch" => Request with { ExpectedEpoch = 0 }, "version" => Request with { ExpectedVersion = 2 },
            _ => Request with { Isolation = new("node-a", false, false, "operator", Request.Reason) } };
        await using var host = await ManagementFailoverTestHost.CreateAsync(_directory, _platform); using var client = host.Client();
        Assert.Equal(expected, (await client.PostAsJsonAsync("/management/v1/high-availability/takeover", request, Json)).StatusCode);
        Assert.Equal(0, _platform.Effects);
    }

    [Fact]
    public async Task UnknownAndUnauthenticatedStatesNeverReturnAVerifiedEpoch()
    {
        _platform.Unknown = true;
        await using var host = await ManagementFailoverTestHost.CreateAsync(_directory, _platform); using var client = host.Client();
        var envelope = await client.GetFromJsonAsync<ManagementEnvelope<HighAvailabilityStatus>>("/management/v1/high-availability/status", Json);
        Assert.Null(envelope!.ServerEpoch); Assert.False(envelope.EpochVerified); Assert.Null(envelope.Result!.VerifiedAt);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/management/v1/high-availability/takeover", Request, Json)).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/management/v1/high-availability/status")).StatusCode);
        Assert.Equal(0, _platform.Effects);
    }

    [Fact]
    public async Task LostReplyIsReconciledByAuthenticatedOperationQueryAcrossHostRestart()
    {
        _platform.ThrowAfterEffect = true;
        await using (var host = await ManagementFailoverTestHost.CreateAsync(_directory, _platform))
        {
            using var client = host.Client();
            var response = await client.PostAsJsonAsync("/management/v1/high-availability/takeover", Request, Json);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(ManagementOperationPhase.NeedsVerification, (await response.Content.ReadFromJsonAsync<ManagementEnvelope<ManagementOperation>>(Json))!.Result!.Phase);
        }
        await using var restarted = await ManagementFailoverTestHost.CreateAsync(_directory, _platform); using var next = restarted.Client();
        var observed = await next.GetFromJsonAsync<ManagementEnvelope<ManagementOperation>>("/management/v1/operations/" + Request.RequestId, Json);
        Assert.Equal(ManagementOperationPhase.Completed, observed!.Result!.Phase); Assert.Equal(1, _platform.Effects);
        Assert.Null(observed.Result.FailoverPlan);
    }

    [Theory]
    [InlineData("ha-status")]
    [InlineData("takeover")]
    public async Task SharedCliHandlerUsesTheSameHttpContractAndConclusion(string command)
    {
        await using var host = await ManagementFailoverTestHost.CreateAsync(_directory, _platform);
        using var output = new StringWriter(); using var errors = new StringWriter();
        var input = Path.Combine(_directory, "synthetic-request.txt"); await File.WriteAllTextAsync(input, JsonSerializer.Serialize(Request, Json));
        using var client = new LocalManagementClient(new("https://localhost:9443"), "server", "node-b", () => host.Token, host.App.GetTestServer().CreateHandler());
        Assert.Equal(0, await new LocalManagementCliHandler(client).RunAsync(command,
            command == "takeover" ? await File.ReadAllTextAsync(input) : null, null, output, errors));
        Assert.Empty(errors.ToString()); Assert.Contains("server", output.ToString());
        Assert.Equal(command == "takeover" ? 1 : 0, _platform.Effects);
    }

    private sealed class Platform : IHighAvailabilityManagementPlatform
    {
        public ServerDeploymentState State = new("server", "node-a", 1, ProtectionMode.SynchronousDualNode,
            new Dictionary<string, NodeState> { ["node-a"] = new("node-a", DeploymentNodeRole.Primary, true, false, 0, true, true, DateTimeOffset.UtcNow),
                ["node-b"] = new("node-b", DeploymentNodeRole.Standby, true, true, 0, true, true, DateTimeOffset.UtcNow) }, 3, []);
        public int Effects; public bool Unknown, ThrowAfterEffect;
        public void SetRole(string node, DeploymentNodeRole role) => State = State with {
            Nodes = new Dictionary<string, NodeState>(State.Nodes) { [node] = State.Nodes[node] with {
                Role = role, DatabaseInRecovery = role is DeploymentNodeRole.Standby or DeploymentNodeRole.Rebuilding } } };
        public void MakeLocalPrimary() { SetRole("node-b", DeploymentNodeRole.Primary); SetRole("node-a", DeploymentNodeRole.Standby); State = State with { ActiveNodeId = "node-b" }; }
        public Task<HighAvailabilityStatus> ObserveAsync(CancellationToken token) => Task.FromResult(new HighAvailabilityStatus("server", "node-b",
            Unknown ? null : State, Unknown ? null : DateTimeOffset.UtcNow, !Unknown && State.ProtectionMode == ProtectionMode.SynchronousDualNode,
            [new("failover-state", !Unknown, "synthetic observation", true),
             new("synchronous-protection", !Unknown && State.ProtectionMode == ProtectionMode.SynchronousDualNode, "synthetic confirmation", true)]));
        public Task<FailoverExecutionPlan> PrepareAsync(ManagementOperation operation, HighAvailabilityRequest request,
            ServerDeploymentState before, ServerDeploymentState after, CancellationToken token) => Task.FromResult(
            new FailoverExecutionPlan(operation.RequestId, operation.ContentHash, operation.Kind, before, after, "synthetic", "synthetic", 1, null));
        public Task ExecuteAsync(FailoverExecutionPlan plan, CancellationToken token)
        { Effects++; State = plan.After; if (ThrowAfterEffect) throw new IOException("synthetic lost response"); return Task.CompletedTask; }
        public Task<bool> ReconcileAsync(FailoverExecutionPlan plan, CancellationToken token) => Task.FromResult(State.Version == plan.After.Version);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}

// Real HTTP routing, account/session middleware, configuration file, cross-process journal and coordinator.
// The injected platform is explicitly synthetic; native PostgreSQL coverage lives in separate opted-in tests.
internal sealed class ManagementFailoverTestHost(WebApplication app, string token, string serverId = "server", string nodeId = "node-b") : IAsyncDisposable
{
    public WebApplication App { get; } = app;
    public string Token { get; } = token;
    public HttpClient Client()
    {
        var client = App.GetTestClient(); client.BaseAddress = new("https://localhost:9443");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        client.DefaultRequestHeaders.Add(ManagementHeaders.ServerId, serverId); client.DefaultRequestHeaders.Add(ManagementHeaders.NodeId, nodeId); return client;
    }
    public static async Task<ManagementFailoverTestHost> CreateAsync(string root, IHighAvailabilityManagementPlatform platform,
        string serverId = "server", string nodeId = "node-b")
    {
        var operations = Path.Combine(root, "operations"); Directory.CreateDirectory(operations);
        var configurations = new FileServiceConfigurationStore(Path.Combine(root, "configuration.json"));
        if (!File.Exists(Path.Combine(root, "configuration.json"))) await configurations.SaveAsync(new(serverId, nodeId, "127.0.0.1", 8443, root, true, 1), 0);
        var journal = new FileManagementOperationStore(operations); var clock = new SystemClock();
        var coordinator = new DurableFailoverCoordinator(serverId, nodeId, platform, journal, clock);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new ManagementOperatorSessions(() => null, Path.Combine(root, "synthetic-operators.cache"),
            loadFromDatabase: _ => Task.FromResult<IReadOnlyList<CachedOperatorCredential>>([
                new("operator", Exam.Application.Security.OperatorPasswordHasher.Hash("synthetic-password"), true)])));
        var host = new BusinessHost(); builder.Services.AddSingleton(new ManagementRuntime(serverId, nodeId, configurations,
            host, _ => host, new Probe(), () => throw new InvalidOperationException("Synthetic HA fixture does not execute backups."),
            journal, clock, failover: () => coordinator));
        var app = builder.Build(); app.MapManagementApi(); await app.StartAsync();
        using var client = app.GetTestClient(); client.BaseAddress = new("https://localhost:9443");
        using var login = await client.PostAsJsonAsync("/management/v1/login", new LoginOperatorRequest("operator", "synthetic-password"), JsonDefaults.Create());
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var envelope = await login.Content.ReadFromJsonAsync<ManagementEnvelope<JsonElement>>(JsonDefaults.Create());
        return new(app, envelope!.Result.GetProperty("token").GetString()!, serverId, nodeId);
    }
    public ValueTask DisposeAsync() => App.DisposeAsync();
    private sealed class BusinessHost : IManagedBusinessHost
    { public BusinessServiceStatus Status => BusinessServiceStatus.Stopped; public Task StartAsync(CancellationToken token) => Task.CompletedTask; public Task StopAsync(CancellationToken token) => Task.CompletedTask; }
    private sealed class Probe : IStartupReadinessProbe
    { public Task<IReadOnlyList<StartupCheckResult>> RunAsync(ServiceConfiguration configuration, CancellationToken token) => Task.FromResult<IReadOnlyList<StartupCheckResult>>([]); }
}
