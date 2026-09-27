using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;
using Exam.Infrastructure.ServiceManagement;
using Exam.Management;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Exam.IntegrationTests.ServiceManagement;

public sealed class LocalManagementApiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "management-api-" + Guid.NewGuid().ToString("N"));
    private readonly JsonSerializerOptionsProvider _json = new();
    private readonly ControlledHost _host = new();
    private readonly StagingBackups _backups = new();
    private const string Token = "synthetic-local-management-token-32-characters";
    private ServiceConfiguration Configuration => new("server", "node", "127.0.0.1", 8443, _directory, false, 1);
    public LocalManagementApiTests() => Directory.CreateDirectory(Path.Combine(_directory, "operations"));

    [Fact]
    public async Task StoppedBusinessAndMissingDatabaseStillPermitStatus_AndNeverReportAnEpoch()
    {
        await using var app = await AppAsync();
        using var client = Client(app);
        var response = await client.GetAsync("/management/v1/service/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ManagementEnvelope<ManagementStatus>>(_json.Options);
        Assert.NotNull(envelope);
        Assert.Null(envelope.ServerEpoch);
        Assert.False(envelope.EpochVerified);
        Assert.Equal(BusinessServiceStatus.Stopped, envelope.Result!.ProcessStatus);
        Assert.Equal(BusinessReadiness.Unknown, envelope.Result.Readiness);
    }

    [Fact]
    public async Task CorruptBusinessConfigurationDoesNotPreventStatusOrStop()
    {
        await using var app = await AppAsync();
        await File.WriteAllTextAsync(Path.Combine(_directory, "configuration.json"), "{}");
        _host.Status = BusinessServiceStatus.Running;
        using var client = Client(app);
        var envelope = await client.GetFromJsonAsync<ManagementEnvelope<ManagementStatus>>("/management/v1/service/status", _json.Options);
        Assert.Null(envelope!.Result!.Configuration);
        Assert.Contains(envelope.Result.Checks, check => !check.Passed && check.BlocksWrites);
        var response = await client.PostAsJsonAsync("/management/v1/service/stop", new ManagementServiceRequest("stop", "server", "node"), _json.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(BusinessServiceStatus.Stopped, _host.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task EveryReadEndpointRequiresValidIdentity(string? token)
    {
        await using var app = await AppAsync();
        using var client = Client(app, token);
        foreach (var path in new[] { "/service/status", "/service/configuration", "/operations/id" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/management/v1" + path)).StatusCode);
    }

    [Fact]
    public async Task MissingProtectedCredentialsRefuseAccess()
    {
        await using var app = await AppAsync(credentials: false);
        using var client = Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/management/v1/service/status")).StatusCode);
    }

    [Fact]
    public async Task PlainHttpAndWrongExpectedNodeAreRejected()
    {
        await using var app = await AppAsync();
        using var client = Client(app);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("http://localhost/management/v1/service/status")).StatusCode);
        client.DefaultRequestHeaders.Remove(ManagementHeaders.NodeId);
        client.DefaultRequestHeaders.Add(ManagementHeaders.NodeId, "other-node");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/management/v1/service/stop",
            new ManagementServiceRequest("stop-wrong-target", "server", "node"), _json.Options)).StatusCode);
        Assert.Equal(0, _host.StopCalls);
    }

    [Fact]
    public async Task ForgedActorAndWrongTargetNeverReachServiceControl()
    {
        await using var app = await AppAsync();
        using var client = Client(app);
        using var body = new StringContent("{\"requestId\":\"id\",\"serverId\":\"server\",\"nodeId\":\"node\",\"ActorId\":\"fake\"}", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/management/v1/service/stop", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/management/v1/service/stop", new ManagementServiceRequest("id", "server", "wrong"), _json.Options)).StatusCode);
        Assert.Equal(0, _host.StopCalls);
    }

    [Fact]
    public async Task RealFileConfigurationConflictAndRestartReplayKeepPersistedResult()
    {
        var request = new ManagementConfigurationRequest("configure", "server", "node", 1, Configuration with { HttpsPort = 9443 });
        await using (var app = await AppAsync())
        {
            using var client = Client(app);
            Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/management/v1/service/configuration", request, _json.Options)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/management/v1/service/configuration", request with { RequestId = "old-version" }, _json.Options)).StatusCode);
        }
        await using var restarted = await AppAsync();
        using var replayClient = Client(restarted);
        var response = await replayClient.PutAsJsonAsync("/management/v1/service/configuration", request, _json.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ManagementEnvelope<ManagementOperation>>(_json.Options);
        Assert.Equal(2, envelope!.Result!.Configuration!.Version);
    }

    [Fact]
    public async Task BlockingPreflightDoesNotStartProcess_AndSelfCheckDoesNotStartIt()
    {
        await using var app = await AppAsync(blockStartup: true);
        using var client = Client(app);
        var request = new ManagementServiceRequest("checks", "server", "node");
        var check = await client.PostAsJsonAsync("/management/v1/service/checks", request, _json.Options);
        Assert.Equal(HttpStatusCode.OK, check.StatusCode);
        var start = await client.PostAsJsonAsync("/management/v1/service/start", request with { RequestId = "start" }, _json.Options);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
        Assert.Equal(0, _host.StartCalls);
    }

    [Fact]
    public async Task RestoreReportsStagingOnly_AndRestartDoesNotRestageOrStartBusiness()
    {
        var request = new RestoreRequest("restore", "synthetic-backup", "synthetic-source", "operator");
        await using (var app = await AppAsync())
        {
            using var client = Client(app);
            var response = await client.PostAsJsonAsync("/management/v1/service/restores", request, _json.Options);
            var envelope = await response.Content.ReadFromJsonAsync<ManagementEnvelope<ManagementOperation>>(_json.Options);
            Assert.Equal(ManagementOperationPhase.Staged, envelope!.Result!.Phase);
            Assert.True(envelope.Result.Restore!.IsolationStaged);
            Assert.False(envelope.Result.Restore.RecoveryVerified);
            Assert.False(envelope.Result.Restore.BusinessReady);
        }
        await using var restarted = await AppAsync();
        using var second = Client(restarted);
        Assert.Equal(HttpStatusCode.OK, (await second.PostAsJsonAsync("/management/v1/service/restores", request, _json.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await second.PostAsJsonAsync("/management/v1/service/start", new ManagementServiceRequest("start", "server", "node"), _json.Options)).StatusCode);
        Assert.Equal(1, _backups.RestoreCalls);
        Assert.Equal(0, _host.StartCalls);
    }

    private async Task<WebApplication> AppAsync(bool credentials = true, bool blockStartup = false)
    {
        var configurationStore = new FileServiceConfigurationStore(Path.Combine(_directory, "configuration.json"));
        if (!File.Exists(Path.Combine(_directory, "configuration.json"))) await configurationStore.SaveAsync(Configuration, 0);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["ExamSecurity:OperatorToken"] = credentials ? Token : null, ["ExamSecurity:OperatorId"] = credentials ? "operator" : null });
        builder.Services.AddSingleton(new ManagementRuntime("server", "node", configurationStore, _host, () => _host,
            new Probe(blockStartup), () => _backups, new FileManagementOperationStore(Path.Combine(_directory, "operations")), new SystemClock()));
        var app = builder.Build();
        app.MapManagementApi();
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(WebApplication app, string? token = Token)
    {
        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost");
        client.DefaultRequestHeaders.Add(ManagementHeaders.ServerId, "server");
        client.DefaultRequestHeaders.Add(ManagementHeaders.NodeId, "node");
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class Probe(bool block) : IStartupReadinessProbe
    {
        public Task<IReadOnlyList<StartupCheckResult>> RunAsync(ServiceConfiguration configuration, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<StartupCheckResult>>([new("synthetic-recovery", !block, "合成检查", true)]);
    }
    private sealed class ControlledHost : IManagedBusinessHost
    {
        public BusinessServiceStatus Status { get; set; } = BusinessServiceStatus.Stopped;
        public int StopCalls { get; private set; }
        public int StartCalls { get; private set; }
        public Task StartAsync(CancellationToken token) { StartCalls++; Status = BusinessServiceStatus.Running; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken token) { StopCalls++; Status = BusinessServiceStatus.Stopped; return Task.CompletedTask; }
    }
    private sealed class StagingBackups : IBackupCoordinator
    {
        public int RestoreCalls { get; private set; }
        public Task<BackupResult> CreateAsync(BackupRequest request, CancellationToken token) => throw new InvalidOperationException("合成备份依赖缺失");
        public Task RestoreAsync(RestoreRequest request, CancellationToken token) { RestoreCalls++; return Task.CompletedTask; }
    }
    private sealed class JsonSerializerOptionsProvider
    { public System.Text.Json.JsonSerializerOptions Options { get; } = JsonDefaults.Create(); }
}
