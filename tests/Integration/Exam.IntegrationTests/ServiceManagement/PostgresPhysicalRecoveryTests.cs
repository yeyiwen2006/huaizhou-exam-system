using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Exam.Application.Activation;
using Exam.Application.Scoring;
using Exam.Contracts.Importing;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Contracts.Submissions;
using Exam.Domain.Submissions;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Postgres.Scoring;
using Exam.Infrastructure.Postgres.Submissions;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.Infrastructure.Security;
using Exam.Infrastructure.ServiceManagement;
using Exam.IntegrationTests.Postgres;
using Exam.Server.Api;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Exam.IntegrationTests.ServiceManagement;

public sealed class PhysicalPostgresFactAttribute : FactAttribute
{
    public PhysicalPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EXAM_TEST_PHYSICAL_SOURCE_CONTAINER")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EXAM_TEST_DOCKER_EXECUTABLE")))
            Skip = "真实物理恢复演练需要显式配置专用合成 PostgreSQL 容器和 Docker 可执行路径；普通数据库测试不替代此检查。";
    }
}

[Trait("Category", "Postgres")]
[Trait("Category", "PostgresPhysical")]
public sealed class PostgresPhysicalRecoveryTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _source = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "f01-physical-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _key = RSA.Create(2048);
    private X509Certificate2 _certificate = null!;
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _certificate = new CertificateRequest("CN=Synthetic-F01-Physical-Restore", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        await _source.InitializeAsync();
    }
    public async Task DisposeAsync()
    {
        await _source.DisposeAsync();
        _certificate.Dispose();
        _key.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [PhysicalPostgresFact]
    public async Task RealPhysicalBackupRestoresOriginalReceiptAndRemainingCollection_WithoutChangingSource()
    {
        var original = await SeedAsync();
        var gradeBeforeBackup = (await new PostgresGradingResultStore(_source.DataSource).FindAsync(original.SubmissionId))!;
        var configuration = new ServiceConfiguration("server", "node", "127.0.0.1", 8443, _root, false, 1);
        Assert.All(await InspectAsync(_source.DataSource, configuration), c => Assert.True(c.Passed, c.Name));
        var resources = Path.Combine(_root, "resources");
        var protectedConfiguration = Path.Combine(_root, "protected");
        Directory.CreateDirectory(resources);
        Directory.CreateDirectory(protectedConfiguration);
        await File.WriteAllTextAsync(Path.Combine(resources, "synthetic-resource-reference.txt"), "synthetic-resource");
        // Actual ephemeral test private key is kept in this temp directory only, never added to the repository.
        var keyBytes = _key.ExportPkcs8PrivateKey();
        try { await File.WriteAllBytesAsync(Path.Combine(protectedConfiguration, "synthetic-node.pk8"), keyBytes); }
        finally { CryptographicOperations.ZeroMemory(keyBytes); }
        await File.WriteAllBytesAsync(Path.Combine(protectedConfiguration, "synthetic-node.cer"), _certificate.RawData);
        await using var tools = new DockerPostgresTools();
        var options = new PostgresBackupOptions(_root, _source.ConnectionString, Path.Combine(_root, "backups"),
            Path.Combine(_root, "staging"), resources, protectedConfiguration, TimeSpan.FromMinutes(2));
        var coordinator = new PostgresBackupCoordinator(options, tools, dataSource: _source.DataSource);
        var backupRequest = new BackupRequest("physical-backup", Path.Combine(options.AllowedBackupRoot, "snapshot"), "operator");
        var backup = await coordinator.CreateAsync(backupRequest, default);
        Assert.Equal(backup, await new PostgresBackupCoordinator(options, tools, dataSource: _source.DataSource).CreateAsync(backupRequest, default));
        Assert.Equal(1, tools.BaseBackupCalls);

        var restore = new RestoreRequest("physical-restore", backup.BackupId, backup.DestinationDirectory, "operator");
        await coordinator.RestoreAsync(restore, default);
        await new PostgresBackupCoordinator(options, tools, dataSource: _source.DataSource).RestoreAsync(restore, default);
        Assert.Equal(3, tools.VerifyCalls);
        var offlineEvidence = new PostgresBackupCoordinator(options with { ConnectionString = "Host=127.0.0.1;Port=1;Database=unavailable" }, tools);
        // The production startup integrity path must work without a live business database.
        Assert.Equal(backup, await offlineEvidence.VerifyAvailableBackupAsync(backup.DestinationDirectory, backup.BackupId, default));
        var backupCheck = new BackupIntegrityStartupReadinessProbe(() => offlineEvidence, backup.DestinationDirectory, backup.BackupId);
        Assert.True(Assert.Single(await backupCheck.RunAsync(configuration, default)).Passed);
        Assert.Equal(backup, await offlineEvidence.FindCompletedBackupAsync(backupRequest, default));
        Assert.True(await offlineEvidence.FindStagedRestoreAsync(restore, default));
        var staged = Path.Combine(options.RestoreStagingRoot, backup.BackupId);
        var marker = await File.ReadAllBytesAsync(Path.Combine(staged, "restore-verified.json"));
        var workingRoot = Path.Combine(_root, "working");
        Directory.CreateDirectory(workingRoot);
        var workingCopy = await offlineEvidence.PrepareRecoveryWorkingCopyAsync(restore, workingRoot, default);
        Assert.Equal(workingCopy, await offlineEvidence.PrepareRecoveryWorkingCopyAsync(restore, workingRoot, default));
        using var restoredRsa = RSA.Create();
        var restoredBytes = await File.ReadAllBytesAsync(Path.Combine(staged, "protected-configuration", "synthetic-node.pk8"));
        try { restoredRsa.ImportPkcs8PrivateKey(restoredBytes, out _); }
        finally { CryptographicOperations.ZeroMemory(restoredBytes); }
        using var restoredPublic = X509CertificateLoader.LoadCertificate(await File.ReadAllBytesAsync(Path.Combine(staged, "protected-configuration", "synthetic-node.cer")));
        using var restoredKey = restoredPublic.CopyWithPrivateKey(restoredRsa);

        // Real isolated PostgreSQL startup through the production orchestration; SCM is a test adapter on macOS.
        var protectedRecoveryConfiguration = Path.Combine(_root, "recovery.conf");
        await File.WriteAllTextAsync(protectedRecoveryConfiguration, "# synthetic protected recovery configuration\n");
        var recoveryConnection = new NpgsqlConnectionStringBuilder(_source.ConnectionString) { Host = "127.0.0.1", Port = tools.RestoredPort };
        var controlledService = new DockerRecoveryService(tools, Path.Combine(workingCopy.WorkingDirectory, "database"),
            ControlledRecoveryDatabase.BuildServiceCommand("SyntheticRecoveryPG", options.PostgresBinDirectory, workingRoot,
                protectedRecoveryConfiguration, tools.RestoredPort));
        var controlledDatabase = new ControlledRecoveryDatabase("SyntheticRecoveryPG", "SyntheticOriginalPG", "SyntheticBusiness", "SyntheticManagement",
            options.PostgresBinDirectory, workingRoot, protectedRecoveryConfiguration, _source.ConnectionString,
            recoveryConnection.ConnectionString, controlledService, controlledService);
        await controlledDatabase.PrepareAndStartAsync(restore,
            ct => offlineEvidence.PrepareRecoveryWorkingCopyAsync(restore, workingRoot, ct),
            ct => offlineEvidence.FindPreparedRecoveryWorkingCopyAsync(restore, workingRoot, ct), default);
        await controlledDatabase.PrepareAndStartAsync(restore, _ => throw new IOException("不能在运行实例下重新复制"),
            ct => offlineEvidence.FindPreparedRecoveryWorkingCopyAsync(restore, workingRoot, ct), default);
        Assert.Equal(1, controlledService.Starts);
        var connection = new NpgsqlConnectionStringBuilder(_source.ConnectionString) { Host = "127.0.0.1", Port = tools.RestoredPort };
        await using var recovered = NpgsqlDataSource.Create(connection.ConnectionString);
        await WaitForDatabaseAsync(recovered);
        var recoveredChecks = await new PostgresRecoveryReadinessProbe(recovered, new TestUnwrapper(restoredKey)).RunAsync(configuration, default);
        Assert.All(recoveredChecks, c => Assert.True(c.Passed, c.Name));
        var targetChecks = await new PostgresWorkingCopyReadinessProbe(_source.ConnectionString, connection.ConnectionString, "", 1,
            new PostgresRecoveryReadinessProbe(recovered, new TestUnwrapper(restoredKey))).RunAsync(configuration, workingCopy, default);
        Assert.All(targetChecks, c => Assert.True(c.Passed, c.Name));
        var wrongBusinessSource = await new PostgresWorkingCopyReadinessProbe(_source.ConnectionString, connection.ConnectionString, "", 1,
            new PostgresRecoveryReadinessProbe(recovered, new TestUnwrapper(restoredKey)), _source.DataSource)
            .RunAsync(configuration, workingCopy, default);
        Assert.False(Assert.Single(wrongBusinessSource).Passed);
        var comparison = new PostgresRecoveryStateComparison(_source.ConnectionString, connection.ConnectionString, 1);
        var completeState = await comparison.RunAsync(configuration, default);
        Assert.True(completeState.Passed, completeState.Message);
        Assert.False((await new PostgresRecoveryStateComparison(_source.ConnectionString, connection.ConnectionString, 2)
            .RunAsync(configuration, default)).Passed);
        var unavailable = new NpgsqlConnectionStringBuilder(_source.ConnectionString) { Port = 1, Timeout = 1 };
        Assert.False((await new PostgresRecoveryStateComparison(unavailable.ConnectionString, connection.ConnectionString, 1)
            .RunAsync(configuration, default)).Passed);
        var wrongTarget = workingCopy with { WorkingDirectory = Path.Combine(workingRoot, "other-slot") };
        Assert.False(Assert.Single(await new PostgresWorkingCopyReadinessProbe(_source.ConnectionString, connection.ConnectionString, "", 1)
            .RunAsync(configuration, wrongTarget, default)).Passed);
        var recoveredStore = new PostgresSubmissionStore(recovered);
        Assert.Equal(original, (await recoveredStore.FindSubmissionAsync("synthetic-exam", "candidate-1"))!.Receipt);
        var grading = new GradingCoordinator(new PostgresGradingTaskSource(recovered), new PostgresGradingResultStore(recovered),
            new PostgresGradingInputs(recovered));
        var firstGrading = await grading.RunOnceAsync();
        Assert.Equal(1, firstGrading.Graded);
        Assert.Equal(0, firstGrading.Failed);
        var originalGrade = (await new PostgresGradingResultStore(recovered).FindAsync(original.SubmissionId))!;
        Assert.Equal(1, originalGrade.Grade.GradingVersion);
        Assert.Equal("3", originalGrade.Grade.ExactTotalScore);
        Assert.Equal(JsonSerializer.Serialize(gradeBeforeBackup, Json), JsonSerializer.Serialize(originalGrade, Json));
        Assert.Equal(0, (await grading.RunOnceAsync()).Claimed);

        var selectionPath = Path.Combine(_root, "deployment-selection.json");
        await new FileRecoveryDeploymentSwitch(new FileRecoveryDeploymentSelection(selectionPath), 1).SelectAsync(
            new("synthetic-enable", "server", "node", "operator", ManagementOperationKind.Restore, new string('A', 64),
                ManagementOperationPhase.Staged, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                Restore: new(backup.BackupId, true, true, false), RecoveryWorkingDirectory: workingCopy.WorkingDirectory), default);
        await using (var app = await RecoveredApiAsync(connection.ConnectionString, restoredKey, _source.ConnectionString, selectionPath))
        {
            using var client = app.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Exam-Device-Id", "device-1");
            client.DefaultRequestHeaders.Add("X-Exam-Device-Credential", "synthetic-device-credential");
            var receipt = await client.GetFromJsonAsync<ApiEnvelope<SubmissionReceipt>>(
                "/api/v1/submissions/receipts?participationId=participation-1&entryVersion=1&deviceId=device-1&authorizationVersion=1", Json);
            Assert.Equal(original, receipt!.Result);
            client.DefaultRequestHeaders.Remove("X-Exam-Device-Id");
            client.DefaultRequestHeaders.Add("X-Exam-Device-Id", "device-2");
            var remaining = Submission(2);
            var response = await client.PostAsJsonAsync("/api/v1/submissions/final", remaining, Json);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var saved = await response.Content.ReadFromJsonAsync<ApiEnvelope<SubmissionReceipt>>(Json);
            Assert.NotNull(saved!.Result);
            var replay = await client.PostAsJsonAsync("/api/v1/submissions/final", remaining, Json);
            Assert.Equal(saved.Result, (await replay.Content.ReadFromJsonAsync<ApiEnvelope<SubmissionReceipt>>(Json))!.Result);
        }
        // Recreate the store after host shutdown; persisted pending collection is sufficient to finish.
        var collection = new PostgresCollectionCompletionStore(recovered);
        await collection.ProcessPendingAsync();
        Assert.Equal(2, Assert.Single(await collection.ReadEventsAsync("synthetic-exam", 0)).ParticipantCount);
        Assert.All(await new PostgresRecoveryReadinessProbe(recovered, new TestUnwrapper(restoredKey)).RunAsync(configuration, default),
            c => Assert.True(c.Passed, c.Name));
        var sourceStore = new PostgresSubmissionStore(_source.DataSource);
        Assert.Equal(original, (await sourceStore.FindSubmissionAsync("synthetic-exam", "candidate-1"))!.Receipt);
        Assert.Null(await sourceStore.FindSubmissionAsync("synthetic-exam", "candidate-2"));
        // Recreate the coordinator after the recovered API exits; remaining tasks and exact results are durable.
        var afterRestart = new GradingCoordinator(new PostgresGradingTaskSource(recovered), new PostgresGradingResultStore(recovered),
            new PostgresGradingInputs(recovered));
        var remainingGrading = await afterRestart.RunOnceAsync();
        Assert.Equal(1, remainingGrading.Graded);
        Assert.Equal(0, remainingGrading.Failed);
        var grades = await new PostgresGradingResultStore(recovered).ListAsync("synthetic-exam", 1);
        Assert.Equal(2, grades.Count);
        Assert.All(grades, grade => Assert.Equal("3", grade.Grade.ExactTotalScore));
        Assert.Equal(JsonSerializer.Serialize(originalGrade, Json),
            JsonSerializer.Serialize(await new PostgresGradingResultStore(recovered).FindAsync(original.SubmissionId), Json));
        Assert.Equal(0, (await afterRestart.RunOnceAsync()).Claimed);
        Assert.Equal(JsonSerializer.Serialize(gradeBeforeBackup, Json),
            JsonSerializer.Serialize(Assert.Single(await new PostgresGradingResultStore(_source.DataSource).ListAsync("synthetic-exam", 1)), Json));
        Assert.False((await comparison.RunAsync(configuration, default)).Passed);
        Assert.Equal(marker, await File.ReadAllBytesAsync(Path.Combine(staged, "restore-verified.json")));
        Assert.Equal(backup, await coordinator.CreateAsync(backupRequest, default));
    }

    private async Task<SubmissionReceipt> SeedAsync()
    {
        var now = DateTimeOffset.UtcNow;
        await using (var authority = _source.DataSource.CreateCommand(
            "BEGIN; INSERT INTO exam.server_state VALUES ('server', 'node', 1, 'EmergencySingleNode', 1, now()); " +
            "INSERT INTO exam.server_nodes VALUES ('server', 'node', 'Primary', true, false, 0, true, true, now()); COMMIT;"))
            await authority.ExecuteNonQueryAsync();
        var versioning = new PostgresExamVersioningService(_source.DataSource);
        Assert.True(versioning.CreateExam(new("synthetic-exam", new(now.AddHours(1), now.AddHours(2)), now, "create")).Succeeded);
        using var plain = new MemoryStream(SyntheticRecoveryPaper.Build());
        using var cipher = new MemoryStream();
        var envelope = await new PackageEncryptionService(4096).EncryptAsync(plain, cipher, "synthetic-resource", 1, [new("node", _certificate)]);
        await using (var resource = _source.DataSource.CreateCommand("""
            INSERT INTO exam.versioned_resources VALUES ('synthetic-resource', 'synthetic-exam', 'paper-package', 1, $1, $2::jsonb, now(), 'operator')
            """))
        {
            resource.Parameters.AddWithValue(cipher.ToArray());
            resource.Parameters.AddWithValue(JsonSerializer.Serialize(envelope, Json));
            await resource.ExecuteNonQueryAsync();
        }
        Assert.True(versioning.PublishPaper(new("synthetic-exam", 0, "synthetic-resource", SyntheticRecoveryPaper.Structure(), [],
            new(true, true, true, true), "operator", "合成物理恢复", now, "publish")).Succeeded);
        var gradingManifest = new ExamManifest(1, "synthetic-exam", "合成恢复计分",
            [new("subject", "合成科目", "3")],
            [new("group", "subject", "合成题组", "single", 1, 1, false, "3", new(new("3", "0", "-0.75"), null))],
            [new("q1", 1, "subject", "group", "questions/合成恢复计分_1.tex", ["A", "B"], null)], []);
        var answerKey = new AnswerKeyFile(1, "synthetic-exam", [new("q1", 1, ["A"])]);
        var publishedRules = versioning.PublishGradingRules(new("synthetic-exam", 0, "synthetic-rules", new(true, true, true),
            "operator", "合成恢复计分", now, "publish-rules", JsonSerializer.Serialize(gradingManifest, ImportJson.Create()),
            JsonSerializer.Serialize(answerKey, ImportJson.Create())));
        Assert.True(publishedRules.Succeeded, publishedRules.Error);
        await using (var entry = _source.DataSource.CreateCommand("UPDATE exam.exam_entry_access SET instructions_ready = true WHERE exam_id = 'synthetic-exam'"))
            await entry.ExecuteNonQueryAsync();
        var started = versioning.UpdateSchedule(new("synthetic-exam", 1, new(now.AddHours(-1), now.AddHours(2)), now, "start-schedule"));
        Assert.True(started.Succeeded, started.Error);
        await using (var seed = _source.DataSource.CreateCommand("""
            INSERT INTO exam.activation_daily_sequences VALUES ('2026-09-27', 2);
            INSERT INTO exam.activation_devices VALUES
              ('device-1', '20260927001', '2026-09-27', 'active', NULL, 'synthetic-verifier', 1, now(), NULL, 1),
              ('device-2', '20260927002', '2026-09-27', 'active', NULL, 'synthetic-verifier', 1, now(), NULL, 1);
            INSERT INTO exam.candidate_roster_entries (exam_id, candidate_id, candidate_name, class_text, admission_no) VALUES
              ('synthetic-exam', 'candidate-1', '合成考生一', '01', '000001'),
              ('synthetic-exam', 'candidate-2', '合成考生二', '01', '000002');
            """)) await seed.ExecuteNonQueryAsync();
        var store = new PostgresSubmissionStore(_source.DataSource);
        for (var number = 1; number <= 2; number++)
            await store.SetAuthorizationStateAsync(new("synthetic-exam", $"candidate-{number}", $"participation-{number}", 1,
                $"device-{number}", 1, ParticipationBusinessState.Answering, 1, 1));
        var submitted = await store.SaveSubmissionAsync(Submission(1), now, "original-receipt");
        Assert.Equal(SubmissionPersistenceStatus.Saved, submitted.Status);
        // Simulate a crash after the exact result was persisted but before the outbox task was completed.
        var grade = ImportedGradingRules.Create(JsonSerializer.Serialize(gradingManifest, ImportJson.Create()),
            JsonSerializer.Serialize(answerKey, ImportJson.Create())).Grade(submitted.Submission!, 1);
        Assert.True(grade.Succeeded, grade.FailureReason);
        await new PostgresGradingResultStore(_source.DataSource).SaveAsync(new(grade.Grade!, now));
        return submitted.Submission!.Receipt;
    }

    private static SubmitFinalAnswersRequest Submission(int number) => new($"submit-{number}", $"submission-{number}", "synthetic-exam",
        $"candidate-{number}", $"participation-{number}", 1, $"device-{number}", 1, 1,
        new Dictionary<string, AnswerValue> { ["q1"] = AnswerValue.Selected("A") });
    private Task<IReadOnlyList<StartupCheckResult>> InspectAsync(NpgsqlDataSource source, ServiceConfiguration configuration) =>
        new PostgresRecoveryReadinessProbe(source, new TestUnwrapper(_certificate)).RunAsync(configuration, default);
    private static async Task WaitForDatabaseAsync(NpgsqlDataSource source)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            try { await using var connection = await source.OpenConnectionAsync(timeout.Token); return; }
            catch (NpgsqlException) { await Task.Delay(100, timeout.Token); }
        }
    }
    private static async Task<WebApplication> RecoveredApiAsync(string connectionString, X509Certificate2 certificate,
        string originalConnection, string selectionPath)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Exam"] = originalConnection, ["ConnectionStrings:ExamRecovery"] = connectionString,
            ["Recovery:SelectionPath"] = selectionPath, ["ExamSecurity:OperatorToken"] = "synthetic-physical-recovery-operator-token",
            ["ExamSecurity:OperatorId"] = "operator", ["ExamServer:ServerId"] = "server", ["ExamServer:NodeId"] = "node", ["ExamServer:ServerEpoch"] = "1"
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = Json.PropertyNamingPolicy;
            options.SerializerOptions.PropertyNameCaseInsensitive = Json.PropertyNameCaseInsensitive;
            foreach (var converter in Json.Converters) options.SerializerOptions.Converters.Add(converter);
        });
        builder.Services.AddSignalR();
        builder.Services.AddPostgresExamRuntime(builder.Configuration, new() { ServerId = "server", NodeId = "node", ServerEpoch = 1 }, true);
        builder.Services.AddSingleton<IPackageKeyUnwrapper>(new TestUnwrapper(certificate));
        builder.Services.AddSingleton<IDeviceCredentialValidator, SyntheticDeviceCredentials>();
        var app = builder.Build();
        app.UseMiddleware<ExamApiAccessMiddleware>();
        app.MapGroup("/api/v1").MapSubmissionApi();
        try
        {
            await app.StartAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!app.Services.GetRequiredService<BusinessRecoveryGuard>().Snapshot.Ready) await Task.Delay(20, timeout.Token);
            return app;
        }
        catch { await app.DisposeAsync(); throw; }
    }
    private sealed class TestUnwrapper(X509Certificate2 certificate) : IPackageKeyUnwrapper
    { public byte[] Unwrap(EncryptedPackageEnvelope envelope) => PackageEncryptionService.UnwrapDataKey(envelope, "node", certificate); }
    private sealed class SyntheticDeviceCredentials : IDeviceCredentialValidator
    {
        public ValueTask<bool> ValidateActiveDeviceAsync(string deviceId, string credential, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(deviceId is "device-1" or "device-2" && credential == "synthetic-device-credential");
        public ValueTask<bool> IsActiveDeviceAsync(string deviceId, CancellationToken cancellationToken = default) => ValueTask.FromResult(deviceId is "device-1" or "device-2");
    }

    private sealed class DockerRecoveryService(DockerPostgresTools tools, string directory, string command)
        : IWindowsServiceControl, IWindowsServiceDeployment
    {
        public int Starts { get; private set; }
        private ManagedWindowsServiceState _state = ManagedWindowsServiceState.Stopped;
        public string GetBinaryPath(string name) => command;
        public ManagedWindowsServiceState GetState(string name) => _state;
        public void Start(string name)
        {
            Starts++;
            Task.Run(() => tools.StartRestoredAsync(directory)).GetAwaiter().GetResult();
            _state = ManagedWindowsServiceState.Running;
        }
        public void Stop(string name) => throw new InvalidOperationException("本演练只在最终清理隔离容器。");
    }

    private sealed class DockerPostgresTools : IExternalProcessRunner, IAsyncDisposable
    {
        private readonly string _docker = Environment.GetEnvironmentVariable("EXAM_TEST_DOCKER_EXECUTABLE")!;
        private readonly string _sourceContainer = Environment.GetEnvironmentVariable("EXAM_TEST_PHYSICAL_SOURCE_CONTAINER")!;
        private readonly string _remote = "/tmp/codex-f01-physical-" + Guid.NewGuid().ToString("N");
        private readonly string _restoredContainer = "codex-f01-restored-" + Guid.NewGuid().ToString("N");
        private bool _restoredCreated;
        public int BaseBackupCalls { get; private set; }
        public int VerifyCalls { get; private set; }
        public int RestoredPort { get; } = AllocatePort();
        private static int AllocatePort()
        {
            var allocation = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            allocation.Start();
            var port = ((IPEndPoint)allocation.LocalEndpoint).Port;
            allocation.Stop();
            return port;
        }
        private async Task<ExternalProcessResult> DockerAsync(IReadOnlyList<string> arguments, CancellationToken token = default)
        {
            var result = await new ExternalProcessRunner().RunAsync(_docker, arguments, new Dictionary<string, string>(), TimeSpan.FromMinutes(2), token);
            if (result.ExitCode != 0) throw new IOException($"隔离物理恢复工具失败（退出码 {result.ExitCode}）；不输出原始工具日志。");
            return result;
        }
        public async Task<ExternalProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken = default,
            string? workingDirectory = null, ExternalProcessLimits? limits = null)
        {
            if (!_sourceContainer.StartsWith("codex-f01-physical-source-", StringComparison.Ordinal))
                throw new InvalidOperationException("只允许显式命名的专用 F01 合成源容器。");
            await DockerAsync(["exec", _sourceContainer, "mkdir", "-p", _remote], cancellationToken);
            if (Path.GetFileName(executable) == "pg_basebackup.exe")
            {
                BaseBackupCalls++;
                var destination = arguments[arguments.ToList().IndexOf("--pgdata") + 1];
                await DockerAsync(["exec", _sourceContainer, "pg_basebackup", "--host=127.0.0.1", "--port=5432", "--username=postgres",
                    "--pgdata=" + _remote + "/backup", "--format=plain", "--wal-method=stream", "--checkpoint=fast", "--no-password"], cancellationToken);
                await DockerAsync(["cp", _sourceContainer + ":" + _remote + "/backup/.", destination], cancellationToken);
            }
            else if (Path.GetFileName(executable) == "pg_verifybackup.exe")
            {
                VerifyCalls++;
                var path = _remote + "/verify-" + VerifyCalls;
                await DockerAsync(["cp", arguments[0], _sourceContainer + ":" + path], cancellationToken);
                await DockerAsync(["exec", _sourceContainer, "pg_verifybackup", path], cancellationToken);
            }
            else throw new InvalidOperationException("恢复演练只允许 PostgreSQL 物理备份及校验工具。");
            return new(0, "真实 PostgreSQL 工具完成。", "");
        }
        public async Task StartRestoredAsync(string verifiedDatabaseDirectory)
        {
            var image = Environment.GetEnvironmentVariable("EXAM_TEST_POSTGRES_IMAGE") ?? "postgres:18";
            var selectedPort = RestoredPort;
            await DockerAsync(["create", "--name", _restoredContainer, "--label", "codex.f01.synthetic=true", "--publish", $"127.0.0.1:{selectedPort}:{selectedPort}",
                "--volume", "/var/lib/postgresql", "--entrypoint", "sleep", image, "infinity"]);
            _restoredCreated = true;
            await DockerAsync(["start", _restoredContainer]);
            await DockerAsync(["exec", _restoredContainer, "mkdir", "-p", verifiedDatabaseDirectory]);
            await DockerAsync(["cp", verifiedDatabaseDirectory + Path.DirectorySeparatorChar + ".", _restoredContainer + ":" + verifiedDatabaseDirectory]);
            await DockerAsync(["exec", _restoredContainer, "chown", "-R", "postgres:postgres", verifiedDatabaseDirectory]);
            await DockerAsync(["exec", _restoredContainer, "chmod", "700", verifiedDatabaseDirectory]);
            await DockerAsync(["exec", "--user", "postgres", _restoredContainer, "pg_ctl", "--pgdata=" + verifiedDatabaseDirectory,
                "--log=/tmp/codex-f01-recovery.log", $"--options=-p {selectedPort} -h 0.0.0.0", "--wait", "start"]);
            var port = (await DockerAsync(["port", _restoredContainer, selectedPort + "/tcp"])).StandardOutput.Trim();
            if (!port.StartsWith("127.0.0.1:", StringComparison.Ordinal) || !int.TryParse(port.Split(':')[1], out var number))
                throw new InvalidDataException("隔离副本必须只监听本机回环随机端口。");
            if (number != RestoredPort) throw new InvalidDataException("实际恢复端口与受控部署不一致。");
        }
        public async ValueTask DisposeAsync()
        {
            if (_restoredCreated) await DockerAsync(["rm", "--force", "--volumes", _restoredContainer]);
            if (_sourceContainer.StartsWith("codex-f01-physical-source-", StringComparison.Ordinal))
                await DockerAsync(["exec", _sourceContainer, "rm", "--recursive", "--force", _remote]);
        }
    }
}
