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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedRequestIsInputError_WithoutServiceOrJournalEffects(bool streamed)
    {
        await using var app = await AppAsync();
        using var client = Client(app);
        var body = System.Text.Encoding.UTF8.GetBytes("{\"requestId\":\"oversized\",\"serverId\":\"server\",\"nodeId\":\"node\",\"padding\":\"" + new string('x', 65536) + "\"}");
        using HttpContent content = streamed ? new UnknownLengthContent(body) : new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var response = await client.PostAsync("/management/v1/service/stop", content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var envelope = await response.Content.ReadFromJsonAsync<ManagementEnvelope<ManagementOperation>>(_json.Options);
        Assert.Equal(ErrorCodes.InvalidInput, envelope!.Error!.Code);
        Assert.Equal(0, _host.StopCalls);
        Assert.Empty(Directory.GetFiles(Path.Combine(_directory, "operations"), "*.json"));
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

    [Fact]
    public async Task StopResultLostAfterSideEffectIsReconciledFromActualProcessWithoutStoppingAgain()
    {
        _host.Status = BusinessServiceStatus.Running;
        await using (var app = await AppAsync(failCompletion: true))
        {
            var runtime = app.Services.GetRequiredService<ManagementRuntime>();
            await Assert.ThrowsAsync<IOException>(() => runtime.ExecuteServiceAsync(new("lost-stop", "server", "node"),
                "operator", ManagementOperationKind.Stop, default));
        }
        await using var restarted = await AppAsync();
        using var client = Client(restarted);
        var envelope = await client.GetFromJsonAsync<ManagementEnvelope<ManagementOperation>>("/management/v1/operations/lost-stop", _json.Options);
        Assert.Equal(ManagementOperationPhase.Completed, envelope!.Result!.Phase);
        Assert.Equal(BusinessServiceStatus.Stopped, envelope.Result.Status!.ProcessStatus);
        Assert.Equal(1, _host.StopCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostConfigurationResultRequiresExactVersionAndContentBeforeReconciliation(bool alterSavedConfiguration)
    {
        var request = new ManagementConfigurationRequest("lost-config", "server", "node", 1, Configuration with { HttpsPort = 9443 });
        await using (var app = await AppAsync(failCompletion: true))
        {
            var runtime = app.Services.GetRequiredService<ManagementRuntime>();
            await Assert.ThrowsAsync<IOException>(() => runtime.ConfigureAsync(request, "operator", default));
        }
        if (alterSavedConfiguration)
            await new FileServiceConfigurationStore(Path.Combine(_directory, "configuration.json"))
                .SaveAsync(request.Configuration with { HttpsPort = 9444, Version = 3 }, 2);
        await using var restarted = await AppAsync();
        var result = await restarted.Services.GetRequiredService<ManagementRuntime>().FindOperationAsync("lost-config", "operator", default);
        Assert.Equal(alterSavedConfiguration ? ManagementOperationPhase.NeedsVerification : ManagementOperationPhase.Completed, result.Phase);
        if (!alterSavedConfiguration) Assert.Equal(request.Configuration with { Version = 2 }, result.Configuration);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostStartResultNeedsFreshBusinessReadinessObservation(bool ready)
    {
        await using (var app = await AppAsync(failCompletion: true))
        {
            await Assert.ThrowsAsync<IOException>(() => app.Services.GetRequiredService<ManagementRuntime>()
                .ExecuteServiceAsync(new("lost-start", "server", "node"), "operator", ManagementOperationKind.Start, default));
        }
        await using var restarted = await AppAsync(observedReady: ready);
        var result = await restarted.Services.GetRequiredService<ManagementRuntime>().FindOperationAsync("lost-start", "operator", default);
        Assert.Equal(ready ? ManagementOperationPhase.Completed : ManagementOperationPhase.NeedsVerification, result.Phase);
        Assert.Equal(1, _host.StartCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostRestoreResultReconcilesOnlyValidatedStaging_AndNeverClaimsBusinessRecovery(bool evidenceAvailable)
    {
        var request = new RestoreRequest("lost-restore", "synthetic-backup", "synthetic-source", "operator");
        await using (var app = await AppAsync(failCompletion: true))
            await Assert.ThrowsAsync<IOException>(() => app.Services.GetRequiredService<ManagementRuntime>().RestoreAsync(request, "operator", default));
        _backups.EvidenceAvailable = evidenceAvailable;
        await using var restarted = await AppAsync();
        var runtime = restarted.Services.GetRequiredService<ManagementRuntime>();
        var observed = await runtime.FindOperationAsync(request.RequestId, "operator", default);
        Assert.Equal(evidenceAvailable ? ManagementOperationPhase.Staged : ManagementOperationPhase.NeedsVerification, observed.Phase);
        if (evidenceAvailable)
        {
            Assert.True(observed.Restore!.IsolationStaged);
            Assert.False(observed.Restore.RecoveryVerified);
            Assert.False(observed.Restore.BusinessReady);
        }
        await runtime.RestoreAsync(request, "operator", default);
        Assert.Equal(1, _backups.RestoreCalls);
        await Assert.ThrowsAsync<DomainRuleException>(() => runtime.ExecuteServiceAsync(new("start-after-restore", "server", "node"),
            "operator", ManagementOperationKind.Start, default));
        Assert.Equal(0, _host.StartCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostBackupResultNeedsValidatedFileEvidence_AndNeverRepeatsBackup(bool evidenceAvailable)
    {
        _backups.CompleteBackup = true;
        var request = new BackupRequest("lost-backup", "synthetic-destination", "operator");
        await using (var app = await AppAsync(failCompletion: true))
            await Assert.ThrowsAsync<IOException>(() => app.Services.GetRequiredService<ManagementRuntime>().BackupAsync(request, "operator", default));
        _backups.EvidenceAvailable = evidenceAvailable;
        await using var restarted = await AppAsync();
        var runtime = restarted.Services.GetRequiredService<ManagementRuntime>();
        var observed = await runtime.FindOperationAsync(request.RequestId, "operator", default);
        Assert.Equal(evidenceAvailable ? ManagementOperationPhase.Completed : ManagementOperationPhase.NeedsVerification, observed.Phase);
        await runtime.BackupAsync(request, "operator", default);
        Assert.Equal(1, _backups.BackupCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreActivationReplaysOneStart_AndLostTerminalWriteIsObservedWithoutRepeatingEffects(bool loseTerminal)
    {
        var request = new RestoreRequest("activate", "backup", _directory, "operator");
        var observations = 0;
        Task<ManagementOperation?> Observe(ManagementOperation op, CancellationToken token)
        {
            observations++;
            Assert.Equal(BusinessServiceStatus.Running, _host.Status);
            Assert.Equal(RecoveryActivationPhase.StartingBusiness, op.RecoveryActivation);
            Assert.True(new FileRecoveryDeploymentSwitch(new FileRecoveryDeploymentSelection(Path.Combine(_directory, "deployment.json")), 1).IsSelected(op));
            return Task.FromResult<ManagementOperation?>(op with { Phase = ManagementOperationPhase.Completed,
                RecoveryActivation = RecoveryActivationPhase.BusinessReady, Restore = op.Restore! with { BusinessReady = true }, Error = null });
        }
        Task<IReadOnlyList<StartupCheckResult>> Verify(RestoreRequest _, ServiceConfiguration __, CancellationToken ___) =>
            Task.FromResult<IReadOnlyList<StartupCheckResult>>([new("recovery-target", true, "合成目标", true)]);
        await using (var first = await AppAsync(failCompletion: loseTerminal, failOnlyTerminal: true, prepareWorkingCopy: (_, _) => Task.CompletedTask,
            verifyTarget: Verify, workingDirectory: () => _directory, activateRecovery: true))
        {
            using var client = Client(first);
            var response = await client.PostAsJsonAsync("/management/v1/service/restores", request, _json.Options);
            Assert.Equal(loseTerminal ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, response.StatusCode);
            var saved = await new FileManagementOperationStore(Path.Combine(_directory, "operations")).FindAsync("activate", default);
            Assert.Equal(loseTerminal ? ManagementOperationPhase.NeedsVerification : ManagementOperationPhase.Completed, saved!.Phase);
        }
        await using var restarted = await AppAsync(prepareWorkingCopy: (_, _) => throw new InvalidOperationException("不得重新复制"),
            verifyTarget: Verify, workingDirectory: () => _directory, activateRecovery: true, observeRecovery: Observe);
        using var replayClient = Client(restarted);
        var replay = await replayClient.PostAsJsonAsync("/management/v1/service/restores", request, _json.Options);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var result = (await replay.Content.ReadFromJsonAsync<ManagementEnvelope<ManagementOperation>>(_json.Options))!.Result!;
        Assert.Equal(ManagementOperationPhase.Completed, result.Phase);
        Assert.True(result.Restore!.BusinessReady);
        Assert.Equal(1, _host.StartCalls);
        Assert.Equal(1, _backups.RestoreCalls);
        Assert.Equal(loseTerminal ? 1 : 0, observations);
    }

    private async Task<WebApplication> AppAsync(bool credentials = true, bool blockStartup = false, bool failCompletion = false, bool observedReady = false,
        bool deploymentBuilder = false, Func<RestoreRequest, CancellationToken, Task>? prepareWorkingCopy = null,
        Func<RestoreRequest, ServiceConfiguration, CancellationToken, Task<IReadOnlyList<StartupCheckResult>>>? verifyTarget = null,
        Func<string>? workingDirectory = null, bool activateRecovery = false,
        Func<ManagementOperation, CancellationToken, Task<ManagementOperation?>>? observeRecovery = null, bool failOnlyTerminal = false)
    {
        var configurationStore = new FileServiceConfigurationStore(Path.Combine(_directory, "configuration.json"));
        if (!File.Exists(Path.Combine(_directory, "configuration.json"))) await configurationStore.SaveAsync(Configuration, 0);
        var builder = deploymentBuilder ? ManagementEntryPoint.CreateHostBuilder([]) : WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["ExamSecurity:OperatorToken"] = credentials ? Token : null, ["ExamSecurity:OperatorId"] = credentials ? "operator" : null });
        IManagementOperationStore journal = new FileManagementOperationStore(Path.Combine(_directory, "operations"));
        if (failCompletion) journal = new FailCompletionStore(journal, failOnlyTerminal);
        builder.Services.AddSingleton(new ManagementRuntime("server", "node", configurationStore, _host, () => _host,
            new Probe(blockStartup), () => _backups, journal, new SystemClock(), observeBusinessReady: _ => Task.FromResult(observedReady),
            prepareRecoveryWorkingCopy: prepareWorkingCopy, verifyRecoveryTarget: verifyTarget, recoveryWorkingDirectory: workingDirectory,
            activateRecovery: activateRecovery ? (op, token) => new RecoveryActivationRunner(_host, () => _host,
                new FileRecoveryDeploymentSwitch(new FileRecoveryDeploymentSelection(Path.Combine(_directory, "deployment.json")), 1), journal)
                .ActivateAsync(op, token) : null, observeRecoveryActivation: observeRecovery));
        var app = builder.Build();
        app.MapManagementApi();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task RestoreContinuationPersistsStagingBeforeWorkingCopy_AndReusesOriginalRequestAcrossRestart()
    {
        var copies = 0;
        var store = new FileManagementOperationStore(Path.Combine(_directory, "operations"));
        var request = new RestoreRequest("continue-restore", "synthetic-backup", "/synthetic-backup", "operator");
        async Task Prepare(RestoreRequest original, CancellationToken token)
        {
            Assert.Equal(request, original);
            var persisted = await store.FindAsync(request.RequestId, token);
            Assert.Equal(ManagementOperationPhase.Staged, persisted!.Phase);
            Assert.True(persisted.Restore!.IsolationStaged);
            Assert.False(persisted.Restore.BusinessReady);
            copies++;
        }
        await using (var first = await AppAsync(prepareWorkingCopy: Prepare))
        {
            using var client = Client(first);
            var response = await client.PostAsJsonAsync("/management/v1/service/restores", request, _json.Options);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var operation = (await response.Content.ReadFromJsonAsync<ManagementEnvelope<ManagementOperation>>(_json.Options))!.Result!;
            Assert.Contains(operation.Checks!, c => c.Name == "recovery-working-copy" && c.Passed);
        }
        await using var second = await AppAsync(prepareWorkingCopy: Prepare);
        using var next = Client(second);
        Assert.Equal(HttpStatusCode.OK, (await next.GetAsync("/management/v1/operations/continue-restore")).StatusCode);
        Assert.Equal(1, copies); // Queries never resume side effects.
        Assert.Equal(HttpStatusCode.OK, (await next.PostAsJsonAsync("/management/v1/service/restores", request, _json.Options)).StatusCode);
        Assert.Equal(2, copies); // Explicit continuation observes the same bound working-copy marker; no new staging.
        Assert.Equal(1, _backups.RestoreCalls);
        Assert.Equal(HttpStatusCode.Conflict, (await next.PostAsJsonAsync("/management/v1/service/restores",
            request with { SourceDirectory = "/other" }, _json.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await next.PostAsJsonAsync("/management/v1/service/start",
            new ManagementServiceRequest("bypass-restore", "server", "node"), _json.Options)).StatusCode);
        Assert.Equal(0, _host.StartCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RecoveryVerificationIsPersistedSeparatelyFromBusinessReadiness(bool verified)
    {
        await using var app = await AppAsync(prepareWorkingCopy: (_, _) => Task.CompletedTask,
            verifyTarget: (_, _, _) => Task.FromResult<IReadOnlyList<StartupCheckResult>>(
                [new("recovery-target", verified, "合成目标核对", true)]));
        using var client = Client(app);
        var response = await client.PostAsJsonAsync("/management/v1/service/restores",
            new RestoreRequest("verify-target", "synthetic-backup", "synthetic-source", "operator"), _json.Options);
        var result = (await response.Content.ReadFromJsonAsync<ManagementEnvelope<ManagementOperation>>(_json.Options))!.Result!;
        Assert.Equal(ManagementOperationPhase.Staged, result.Phase);
        Assert.True(result.Restore!.IsolationStaged);
        Assert.Equal(verified, result.Restore.RecoveryVerified);
        Assert.False(result.Restore.BusinessReady);
        var persisted = await new FileManagementOperationStore(Path.Combine(_directory, "operations")).FindAsync("verify-target", default);
        Assert.Equal(result.Restore, persisted!.Restore);
        Assert.Equal(0, _host.StartCalls);
    }

    [Fact]
    public async Task DeploymentTargetChangeCannotRedirectAnExistingRestore()
    {
        var target = Path.Combine(_directory, "first-slot");
        var copies = 0;
        await using var app = await AppAsync(prepareWorkingCopy: (_, _) => { copies++; return Task.CompletedTask; },
            workingDirectory: () => target);
        using var client = Client(app);
        var request = new RestoreRequest("fixed-target", "synthetic-backup", "synthetic-source", "operator");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/management/v1/service/restores", request, _json.Options)).StatusCode);
        target = Path.Combine(_directory, "another-slot");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/management/v1/service/restores", request, _json.Options)).StatusCode);
        var persisted = await new FileManagementOperationStore(Path.Combine(_directory, "operations")).FindAsync("fixed-target", default);
        Assert.Equal(Path.Combine(_directory, "first-slot"), persisted!.RecoveryWorkingDirectory);
        Assert.Equal(ManagementOperationPhase.NeedsVerification, persisted.Phase);
        Assert.Equal(1, copies);
        Assert.Equal(1, _backups.RestoreCalls);
    }

    [Fact]
    public async Task ProductionHostBuilderRetainsConsoleLifecycleAndIndependentManagementRoutes()
    {
        await using (var consoleHost = ManagementEntryPoint.CreateHostBuilder([]).Build())
        {
            Assert.Equal("ConsoleLifetime", consoleHost.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostLifetime>().GetType().Name);
            Assert.Empty(consoleHost.Services.GetServices<Microsoft.Extensions.Logging.ILoggerProvider>());
        }
        await using var app = await AppAsync(deploymentBuilder: true);
        Assert.Equal("Exam.Management", app.Environment.ApplicationName);
        // TestServer deliberately replaces the console lifetime; only transport is substituted here.
        Assert.Empty(app.Services.GetServices<Microsoft.Extensions.Logging.ILoggerProvider>());
        using var client = new LocalManagementClient(new Uri("https://localhost:9443"), "server", "node", () => Token,
            app.GetTestServer().CreateHandler());
        Assert.Equal(BusinessServiceStatus.Stopped, (await client.GetStatusAsync()).Envelope.Result!.ProcessStatus);
        await app.StopAsync();
    }

    [Fact]
    public async Task SharedClientAndCliOperateWithoutBusinessConnection_AndReplayStop()
    {
        await using var app = await AppAsync();
        await File.WriteAllTextAsync(Path.Combine(_directory, "configuration.json"), "{}");
        _host.Status = BusinessServiceStatus.Running;
        using var client = new LocalManagementClient(new Uri("https://localhost:9443"), "server", "node", () => Token,
            app.GetTestServer().CreateHandler());
        var handler = new LocalManagementCliHandler(client);
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(0, await handler.RunAsync("status", null, null, output, errors));
        Assert.Contains("service-configuration", output.ToString());
        var request = System.Text.Json.JsonSerializer.Serialize(new ManagementServiceRequest("cli-stop", "server", "node"), _json.Options);
        Assert.Equal(0, await handler.RunAsync("stop", request, null, output, errors));
        Assert.Equal(0, await handler.RunAsync("stop", request, null, output, errors));
        Assert.Equal(0, await handler.RunAsync("operation", null, "cli-stop", output, errors));
        Assert.Equal(1, _host.StopCalls);
        Assert.Equal("", errors.ToString());
    }

    [Theory]
    [InlineData("restore", false, 4)]
    [InlineData("checks", true, 6)]
    [InlineData("start", true, 6)]
    [InlineData("stop", false, 3)]
    public async Task CliPreservesRecoveryAndFailureSemantics(string command, bool blocked, int expectedExit)
    {
        await using var app = await AppAsync(blockStartup: blocked);
        using var client = new LocalManagementClient(new Uri("https://localhost:9443"), "server", "node", () => Token,
            app.GetTestServer().CreateHandler());
        var input = command == "restore" ? System.Text.Json.JsonSerializer.Serialize(new RestoreRequest("cli-restore", "backup", "synthetic-path", "operator"), _json.Options) :
            "{\"requestId\":\"cli-request\",\"serverId\":\"server\",\"nodeId\":\"node\"" + (command == "stop" ? ",\"actorId\":\"forged\"}" : "}");
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(expectedExit, await new LocalManagementCliHandler(client).RunAsync(command, input, null, output, errors));
        Assert.Equal("", output.ToString());
        Assert.NotEmpty(errors.ToString());
        if (command == "restore")
        {
            Assert.Contains("\"recoveryVerified\":false", errors.ToString());
            Assert.Contains("\"businessReady\":false", errors.ToString());
        }
        Assert.Equal(0, _host.StartCalls);
        Assert.Equal(0, _host.StopCalls);
    }

    [Fact]
    public async Task CliConfigurationUsesPersistedVersion_AndReportsConflict()
    {
        await using var app = await AppAsync();
        using var client = new LocalManagementClient(new Uri("https://localhost:9443"), "server", "node", () => Token,
            app.GetTestServer().CreateHandler());
        using var output = new StringWriter();
        using var errors = new StringWriter();
        var handler = new LocalManagementCliHandler(client);
        var request = new ManagementConfigurationRequest("cli-config", "server", "node", 1, Configuration with { HttpsPort = 9444 });
        Assert.Equal(0, await handler.RunAsync("configure", System.Text.Json.JsonSerializer.Serialize(request, _json.Options), null, output, errors));
        Assert.Equal(4, await handler.RunAsync("configure", System.Text.Json.JsonSerializer.Serialize(request with { RequestId = "cli-conflict" }, _json.Options), null, output, errors));
        Assert.Equal(2, (await client.GetConfigurationAsync()).Envelope.Result!.Version);
    }

    [Fact]
    public async Task ProductionTransportRejectsUntrustedCertificate_BeforeSendingManagementMutation()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=localhost", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var names = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(10));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
        await using var app = builder.Build();
        var mutations = 0;
        app.MapPost("/management/v1/service/stop", () => { mutations++; return Results.Ok(); });
        await app.StartAsync();
        var address = Assert.Single(app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses);
        using var client = new LocalManagementClient(new Uri(address), "server", "node", () => Token);
        using var output = new StringWriter();
        using var errors = new StringWriter();
        Assert.Equal(5, await new LocalManagementCliHandler(client).RunAsync("stop",
            "{\"requestId\":\"tls-stop\",\"serverId\":\"server\",\"nodeId\":\"node\"}", null, output, errors));
        Assert.Equal(0, mutations);
        Assert.Equal("", output.ToString());
        Assert.DoesNotContain(Token, errors.ToString());
        await app.StopAsync();
    }

    [Theory]
    [InlineData("missing", true)]
    [InlineData("corrupt", true)]
    [InlineData("valid", true)]
    [InlineData("missing", false)]
    public async Task ProductionRegistrationDoesNotLoadBrokenBusinessDependenciesBeforeAuthenticationOrStatus(string configurationState, bool credentials)
    {
        var selection = Path.Combine(_directory, "corrupt-deployment.json");
        await File.WriteAllTextAsync(selection, "{}");
        var path = Path.Combine(_directory, "production-configuration.json");
        if (configurationState == "valid") await new FileServiceConfigurationStore(path).SaveAsync(Configuration, 0);
        else if (configurationState == "corrupt") await File.WriteAllTextAsync(path, "{}");
        var builder = ManagementEntryPoint.CreateHostBuilder([]);
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Management:ServerId"] = "server", ["Management:NodeId"] = "node", ["Management:HttpsPort"] = "9443",
            ["Management:BusinessServiceName"] = "invalid/service", // Invalid before SCM access on every platform.
            ["Management:ServiceConfigurationPath"] = path,
            ["Management:OperationDirectory"] = Path.Combine(_directory, "missing-operation-directory"),
            ["Management:BusinessReadyEndpoint"] = "http://invalid-ready-endpoint",
            ["ConnectionStrings:Exam"] = "synthetic-invalid-connection-secret",
            ["Recovery:SelectionPath"] = selection,
            ["Backup:ToolTimeoutMinutes"] = "not-an-integer",
            ["ExamSecurity:OperatorToken"] = credentials ? Token : null,
            ["ExamSecurity:OperatorId"] = credentials ? "operator" : null
        });
        ManagementEntryPoint.AddManagementRuntime(builder);
        await using var app = builder.Build();
        app.MapManagementApi();
        await app.StartAsync();
        using var client = Client(app);
        var response = await client.GetAsync("/management/v1/service/status");
        Assert.Equal(credentials ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("synthetic-invalid-connection-secret", body);
        Assert.DoesNotContain(Token, body);
        var envelope = System.Text.Json.JsonSerializer.Deserialize<ManagementEnvelope<ManagementStatus>>(body, _json.Options)!;
        Assert.Null(envelope.ServerEpoch);
        Assert.False(envelope.EpochVerified);
        if (credentials)
        {
            Assert.Equal(BusinessServiceStatus.Faulted, envelope.Result!.ProcessStatus);
            Assert.Equal(configurationState == "valid", envelope.Result.Configuration is not null);
            if (configurationState != "valid") Assert.Contains(envelope.Result.Checks, check => !check.Passed && check.BlocksWrites);
            var configured = await client.GetAsync("/management/v1/service/configuration");
            Assert.Equal(configurationState == "valid" ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, configured.StatusCode);
        }
        Assert.False(Directory.Exists(Path.Combine(_directory, "missing-operation-directory")));
        await app.StopAsync();
    }

    [Fact]
    public async Task ProductionChecksPersistMissingBackupFailure_WithoutLoadingBackupToolsOrDatabaseSecrets()
    {
        var path = Path.Combine(_directory, "checks-configuration.json");
        await new FileServiceConfigurationStore(path).SaveAsync(Configuration, 0);
        var builder = ManagementEntryPoint.CreateHostBuilder([]);
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Management:ServerId"] = "server", ["Management:NodeId"] = "node", ["Management:HttpsPort"] = "9443",
            ["Management:ServiceConfigurationPath"] = path,
            ["Management:OperationDirectory"] = Path.Combine(_directory, "operations"),
            ["ConnectionStrings:Exam"] = "synthetic-invalid-connection-secret",
            ["Backup:ToolTimeoutMinutes"] = "not-an-integer",
            ["ExamSecurity:OperatorToken"] = Token, ["ExamSecurity:OperatorId"] = "operator"
        });
        ManagementEntryPoint.AddManagementRuntime(builder);
        await using var app = builder.Build();
        app.MapManagementApi();
        await app.StartAsync();
        using var client = Client(app);
        var response = await client.PostAsJsonAsync("/management/v1/service/checks",
            new ManagementServiceRequest("production-backup-check", "server", "node"), _json.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("synthetic-invalid-connection-secret", body);
        Assert.DoesNotContain(Token, body);
        var operation = (await response.Content.ReadFromJsonAsync<ManagementEnvelope<ManagementOperation>>(_json.Options))!.Result!;
        Assert.Equal(ManagementOperationPhase.Completed, operation.Phase); // Check completed, service is not ready.
        Assert.Contains(operation.Checks!, check => check.Name == "backup-integrity" && !check.Passed && check.BlocksWrites);
        var replay = await client.GetFromJsonAsync<ManagementEnvelope<ManagementOperation>>(
            "/management/v1/operations/production-backup-check", _json.Options);
        Assert.Contains(replay!.Result!.Checks!, check => check.Name == "backup-integrity" && !check.Passed && check.BlocksWrites);
        await app.StopAsync();
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
    private sealed class UnknownLengthContent(byte[] body) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
    private sealed class ControlledHost : IManagedBusinessHost
    {
        public BusinessServiceStatus Status { get; set; } = BusinessServiceStatus.Stopped;
        public int StopCalls { get; private set; }
        public int StartCalls { get; private set; }
        public Task StartAsync(CancellationToken token) { StartCalls++; Status = BusinessServiceStatus.Running; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken token) { StopCalls++; Status = BusinessServiceStatus.Stopped; return Task.CompletedTask; }
    }
    private sealed class StagingBackups : IBackupCoordinator, IBackupOperationEvidence
    {
        public int RestoreCalls { get; private set; }
        public int BackupCalls { get; private set; }
        public bool CompleteBackup { get; set; }
        public bool EvidenceAvailable { get; set; }
        public Task<BackupResult> CreateAsync(BackupRequest request, CancellationToken token)
        {
            BackupCalls++;
            return CompleteBackup ? Task.FromResult(Result(request)) : throw new InvalidOperationException("合成备份依赖缺失");
        }
        public Task RestoreAsync(RestoreRequest request, CancellationToken token) { RestoreCalls++; return Task.CompletedTask; }
        public Task<BackupResult?> FindCompletedBackupAsync(BackupRequest request, CancellationToken token) =>
            Task.FromResult<BackupResult?>(EvidenceAvailable ? Result(request) : null);
        public Task<bool> FindStagedRestoreAsync(RestoreRequest request, CancellationToken token) => Task.FromResult(EvidenceAvailable);
        private static BackupResult Result(BackupRequest request) => new("synthetic-backup", request.DestinationDirectory,
            new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero), true, true, true);
    }
    private sealed class JsonSerializerOptionsProvider
    { public System.Text.Json.JsonSerializerOptions Options { get; } = JsonDefaults.Create(); }
    private sealed class FailCompletionStore(IManagementOperationStore inner, bool onlyTerminal = false) : IManagementOperationStore
    {
        public Task<IAsyncDisposable> AcquireAsync(CancellationToken token) => inner.AcquireAsync(token);
        public Task<ManagementOperation?> FindAsync(string id, CancellationToken token) => inner.FindAsync(id, token);
        public Task<bool> HasUnverifiedOperationsAsync(CancellationToken token) => inner.HasUnverifiedOperationsAsync(token);
        public Task SaveAsync(ManagementOperation operation, CancellationToken token) => operation.Phase == ManagementOperationPhase.Accepted ||
            onlyTerminal && operation.Phase != ManagementOperationPhase.Completed
            ? inner.SaveAsync(operation, token) : throw new IOException("合成结果落盘中断");
    }
}
