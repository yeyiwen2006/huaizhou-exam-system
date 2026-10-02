using Exam.Contracts.ServiceManagement;
using Exam.Contracts.HighAvailability;
using Exam.Infrastructure.HighAvailability;
using Exam.Infrastructure.ServiceManagement;
using Exam.IntegrationTests.Postgres;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Exam.IntegrationTests.ServiceManagement;

[Trait("Category", "Postgres")]
[Collection(PostgresCollection.Name)]
public sealed class ManagedProtectionReadinessTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _database = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "managed-protection-" + Guid.NewGuid().ToString("N"));
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        await _database.InitializeAsync();
    }

    [Fact]
    public async Task RunningManagementHostChecksNewlySelectedDatabaseAndEpochWithoutRestart()
    {
        await using (var state = _database.DataSource.CreateCommand("""
            BEGIN;
            INSERT INTO exam.server_state VALUES ('server', 'node', 2, 'EmergencySingleNode', 2, clock_timestamp());
            INSERT INTO exam.server_nodes VALUES ('server', 'node', 'Primary', true, false, 0, true, true, clock_timestamp());
            COMMIT;
            """)) await state.ExecuteNonQueryAsync();
        var configurationPath = Path.Combine(_directory, "service.json");
        var selectionPath = Path.Combine(_directory, "selected.json");
        var confirmationRoot = Path.Combine(_directory, "confirmation");
        Directory.CreateDirectory(Path.Combine(_directory, "operations"));
        await new FileServiceConfigurationStore(configurationPath).SaveAsync(new("server", "node", "127.0.0.1", 8443, _directory, true, 1), 0);
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["Management:ServerId"] = "server", ["Management:NodeId"] = "node", ["Management:HttpsPort"] = "9443",
            ["Management:ServiceConfigurationPath"] = configurationPath, ["Management:OperationDirectory"] = Path.Combine(_directory, "operations"),
            ["Management:MinimumAvailableBytes"] = "1", ["ExamServer:ServerEpoch"] = "1",
            ["ConnectionStrings:Exam"] = "synthetic-invalid-old-database", ["HighAvailability:SelectionPath"] = selectionPath,
            ["HighAvailability:Nodes:node:ConnectionString"] = _database.ConnectionString,
            ["HighAvailability:Nodes:node:ResourcesDirectory"] = _directory,
            ["HighAvailability:Nodes:node:CertificateThumbprint"] = "synthetic-unavailable-certificate",
            ["HighAvailability:Nodes:node:ConfirmationDirectory"] = confirmationRoot,
            ["HighAvailability:Nodes:peer:ConnectionString"] = "Host=synthetic-peer.example.test;Database=synthetic",
            ["HighAvailability:Nodes:peer:BusinessHttpsUrl"] = "https://synthetic-peer.example.test/" });
        Exam.Management.ManagementEntryPoint.AddManagementRuntime(builder);
        await using var app = builder.Build();
        var runtime = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Exam.Management.ManagementRuntime>(app.Services);
        var before = await runtime.ExecuteServiceAsync(new("before", "server", "node"), "synthetic-operator", ManagementOperationKind.Checks, default);
        Assert.Contains(before.Checks!, c => c.Name == "database" && !c.Passed);
        await new Exam.Infrastructure.HighAvailability.FileFailoverDeploymentSelection(selectionPath).SaveAsync(
            new("server", "node", "node", "peer", 2, 2, Exam.Contracts.HighAvailability.ProtectionMode.EmergencySingleNode,
                "synthetic-takeover", Path.Combine(confirmationRoot, "epochs", "2", "node")), default);
        var after = await runtime.ExecuteServiceAsync(new("after", "server", "node"), "synthetic-operator", ManagementOperationKind.Checks, default);
        Assert.Equal(ManagementOperationPhase.Completed, after.Phase);
        Assert.Contains(after.Checks!, c => c.Name == "database" && c.Passed);
        Assert.Contains(after.Checks!, c => c.Name == "database-node-epoch" && c.Passed);
    }

    [Fact]
    public async Task EveryReadinessObservationUsesCurrentProtectionAndNeverChangesAuthority()
    {
        await using (var create = _database.DataSource.CreateCommand("""
            BEGIN;
            INSERT INTO exam.server_state VALUES ('server', 'node', 1, 'EmergencySingleNode', 1, clock_timestamp());
            INSERT INTO exam.server_nodes VALUES ('server', 'node', 'Primary', true, false, 0, true, true, clock_timestamp());
            COMMIT;
            """)) await create.ExecuteNonQueryAsync();
        var protectedHost = await ConfigureAsync(true, "dual.json");
        var unrestrictedHost = await ConfigureAsync(false, "existing-mode.json");

        Assert.False((await protectedHost.CheckProtectionAsync(_database.DataSource, default)).Passed);
        Assert.True((await unrestrictedHost.CheckProtectionAsync(_database.DataSource, default)).Passed);
        await using (var read = _database.DataSource.CreateCommand("SELECT protection_mode FROM exam.server_state WHERE server_id='server'"))
            Assert.Equal("EmergencySingleNode", await read.ExecuteScalarAsync());

        await using (var update = _database.DataSource.CreateCommand("UPDATE exam.server_state SET protection_mode='SynchronousDualNode' WHERE server_id='server'"))
            await update.ExecuteNonQueryAsync();
        Assert.True((await protectedHost.CheckProtectionAsync(_database.DataSource, default)).Passed);
        await using (var update = _database.DataSource.CreateCommand("UPDATE exam.server_state SET protection_mode='EmergencySingleNode' WHERE server_id='server'"))
            await update.ExecuteNonQueryAsync();
        Assert.False((await protectedHost.CheckProtectionAsync(_database.DataSource, default)).Passed);
        await using (var read = _database.DataSource.CreateCommand("SELECT protection_mode FROM exam.server_state WHERE server_id='server'"))
            Assert.Equal("EmergencySingleNode", await read.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ExplicitEmergencyRequiresProtectedCurrentSelectionAndMatchingActualAudit()
    {
        await using (var create = _database.DataSource.CreateCommand("""
            BEGIN;
            INSERT INTO exam.server_state VALUES ('server','node',1,'EmergencySingleNode',1,clock_timestamp());
            INSERT INTO exam.server_nodes VALUES ('server','node','Primary',true,false,0,true,true,clock_timestamp());
            COMMIT;
            """))
            await create.ExecuteNonQueryAsync();
        var confirmation = Path.Combine(_directory, "confirmation");
        var selectionPath = Path.Combine(_directory, "selection.json");
        var selection = new FailoverDeploymentSelection("server", "node", "node", "peer", 1, 1,
            ProtectionMode.EmergencySingleNode, "synthetic-emergency", Path.Combine(confirmation, "epochs", "1", "node"));
        await new FileFailoverDeploymentSelection(selectionPath).SaveAsync(selection, default);
        var service = await ConfigureAsync(true, "synthetic-emergency-config.json", selectionPath, confirmation);
        Assert.False((await service.CheckProtectionAsync(_database.DataSource, default)).Passed);
        await using (var audit = _database.DataSource.CreateCommand("""
            INSERT INTO exam.failover_audit(server_id,action,actor_id,reason,node_id,server_epoch,occurred_at)
            VALUES('server','F02:Emergency:synthetic-emergency','synthetic-operator','synthetic authorized decision','node',1,clock_timestamp())
            """)) await audit.ExecuteNonQueryAsync();
        Assert.True((await service.CheckProtectionAsync(_database.DataSource, default)).Passed);
        await using (var stale = _database.DataSource.CreateCommand("UPDATE exam.server_state SET server_epoch=2 WHERE server_id='server'"))
            await stale.ExecuteNonQueryAsync();
        Assert.False((await service.CheckProtectionAsync(_database.DataSource, default)).Passed);
        await using (var read = _database.DataSource.CreateCommand("SELECT protection_mode FROM exam.server_state WHERE server_id='server'"))
            Assert.Equal("EmergencySingleNode", await read.ExecuteScalarAsync());
    }

    private async Task<BusinessServiceConfiguration> ConfigureAsync(bool requireDual, string filename,
        string? selectionPath = null, string? confirmationRoot = null)
    {
        var path = Path.Combine(_directory, filename);
        await new FileServiceConfigurationStore(path).SaveAsync(new ServiceConfiguration("server", "node", "127.0.0.1", 8443,
            _directory, requireDual, 1), 0);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExamServer:ServerId"] = "server", ["ExamServer:NodeId"] = "node", ["ExamServer:ServerEpoch"] = "1",
            ["Management:ServiceConfigurationPath"] = path, ["Management:MinimumAvailableBytes"] = "1",
            ["HighAvailability:SelectionPath"] = selectionPath,
            ["HighAvailability:Nodes:node:ConfirmationDirectory"] = confirmationRoot
        });
        return (await BusinessServiceConfiguration.LoadAndApplyAsync(builder))!;
    }

    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }
}
