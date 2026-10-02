using System.Net;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Exam.Application.HighAvailability;
using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Contracts.Resources;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.HighAvailability;
using Exam.Infrastructure.Postgres.HighAvailability;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.ServiceManagement;
using Exam.Infrastructure.Security;
using Exam.IntegrationTests.Postgres;
using Exam.IntegrationTests.ServiceManagement;
using Npgsql;

namespace Exam.IntegrationTests.HighAvailability;

[Trait("Category", "Postgres")]
[Trait("Category", "PostgresPhysical")]
[Collection(PostgresCollection.Name)]
public sealed class NativeManagementFailoverTests
{
    [NativeConfirmationFact]
    public async Task ActualStreamingPromotionRebuildAndReprotectionUseDurableManagementApi()
    {
        await NativePhysicalPostgresTools.ValidateConfiguredSourceAsync();
        await using var seedLease = new DatabaseLease(); var seed = seedLease.Value; await seed.InitializeAsync();
        var bin = Environment.GetEnvironmentVariable("EXAM_TEST_NATIVE_POSTGRES_BIN")!;
        var root = Path.Combine(Path.GetTempPath(), "f04-native-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var runner = new ExternalProcessRunner();
        using var key = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=Synthetic-F02-Node", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var portA = Port(); var portB = Port(); var dataA = Path.Combine(root, "a-database"); var dataB = Path.Combine(root, "b-database");
        var seedConnection = new NpgsqlConnectionStringBuilder(seed.ConnectionString);
        string Connection(int port) => new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = port, Username = "postgres",
            Database = seedConnection.Database, Password = seedConnection.Password, SslMode = SslMode.Disable,
            Pooling = false, Timeout = 3, CommandTimeout = 10 }.ConnectionString;
        var hostA = new NativeHost(bin, dataA, portA, root); var hostB = new NativeHost(bin, dataB, portB, root);
        await using var sourceA = NpgsqlDataSource.Create(Connection(portA)); await using var sourceB = NpgsqlDataSource.Create(Connection(portB));
        var nodeA = Node("node-a", Connection(portA), "a"); var nodeB = Node("node-b", Connection(portB), "b");
        var deployments = new Dictionary<string, FailoverNodeDeployment> { [nodeA.NodeId] = nodeA, [nodeB.NodeId] = nodeB };
        var peerTransport = new PeerTransport();
        using var peers = new HttpClient(peerTransport) { Timeout = TimeSpan.FromSeconds(10) };
        // Certificate loading and Windows SCM are synthetic adapters. All database, WAL, file,
        // pg_basebackup, pg_verifybackup, promotion and HTTP management effects below are actual.
        var materialsA = Materials(sourceA); var materialsB = Materials(sourceB);
        var optionsA = Options("node-a", dataA); var optionsB = Options("node-b", dataB);
        var platformA = new PostgresHighAvailabilityManagementPlatform(optionsA, peers, materialsA, runner,
            new Listener(), hostA, verifyDeployment: hostA.VerifyBinding);
        var platformB = new PostgresHighAvailabilityManagementPlatform(optionsB, peers, materialsB, runner,
            new Listener(), hostB, verifyDeployment: hostB.VerifyBinding);
        peerTransport.Ready = async (node, token) =>
        {
            var options = node == "node-a" ? optionsA : optionsB; var deployment = deployments[node]; var source = node == "node-a" ? sourceA : sourceB;
            var state = await new PostgresFailoverStateStore(source).LoadAsync("server", token) ?? throw new IOException("Synthetic state missing.");
            var checks = await (node == "node-a" ? materialsA : materialsB).RunAsync(new("server", node, "127.0.0.1", 1, deployment.ResourcesDirectory, true, 1), token);
            return new("server", node, state.ServerEpoch, checks.All(c => c.Passed), checks.Any(c => c.Name == "node-certificate-private-key" && c.Passed), DateTimeOffset.UtcNow);
        };
        peerTransport.Witness = node =>
        {
            var options = node == "node-a" ? optionsA : optionsB; var deployment = deployments[node]; var source = node == "node-a" ? sourceA : sourceB;
            var selection = new FileFailoverDeploymentSelection(options.SelectionPath).Read();
            return new(source, new FileConfirmationStore(selection?.ConfirmationDirectory ?? deployment.ConfirmationDirectory), "server", node,
                selection?.ActiveNodeId ?? "node-a", selection?.Epoch ?? 1, deployment.ResourcesDirectory, new NoPackages(), deployment.ConnectionString, bin);
        };
        try
        {
            await Backup(seedConnection, dataA, false, "node-a");
            await File.WriteAllTextAsync(Path.Combine(dataA, "recovery.signal"), "");
            await hostA.StartAsync(default);
            await Wait(async () => !await Recovery(sourceA));
            await new PostgresFailoverStateStore(sourceA).SaveAsync(new("server", "node-a", 1, ProtectionMode.SynchronousDualNode,
                new Dictionary<string, NodeState> { ["node-a"] = new("node-a", DeploymentNodeRole.Primary, true, false, 0, true, true, DateTimeOffset.UtcNow),
                    ["node-b"] = new("node-b", DeploymentNodeRole.Standby, true, true, 0, true, true, DateTimeOffset.UtcNow) }, 1, []));
            await Backup(new(Connection(portA)), dataB, true, "node-b"); await hostB.StartAsync(default);
            await Wait(async () => await Recovery(sourceB) && await Scalar(sourceA, "SELECT count(*) FROM pg_stat_replication WHERE application_name='node-b' AND state='streaming'") == 1);
            await Sql(sourceA, "ALTER SYSTEM SET synchronous_standby_names='FIRST 1 (\"node-b\")'"); await Sql(sourceA, "SELECT pg_reload_conf()");
            await Wait(async () => await Scalar(sourceA, "SELECT count(*) FROM pg_stat_replication WHERE sync_state='sync'") == 1);
            var peer = new HttpConfirmationPeer(peers, new(nodeB.BusinessHttpsUrl), "server", "node-b", () => optionsA.PeerToken);
            await new PostgresBusinessConfirmation(sourceA, new(nodeA.ConfirmationDirectory), "server", "node-a", "node-b", 1, nodeA.ResourcesDirectory, peer).ConfirmAsync(default);
            await Wait(async () => (await platformB.ObserveAsync(default)).SynchronousProtectionVerified);
            var initial = await platformB.ObserveAsync(default);
            Assert.True(initial.SynchronousProtectionVerified, JsonSerializer.Serialize(initial, JsonDefaults.Create()));

            // The old database is truly stopped; its independent, latest protected confirmation head remains readable.
            await hostA.StopAsync(default);
            await Wait(async () => (await platformB.ObserveAsync(default)).State?.Nodes["node-b"].ReplayLagBytes == 0);
            await using var apiB = await ManagementFailoverTestHost.CreateAsync(Path.Combine(root, "b-management"), platformB);
            using var clientB = apiB.Client();
            var takeover = await Operation(clientB, "takeover", new("takeover", "server", "node-b", "operator", 1, 1, "node-b", "synthetic old primary stopped",
                new("node-a", true, false, "operator", "synthetic old primary stopped")));
            Assert.Equal(2, takeover.HighAvailability!.State!.ServerEpoch);
            Assert.False(await Recovery(sourceB)); Assert.Equal(ProtectionMode.EmergencySingleNode, takeover.HighAvailability.State.ProtectionMode);
            var replay = await Operation(clientB, "takeover", new("takeover", "server", "node-b", "operator", 1, 1, "node-b", "synthetic old primary stopped",
                new("node-a", true, false, "operator", "synthetic old primary stopped")));
            Assert.Equal(takeover.ContentHash, replay.ContentHash);
            await Operation(clientB, "emergency", new("emergency", "server", "node-b", "operator", 2, 2, "node-b", "synthetic local confirmation"));
            var selectedB = new FileFailoverDeploymentSelection(optionsB.SelectionPath).Read()!;
            await new PostgresBusinessConfirmation(sourceB, new(selectedB.ConfirmationDirectory), "server", "node-b", "node-a", 2, nodeB.ResourcesDirectory, peer).ConfirmAsync(default);
            await using var apiA = await ManagementFailoverTestHost.CreateAsync(Path.Combine(root, "a-management"), platformA, nodeId: "node-a");
            using var clientA = apiA.Client();
            var rebuilt = await Operation(clientA, "rebuild", new("rebuild", "server", "node-a", "operator", 2, 3, "node-a", "synthetic isolated node rebuild", ConfirmDestructiveRebuild: true));
            Assert.Equal(ProtectionMode.Recovering, rebuilt.HighAvailability!.State!.ProtectionMode);
            Assert.True(await Recovery(sourceA)); Assert.NotEmpty(Directory.GetDirectories(root, "a-database.quarantine.*"));
            var protectedAgain = await Operation(clientB, "reprotect", new("reprotect", "server", "node-b", "operator", 2, 4, "node-a", "synthetic restore dual protection"));
            Assert.True(protectedAgain.HighAvailability!.SynchronousProtectionVerified);
            Assert.Equal(ProtectionMode.SynchronousDualNode, protectedAgain.HighAvailability.State!.ProtectionMode);
            Assert.Equal(5, protectedAgain.HighAvailability.State.Version);
            Assert.Equal(DeploymentNodeRole.Standby, protectedAgain.HighAvailability.State.Nodes["node-a"].Role);
            Assert.Equal(new FileConfirmationStore(new FileFailoverDeploymentSelection(optionsA.SelectionPath).Read()!.ConfirmationDirectory).Read(),
                new FileConfirmationStore(new FileFailoverDeploymentSelection(optionsB.SelectionPath).Read()!.ConfirmationDirectory).Read());
            Assert.Equal(1, hostA.RebuildStarts); // Old data is quarantined; only the new standby was restarted.
        }
        finally
        {
            await hostA.StopAsync(default); await hostB.StopAsync(default);
            await sourceA.DisposeAsync(); await sourceB.DisposeAsync();
            PathBoundary.ResolveWithinRoot(Path.GetTempPath(), root, "Synthetic cleanup target escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }

        FailoverNodeDeployment Node(string id, string connection, string suffix)
        {
            var resources = Path.Combine(root, suffix + "-resources"); var confirmation = Path.Combine(root, suffix + "-confirmation");
            Directory.CreateDirectory(resources); Directory.CreateDirectory(confirmation);
            return new(id, connection, resources, certificate.Thumbprint, confirmation, "https://" + id + ".example.test/");
        }
        HighAvailabilityManagementOptions Options(string local, string data) => new("server", local, deployments,
            Path.Combine(root, local + "-selection.json"), Path.Combine(root, local + "-rebuild"), bin, data, "synthetic-postgres", "synthetic-business",
            "synthetic-peer-token-with-at-least-thirty-two-characters");
        IStartupReadinessProbe Materials(NpgsqlDataSource source) => new CompositeStartupReadinessProbe([
            new NodeCertificateStartupReadinessProbe(certificate.Thumbprint, () => new X509Certificate2Collection(
                X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.EphemeralKeySet))),
            new PostgresRecoveryReadinessProbe(source, new NoPackages())]);
        async Task Backup(NpgsqlConnectionStringBuilder connection, string destination, bool recovery, string application)
        {
            var args = new List<string> { "--pgdata", destination, "--wal-method=stream", "--checkpoint=fast", "--no-password" };
            if (recovery) args.Add("--write-recovery-conf");
            var result = await runner.RunAsync(Path.Combine(bin, "pg_basebackup.exe"), args,
                new Dictionary<string, string> { ["PGHOST"] = "127.0.0.1", ["PGPORT"] = connection.Port.ToString(), ["PGUSER"] = "postgres", ["PGAPPNAME"] = application,
                    ["PGPASSWORD"] = connection.Password ?? "" }, TimeSpan.FromMinutes(2));
            Assert.Equal(0, result.ExitCode);
        }
    }

    private static async Task<ManagementOperation> Operation(HttpClient client, string route, HighAvailabilityRequest request)
    {
        using var response = await client.PostAsJsonAsync("/management/v1/high-availability/" + route, request, JsonDefaults.Create());
        var result = await response.Content.ReadFromJsonAsync<ManagementEnvelope<ManagementOperation>>(JsonDefaults.Create());
        if (result?.Result?.Phase == ManagementOperationPhase.NeedsVerification)
        {
            // Real replay can lag briefly. Query the same durable operation; never repeat a POST or lower its proof requirements.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (result?.Result?.Phase == ManagementOperationPhase.NeedsVerification)
            {
                await Task.Delay(200, deadline.Token);
                result = await client.GetFromJsonAsync<ManagementEnvelope<ManagementOperation>>("/management/v1/operations/" + request.RequestId, JsonDefaults.Create(), deadline.Token);
            }
        }
        else Assert.True(response.IsSuccessStatusCode, $"Synthetic {route}: {result?.Error?.Code}");
        Assert.Equal(ManagementOperationPhase.Completed, result!.Result!.Phase); return result.Result;
    }
    private static int Port() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }
    private static async Task Wait(Func<Task<bool>> ready)
    { using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); while (!await ready()) await Task.Delay(50, deadline.Token); }
    private static async Task<bool> Recovery(NpgsqlDataSource source)
    { await using var command = source.CreateCommand("SELECT pg_is_in_recovery()"); return (bool)(await command.ExecuteScalarAsync())!; }
    private static async Task<long> Scalar(NpgsqlDataSource source, string sql)
    { await using var command = source.CreateCommand(sql); return (long)(await command.ExecuteScalarAsync())!; }
    private static async Task Sql(NpgsqlDataSource source, string sql)
    { await using var command = source.CreateCommand(sql); await command.ExecuteNonQueryAsync(); }
    private sealed class NoPackages : IPackageKeyUnwrapper
    { public byte[] Unwrap(EncryptedPackageEnvelope envelope) => throw new IOException("Synthetic empty fixture must not release a package key."); }
    private sealed class Listener : IManagedBusinessHost
    {
        public BusinessServiceStatus Status { get; private set; } = BusinessServiceStatus.Stopped;
        public Task StartAsync(CancellationToken token) { Status = BusinessServiceStatus.Running; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken token) { Status = BusinessServiceStatus.Stopped; return Task.CompletedTask; }
    }
    private sealed class NativeHost(string bin, string data, int port, string root) : IManagedBusinessHost
    {
        public int RebuildStarts;
        public BusinessServiceStatus Status { get; private set; } = BusinessServiceStatus.Stopped;
        public void VerifyBinding()
        {
            if (!Path.GetFullPath(data).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || port is <= 1024 or 5432)
                throw new IOException("Synthetic native host boundary mismatch.");
        }
        public async Task StartAsync(CancellationToken token)
        {
            VerifyBinding(); if (Status == BusinessServiceStatus.Running) return;
            var replay = File.Exists(Path.Combine(data, "standby.signal")) ? "" : ControlledRecoveryDatabase.BuildStandaloneReplayOptions();
            await Control(["start", "-D", data, "-l", Path.Combine(root, "synthetic-" + port + ".log"),
                "-w", "-t", "30", "-o", $"-p {port} -h 127.0.0.1 -c archive_mode=off -c shared_preload_libraries=\"\" " + replay], token);
            Status = BusinessServiceStatus.Running;
            if (Directory.GetDirectories(root, "a-database.quarantine.*").Length > 0) RebuildStarts++;
        }
        public async Task StopAsync(CancellationToken token)
        {
            VerifyBinding(); if (Status != BusinessServiceStatus.Running && !File.Exists(Path.Combine(data, "postmaster.pid"))) return;
            await Control(["stop", "-D", data, "-m", "fast", "-w", "-t", "30"], token); Status = BusinessServiceStatus.Stopped;
        }
        private async Task Control(IReadOnlyList<string> arguments, CancellationToken token)
        {
            VerifyBinding();
            // pg_ctl's persistent PostgreSQL child inherits redirected pipes on Windows.
            // SCM production control has no pipe; the native fixture uses the established direct-control pattern.
            var info = new ProcessStartInfo(Path.Combine(bin, "pg_ctl.exe")) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info) ?? throw new IOException("Synthetic PostgreSQL helper did not start.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(40));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { if (!process.HasExited) process.Kill(); throw; }
            Assert.Equal(0, process.ExitCode);
        }
    }
    private sealed class PeerTransport : HttpMessageHandler
    {
        public Func<string, CancellationToken, Task<HighAvailabilityMaterialProof>> Ready = null!;
        public Func<string, PostgresStandbyConfirmation> Witness = null!;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var node = request.Headers.GetValues("X-Exam-Node-Id").Single();
            Assert.Equal("server", request.Headers.GetValues("X-Exam-Server-Id").Single()); Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            object? result;
            try
            {
                if (request.RequestUri!.AbsolutePath == "/replication/v1/readiness") result = await Ready(node, token);
                else if (request.Method == HttpMethod.Get) result = Witness(node).Read();
                else result = await Witness(node).ConfirmAsync((await request.Content!.ReadFromJsonAsync<DurableConfirmation>(JsonDefaults.Create(), token))!, token);
            }
            catch (Exception) { return new(HttpStatusCode.ServiceUnavailable); }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result, options: JsonDefaults.Create()) };
        }
    }
    private sealed class DatabaseLease : IAsyncDisposable
    { public PostgresDatabaseFixture Value { get; } = new(); public async ValueTask DisposeAsync() => await Value.DisposeAsync(); }
}
