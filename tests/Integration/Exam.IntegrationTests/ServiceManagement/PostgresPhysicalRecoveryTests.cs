using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Exam.Application.Activation;
using Exam.Application.Scoring;
using Exam.Contracts.Importing;
using Exam.Contracts.Common;
using Exam.Contracts.Commands;
using Exam.Contracts.ServiceManagement;
using Exam.Contracts.Submissions;
using Exam.Domain.Submissions;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Postgres.Commands;
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
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Exam.IntegrationTests.ServiceManagement;

public sealed class PhysicalPostgresFactAttribute : FactAttribute
{
    public PhysicalPostgresFactAttribute()
    {
        if (!NativePhysicalPostgresTools.Enabled &&
            (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EXAM_TEST_PHYSICAL_SOURCE_CONTAINER")) ||
             string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EXAM_TEST_DOCKER_EXECUTABLE"))))
            Skip = "真实物理恢复演练需要显式配置专用合成 PostgreSQL 容器或受限本机临时实例；普通数据库测试不替代此检查。";
    }
}

[Trait("Category", "Postgres")]
[Trait("Category", "PostgresPhysical")]
[Collection(PostgresCollection.Name)] // Source suspension must not interrupt another PostgreSQL test's connections.
public sealed class PostgresPhysicalRecoveryTests(PostgresDatabaseFixture collectionSource) : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _source = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "f01-physical-" + Guid.NewGuid().ToString("N"));
    private readonly RSA _key = RSA.Create(2048);
    private X509Certificate2 _certificate = null!;
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();
    public async Task InitializeAsync()
    {
        if (NativePhysicalPostgresTools.Enabled)
            await NativePhysicalPostgresTools.ValidateConfiguredSourceAsync();
        Directory.CreateDirectory(_root);
        _certificate = new CertificateRequest("CN=Synthetic-F01-Physical-Restore", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        await _source.InitializeAsync();
    }
    public async Task DisposeAsync()
    {
        await _source.DisposeAsync();
        _certificate?.Dispose();
        _key.Dispose();
        // Native PostgreSQL must not traverse links; Docker's existing macOS temp parent may be /var.
        PhysicalPostgresTestPaths.RequireWorkingRoot(_root, rejectReparsePoints: NativePhysicalPostgresTools.Enabled);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private IPhysicalPostgresTools CreateTools() => NativePhysicalPostgresTools.Enabled
        ? new NativePhysicalPostgresTools(_source.ConnectionString, _root, _source.DataSource, collectionSource.DataSource)
        : new DockerPostgresTools();

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
        await using var tools = CreateTools();
        var options = new PostgresBackupOptions(tools.NativeBinDirectory ?? _root, _source.ConnectionString, Path.Combine(_root, "backups"),
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

        // Real isolated PostgreSQL startup through production orchestration; service control is a test adapter.
        var protectedRecoveryConfiguration = Path.Combine(_root, "recovery.conf");
        await File.WriteAllTextAsync(protectedRecoveryConfiguration, "# synthetic protected recovery configuration\n");
        var recoveryConnection = new NpgsqlConnectionStringBuilder(_source.ConnectionString) { Host = "127.0.0.1", Port = tools.RestoredPort };
        var controlledService = new PhysicalRecoveryService(tools, Path.Combine(workingCopy.WorkingDirectory, "database"),
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
        // Docker copies a Windows working directory into a Linux path, which must be rejected.
        // Native execution exercises the successful path and real HTTP recovery on the original Windows path.
        if (OperatingSystem.IsWindows() && !tools.IsNative) Assert.False(Assert.Single(targetChecks).Passed);
        else Assert.All(targetChecks, c => Assert.True(c.Passed, c.Name));
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
                Restore: new(backup.BackupId, true, true, false), RecoveryWorkingDirectory: workingCopy.WorkingDirectory,
                Checks: [.. targetChecks, completeState]), default);
        await using (var app = await RecoveredApiAsync(connection.ConnectionString, restoredKey, _source.ConnectionString,
            selectionPath, expectReady: !OperatingSystem.IsWindows() || tools.IsNative))
        {
            if (OperatingSystem.IsWindows() && !tools.IsNative)
            {
                // A Linux Docker data_directory cannot match the Windows host working path.
                Assert.Contains(app.Services.GetRequiredService<BusinessRecoveryGuard>().Snapshot.Checks,
                    check => check.Name == "recovery-target" && !check.Passed && check.BlocksWrites);
                Assert.Equal(original, (await recoveredStore.FindSubmissionAsync("synthetic-exam", "candidate-1"))!.Receipt);
                Assert.Equal(SubmissionPersistenceStatus.Saved,
                    (await recoveredStore.SaveSubmissionAsync(Submission(2), DateTimeOffset.UtcNow, "remaining-receipt")).Status);
            }
            else
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
            // Join the production workers before inspecting durable work left by this host.
            await app.StopAsync();
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
        // The production worker may already have graded the remaining receipt. Resume only durable
        // unfinished work, including a lease left if shutdown interrupted a claimed task.
        await using var pending = recovered.CreateCommand(
            "SELECT count(*)::integer FROM exam.grading_outbox WHERE completed_at IS NULL;");
        var pendingBeforeRestart = (int)(await pending.ExecuteScalarAsync())!;
        Assert.InRange(pendingBeforeRestart, 0, 1);
        var afterRestart = new GradingCoordinator(new PostgresGradingTaskSource(recovered), new PostgresGradingResultStore(recovered),
            new PostgresGradingInputs(recovered), timeProvider: new FixedTimeProvider(DateTimeOffset.UtcNow.AddHours(1)));
        var remainingGrading = await afterRestart.RunOnceAsync();
        Assert.Equal(pendingBeforeRestart, remainingGrading.Claimed);
        Assert.Equal(pendingBeforeRestart, remainingGrading.Graded);
        Assert.Equal(0, remainingGrading.Failed);
        Assert.Equal(0, (int)(await pending.ExecuteScalarAsync())!);
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

    [PhysicalPostgresFact]
    public async Task IntactBackupAndExistingWalDoNotProvePostBackupAcknowledgedSubmission_WhenSourceBecomesUnreadable()
    {
        var original = await SeedAsync();
        var configuration = new ServiceConfiguration("server", "node", "127.0.0.1", 8443, _root, false, 1);
        var resources = Path.Combine(_root, "resources");
        var protectedConfiguration = Path.Combine(_root, "protected");
        Directory.CreateDirectory(resources);
        Directory.CreateDirectory(protectedConfiguration);
        await File.WriteAllTextAsync(Path.Combine(resources, "synthetic.txt"), "synthetic-resource");
        await File.WriteAllTextAsync(Path.Combine(protectedConfiguration, "synthetic.txt"), "synthetic-configuration");
        await using var tools = CreateTools();
        var options = new PostgresBackupOptions(tools.NativeBinDirectory ?? _root, _source.ConnectionString, Path.Combine(_root, "backups"),
            Path.Combine(_root, "staging"), resources, protectedConfiguration, TimeSpan.FromMinutes(2));
        var coordinator = new PostgresBackupCoordinator(options, tools, dataSource: _source.DataSource);
        var backup = await coordinator.CreateAsync(new("stale-backup", Path.Combine(options.AllowedBackupRoot, "snapshot"), "operator"), default);
        var restore = new RestoreRequest("stale-restore", backup.BackupId, backup.DestinationDirectory, "operator");
        await coordinator.RestoreAsync(restore, default);
        var workingRoot = Path.Combine(_root, "working");
        Directory.CreateDirectory(workingRoot);
        var working = await coordinator.PrepareRecoveryWorkingCopyAsync(restore, workingRoot, default);
        await tools.StartRestoredAsync(Path.Combine(working.WorkingDirectory, "database"));
        var restoredConnection = new NpgsqlConnectionStringBuilder(_source.ConnectionString) { Port = tools.RestoredPort };
        await using var recovered = NpgsqlDataSource.Create(restoredConnection.ConnectionString);
        await WaitForDatabaseAsync(recovered);
        var recoveredStore = new PostgresSubmissionStore(recovered);
        Assert.Equal(original, (await recoveredStore.FindSubmissionAsync("synthetic-exam", "candidate-1"))!.Receipt);
        var comparison = new PostgresRecoveryStateComparison(_source.ConnectionString, restoredConnection.ConnectionString, 1);
        Assert.True((await comparison.RunAsync(configuration, default)).Passed);

        // A real acknowledged business commit after the base backup, not a synthetic file timestamp.
        var sourceStore = new PostgresSubmissionStore(_source.DataSource);
        var committed = await sourceStore.SaveSubmissionAsync(Submission(2), DateTimeOffset.UtcNow, "post-backup-receipt");
        Assert.Equal(SubmissionPersistenceStatus.Saved, committed.Status);
        Assert.Equal(committed.Submission!.Receipt, (await sourceStore.FindSubmissionAsync("synthetic-exam", "candidate-2"))!.Receipt);
        Assert.Null(await recoveredStore.FindSubmissionAsync("synthetic-exam", "candidate-2"));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backup.DestinationDirectory, "database", "backup_manifest")));
        var endLsn = manifest.RootElement.GetProperty("WAL-Ranges").EnumerateArray().Single().GetProperty("End-LSN").GetString()!;
        await using (var position = _source.DataSource.CreateCommand("SELECT pg_current_wal_flush_lsn()::text"))
            Assert.True(Lsn((string)(await position.ExecuteScalarAsync())!) > Lsn(endLsn));
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(backup.DestinationDirectory, "database", "pg_wal")));
        // pg_verifybackup and every backup digest still pass even though that acknowledged receipt is absent.
        Assert.Equal(backup, await coordinator.VerifyAvailableBackupAsync(backup.DestinationDirectory, backup.BackupId, default));
        Assert.False((await comparison.RunAsync(configuration, default)).Passed);
        await tools.PauseSourceAsync();
        try
        {
            // The dedicated original instance is unavailable; no invented unreachable connection substitutes for it.
            Assert.False((await comparison.RunAsync(configuration, default)).Passed);
            Assert.Null(await recoveredStore.FindSubmissionAsync("synthetic-exam", "candidate-2"));
        }
        finally { await tools.ResumeSourceAsync(); }
        Assert.Equal(committed.Submission.Receipt, (await sourceStore.FindSubmissionAsync("synthetic-exam", "candidate-2"))!.Receipt);
    }

    [PhysicalPostgresFact]
    public async Task ControlledArchivedWalReplayRecoversPostBackupReceipt_AndStillRequiresLatestStateComparison()
    {
        var original = await SeedAsync();
        var configuration = new ServiceConfiguration("server", "node", "127.0.0.1", 8443, _root, false, 1);
        var resources = Path.Combine(_root, "resources");
        var protectedConfiguration = Path.Combine(_root, "protected");
        Directory.CreateDirectory(resources); Directory.CreateDirectory(protectedConfiguration);
        await File.WriteAllTextAsync(Path.Combine(resources, "synthetic.txt"), "synthetic-resource");
        await File.WriteAllTextAsync(Path.Combine(protectedConfiguration, "synthetic.txt"), "synthetic-configuration");
        await using var tools = CreateTools();
        var options = new PostgresBackupOptions(tools.NativeBinDirectory ?? _root, _source.ConnectionString, Path.Combine(_root, "backups"),
            Path.Combine(_root, "staging"), resources, protectedConfiguration, TimeSpan.FromMinutes(2));
        var coordinator = new PostgresBackupCoordinator(options, tools, dataSource: _source.DataSource);
        var backup = await coordinator.CreateAsync(new("wal-backup", Path.Combine(options.AllowedBackupRoot, "snapshot"), "operator"), default);
        var restore = new RestoreRequest("wal-restore", backup.BackupId, backup.DestinationDirectory, "operator");
        await coordinator.RestoreAsync(restore, default);
        var sourceStore = new PostgresSubmissionStore(_source.DataSource);
        var committed = await sourceStore.SaveSubmissionAsync(Submission(2), DateTimeOffset.UtcNow, "wal-receipt");
        Assert.Equal(SubmissionPersistenceStatus.Saved, committed.Status);
        await using (var pending = _source.DataSource.CreateCommand("""
            BEGIN;
            UPDATE exam.activation_daily_sequences SET last_sequence=3 WHERE activation_date='2026-09-27';
            INSERT INTO exam.activation_devices VALUES
              ('device-3', '20260927003', '2026-09-27', 'active', NULL, 'synthetic-verifier', 1, now(), NULL, 1);
            INSERT INTO exam.candidate_roster_entries (exam_id, candidate_id, candidate_name, class_text, admission_no)
              VALUES ('synthetic-exam', 'candidate-3', '合成考生三', '01', '000003');
            COMMIT;
            """)) await pending.ExecuteNonQueryAsync();
        await sourceStore.SetAuthorizationStateAsync(new("synthetic-exam", "candidate-3", "participation-3", 1,
            "device-3", 1, ParticipationBusinessState.Answering, 1, 1));
        ulong systemIdentifier;
        string target;
        string sourceDirectory;
        await using (var state = _source.DataSource.CreateCommand("SELECT system_identifier::text, pg_current_wal_flush_lsn()::text, current_setting('data_directory') FROM pg_control_system()"))
        await using (var row = await state.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync());
            systemIdentifier = ulong.Parse(row.GetString(0), System.Globalization.CultureInfo.InvariantCulture);
            target = row.GetString(1); sourceDirectory = row.GetString(2);
        }
        await using (var closeSegment = _source.DataSource.CreateCommand("SELECT pg_switch_wal()")) await closeSegment.ExecuteScalarAsync();
        var archive = Path.Combine(_root, "archive"); Directory.CreateDirectory(archive);
        using var baseManifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backup.DestinationDirectory, "database", "backup_manifest")));
        var baseStart = baseManifest.RootElement.GetProperty("WAL-Ranges").EnumerateArray().Single().GetProperty("Start-LSN").GetString()!;
        var archiveFiles = new List<string>();
        await using (var files = _source.DataSource.CreateCommand("SELECT name FROM pg_ls_waldir() WHERE name >= pg_walfile_name($1::pg_lsn) AND name <= pg_walfile_name($2::pg_lsn) AND length(name)=24 ORDER BY name"))
        {
            files.Parameters.AddWithValue(baseStart); files.Parameters.AddWithValue(target);
            await using var rows = await files.ExecuteReaderAsync();
            while (await rows.ReadAsync()) archiveFiles.Add(rows.GetString(0));
        }
        await tools.ExportSourceWalAsync(sourceDirectory, archive, archiveFiles);
        var replay = new PostgresArchiveRecovery(new(archive, systemIdentifier, 1, target));
        var workingRoot = Path.Combine(_root, "working"); Directory.CreateDirectory(workingRoot);
        var recoveryConfiguration = Path.Combine(_root, "recovery.conf");
        await File.WriteAllTextAsync(recoveryConfiguration, "# synthetic recovery configuration\n");
        var recoveredConnection = new NpgsqlConnectionStringBuilder(_source.ConnectionString) { Port = tools.RestoredPort };
        var service = new PhysicalRecoveryService(tools, Path.Combine(workingRoot, "slot", "database"),
            ControlledRecoveryDatabase.BuildServiceCommand("SyntheticRecoveryPG", options.PostgresBinDirectory, workingRoot, recoveryConfiguration, tools.RestoredPort, replay.Options), replay.Options);
        var controlled = new ControlledRecoveryDatabase("SyntheticRecoveryPG", "SyntheticOriginalPG", "SyntheticBusiness", "SyntheticManagement", options.PostgresBinDirectory,
            workingRoot, recoveryConfiguration, _source.ConnectionString, recoveredConnection.ConnectionString, service, service, replay);
        await controlled.PrepareAndStartAsync(restore,
            ct => coordinator.PrepareRecoveryWorkingCopyAsync(restore, workingRoot, ct),
            ct => coordinator.FindPreparedRecoveryWorkingCopyAsync(restore, workingRoot, ct), default);
        var working = (await coordinator.FindPreparedRecoveryWorkingCopyAsync(restore, workingRoot, default))!;
        var binding = replay.ReadBinding(working, restore);
        Assert.Equal(target, binding.Options.TargetLsn);
        await using var recovered = NpgsqlDataSource.Create(recoveredConnection.ConnectionString);
        await WaitForDatabaseAsync(recovered);
        Assert.True((await replay.VerifyReplayedAsync(working, restore, recoveredConnection.ConnectionString, default)).Passed);
        Assert.False((await replay.VerifyReplayedAsync(working, restore, _source.ConnectionString, default)).Passed);
        Assert.False((await new PostgresArchiveRecovery(replay.Options with { TargetLsn = "0/1" })
            .VerifyReplayedAsync(working, restore, recoveredConnection.ConnectionString, default)).Passed);
        await using (var replayState = recovered.CreateCommand("SELECT pg_is_in_recovery(), current_setting('recovery_target_lsn'), current_setting('restore_command'), pg_last_wal_replay_lsn()::text"))
        await using (var row = await replayState.ExecuteReaderAsync())
        {
            Assert.True(await row.ReadAsync()); Assert.False(row.GetBoolean(0));
            Assert.Equal(target, row.GetString(1)); Assert.Equal("exit 1", row.GetString(2));
            Assert.True(Lsn(row.GetString(3)) >= Lsn(target));
        }
        var recoveredStore = new PostgresSubmissionStore(recovered);
        Assert.Equal(original, (await recoveredStore.FindSubmissionAsync("synthetic-exam", "candidate-1"))!.Receipt);
        Assert.Equal(committed.Submission!.Receipt, (await recoveredStore.FindSubmissionAsync("synthetic-exam", "candidate-2"))!.Receipt);
        var comparison = new PostgresRecoveryStateComparison(_source.ConnectionString, recoveredConnection.ConnectionString, 1);
        Assert.True((await comparison.RunAsync(configuration, default)).Passed);
        var targetChecks = await new PostgresWorkingCopyReadinessProbe(_source.ConnectionString, recoveredConnection.ConnectionString, "", 1,
            new PostgresRecoveryReadinessProbe(recovered, new TestUnwrapper(_certificate))).RunAsync(configuration, working, default);
        if (OperatingSystem.IsWindows() && !tools.IsNative) Assert.False(Assert.Single(targetChecks).Passed);
        else Assert.All(targetChecks, c => Assert.True(c.Passed, c.Name));
        var selectedPath = Path.Combine(_root, "archive-deployment-selection.json");
        await new FileRecoveryDeploymentSelection(selectedPath).SelectAsync(new RecoveryDeploymentSelection(
            "server", "node", restore.RequestId, restore.ActorId, backup.BackupId, working.WorkingDirectory, 1, "ExamRecovery")
            { ArchiveRecovery = true }, default);
        Assert.True((await replay.VerifyReplayedAsync(working, restore, recovered, default)).Passed);
        await using (var selected = await RecoveredApiAsync(recoveredConnection.ConnectionString, _certificate,
            _source.ConnectionString, selectedPath, replay.Options, expectReady: !OperatingSystem.IsWindows() || tools.IsNative))
        {
            var snapshot = selected.Services.GetRequiredService<BusinessRecoveryGuard>().Snapshot;
            if (OperatingSystem.IsWindows() && !tools.IsNative)
                Assert.Contains(snapshot.Checks, check => check.Name == "recovery-target" && !check.Passed && check.BlocksWrites);
            else
            {
                Assert.True(snapshot.Ready);
                using var client = selected.GetTestClient();
                client.DefaultRequestHeaders.Add("X-Exam-Device-Id", "device-1");
                client.DefaultRequestHeaders.Add("X-Exam-Device-Credential", "synthetic-device-credential");
                var receipt = await client.GetFromJsonAsync<ApiEnvelope<SubmissionReceipt>>(
                    "/api/v1/submissions/receipts?participationId=participation-1&entryVersion=1&deviceId=device-1&authorizationVersion=1", Json);
                Assert.Equal(original, receipt!.Result);
            }
        }
        await using (var missingWalConfiguration = await RecoveredApiAsync(recoveredConnection.ConnectionString, _certificate,
            _source.ConnectionString, selectedPath, expectReady: false))
        {
            var snapshot = missingWalConfiguration.Services.GetRequiredService<BusinessRecoveryGuard>().Snapshot;
            Assert.False(snapshot.Ready);
            Assert.Contains(snapshot.Checks, check => check.Name == (OperatingSystem.IsWindows() && !tools.IsNative ? "recovery-target" : "recovery-wal-replay")
                && !check.Passed && check.BlocksWrites);
        }
        await controlled.PrepareAndStartAsync(restore, _ => throw new IOException("运行中不能复制WAL"),
            ct => coordinator.FindPreparedRecoveryWorkingCopyAsync(restore, workingRoot, ct), default);
        Assert.Equal(1, service.Starts);
        await tools.PauseSourceAsync();
        try { Assert.False((await comparison.RunAsync(configuration, default)).Passed); }
        finally { await tools.ResumeSourceAsync(); }
        // A configured LSN and successful replay still do not prove that it is the latest acknowledged state.
        await sourceStore.SetAuthorizationStateAsync(new("synthetic-exam", "candidate-2", "participation-2", 1,
            "device-2", 2, ParticipationBusinessState.Submitted, 1, 1));
        Assert.False((await comparison.RunAsync(configuration, default)).Passed);
        // Restarted scheduled work must use the recovered state and persist one end command transition.
        var active = new ActiveNodeGuard(recovered, new ServerIdentityOptions
            { ServerId = "server", NodeId = "node", ServerEpoch = 1 }, false);
        var allowed = await active.CheckAsync();
        Assert.True(allowed.Allowed, allowed.Reason);
        await using (var before = recovered.CreateCommand("SELECT lifecycle FROM exam.version_state WHERE exam_id='synthetic-exam'"))
            Assert.Equal("Running", await before.ExecuteScalarAsync());
        PostgresScheduleProcessor Scheduler() => new(recovered, new PostgresExamVersioningService(recovered),
            active, new FixedTimeProvider(DateTimeOffset.UtcNow.AddHours(3)), NullLogger<PostgresScheduleProcessor>.Instance);
        Assert.Equal(1, await Scheduler().ExecuteDueAsync());
        Assert.Equal(0, await Scheduler().ExecuteDueAsync());
        await using (var schedule = recovered.CreateCommand("SELECT lifecycle FROM exam.version_state WHERE exam_id='synthetic-exam'"))
            Assert.Equal("Ended", await schedule.ExecuteScalarAsync());
        await using (var eventCount = recovered.CreateCommand("SELECT count(*) FROM exam.command_events WHERE exam_id='synthetic-exam' AND event_type='End'"))
            Assert.True((long)(await eventCount.ExecuteScalarAsync())! > 0);
        var due = await new PostgresCommandEventStore(recovered).ClaimDueForDeviceAsync(
            "participation-3", 1, "device-3", 1, DateTimeOffset.UtcNow.AddHours(4));
        Assert.Contains(due, item => item.EventType == CommandEventType.End);
        Assert.Equal(backup, await coordinator.VerifyAvailableBackupAsync(backup.DestinationDirectory, backup.BackupId, default));
    }

    private static ulong Lsn(string text)
    {
        var parts = text.Split('/');
        return (Convert.ToUInt64(parts[0], 16) << 32) | Convert.ToUInt64(parts[1], 16);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
        string originalConnection, string selectionPath, PostgresArchiveRecoveryOptions? walOptions = null, bool expectReady = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Exam"] = originalConnection, ["ConnectionStrings:ExamRecovery"] = connectionString,
            ["Recovery:SelectionPath"] = selectionPath, ["ExamSecurity:OperatorToken"] = "synthetic-physical-recovery-operator-token",
            ["ExamSecurity:OperatorId"] = "operator", ["ExamServer:ServerId"] = "server", ["ExamServer:NodeId"] = "node", ["ExamServer:ServerEpoch"] = "1"
        };
        if (walOptions is not null)
        {
            settings["Recovery:WalArchiveDirectory"] = walOptions.ArchiveDirectory;
            settings["Recovery:WalSystemIdentifier"] = walOptions.SystemIdentifier.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings["Recovery:WalTimeline"] = walOptions.Timeline.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings["Recovery:WalTargetLsn"] = walOptions.TargetLsn;
        }
        builder.Configuration.AddInMemoryCollection(settings);
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
            var guard = app.Services.GetRequiredService<BusinessRecoveryGuard>();
            try
            {
                while (expectReady ? !guard.Snapshot.Ready : guard.Snapshot.Checks.Count == 0)
                    await Task.Delay(20, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                throw new InvalidOperationException("恢复业务宿主启动核对超时：" + JsonSerializer.Serialize(guard.Snapshot, Json));
            }
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

    private sealed class PhysicalRecoveryService(IPhysicalPostgresTools tools, string directory, string command, PostgresArchiveRecoveryOptions? archiveReplay = null)
        : IWindowsServiceControl, IWindowsServiceDeployment
    {
        public int Starts { get; private set; }
        private ManagedWindowsServiceState _state = ManagedWindowsServiceState.Stopped;
        public string GetBinaryPath(string name) => command;
        public ManagedWindowsServiceState GetState(string name) => _state;
        public void Start(string name)
        {
            Starts++;
            Task.Run(() => tools.StartRestoredAsync(directory, archiveReplay)).GetAwaiter().GetResult();
            _state = ManagedWindowsServiceState.Running;
        }
        public void Stop(string name) => throw new InvalidOperationException("本演练只在最终清理隔离副本。");
    }

    private sealed class DockerPostgresTools : IPhysicalPostgresTools
    {
        private readonly string _docker = Environment.GetEnvironmentVariable("EXAM_TEST_DOCKER_EXECUTABLE")!;
        private readonly string _sourceContainer = Environment.GetEnvironmentVariable("EXAM_TEST_PHYSICAL_SOURCE_CONTAINER")!;
        private readonly string _remote = "/tmp/codex-f01-physical-" + Guid.NewGuid().ToString("N");
        private readonly string _restoredContainer = "codex-f01-restored-" + Guid.NewGuid().ToString("N");
        private bool _restoredCreated;
        private bool _sourcePaused;
        public bool IsNative => false;
        public string? NativeBinDirectory => null;
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
        public async Task ExportSourceWalAsync(string sourceDirectory, string archive, IReadOnlyList<string> files)
        {
            if (!_sourceContainer.StartsWith("codex-f01-physical-source-", StringComparison.Ordinal))
                throw new InvalidOperationException("只允许专用合成源容器导出WAL。");
            await DockerAsync(["exec", _sourceContainer, "mkdir", "-p", _remote + "/archive"]);
            foreach (var file in files)
            {
                if (file.Length != 24 || file.Any(c => !"0123456789ABCDEF".Contains(c))) throw new InvalidDataException("合成归档文件名无效。");
                // Docker's archive endpoint does not reliably resolve PGDATA through the source tmpfs mount.
                await DockerAsync(["exec", _sourceContainer, "cp", sourceDirectory + "/pg_wal/" + file, _remote + "/archive/" + file]);
                await DockerAsync(["cp", _sourceContainer + ":" + _remote + "/archive/" + file, Path.Combine(archive, file)]);
            }
        }
        public async Task StartRestoredAsync(string verifiedDatabaseDirectory, PostgresArchiveRecoveryOptions? archiveReplay = null)
        {
            var image = Environment.GetEnvironmentVariable("EXAM_TEST_POSTGRES_IMAGE") ?? "postgres:18";
            var selectedPort = RestoredPort;
            await DockerAsync(["create", "--name", _restoredContainer, "--label", "codex.f01.synthetic=true", "--publish", $"127.0.0.1:{selectedPort}:{selectedPort}",
                "--volume", "/var/lib/postgresql", "--entrypoint", "sleep", image, "infinity"]);
            _restoredCreated = true;
            await DockerAsync(["start", _restoredContainer]);
            // The verified source is a Windows host path; PostgreSQL inside Docker needs a Linux path.
            var remoteDatabase = _remote + "/restored-database";
            await DockerAsync(["exec", _restoredContainer, "mkdir", "-p", remoteDatabase]);
            await DockerAsync(["cp", verifiedDatabaseDirectory + Path.DirectorySeparatorChar + ".", _restoredContainer + ":" + remoteDatabase]);
            await DockerAsync(["exec", _restoredContainer, "chown", "-R", "postgres:postgres", remoteDatabase]);
            await DockerAsync(["exec", _restoredContainer, "chmod", "700", remoteDatabase]);
            var remoteArchive = _remote + "/archive";
            if (archiveReplay is not null)
            {
                await DockerAsync(["exec", _restoredContainer, "mkdir", "-p", remoteArchive]);
                await DockerAsync(["cp", archiveReplay.ArchiveDirectory + Path.DirectorySeparatorChar + ".", _restoredContainer + ":" + remoteArchive]);
                await DockerAsync(["exec", _restoredContainer, "chown", "-R", "postgres:postgres", remoteArchive]);
            }
            // The replay switches contain no archive path. Validate the real host archive before using
            // these switches inside the Linux test container.
            var replayOptions = archiveReplay is null ? "" : ControlledRecoveryDatabase.BuildArchiveReplayOptions(archiveReplay);
            await DockerAsync(["exec", "--user", "postgres", _restoredContainer, "pg_ctl", "--pgdata=" + remoteDatabase,
                "--log=/tmp/codex-f01-recovery.log", $"--options=-p {selectedPort} -h 0.0.0.0{replayOptions}", "--wait", "start"]);
            var port = (await DockerAsync(["port", _restoredContainer, selectedPort + "/tcp"])).StandardOutput.Trim();
            if (!port.StartsWith("127.0.0.1:", StringComparison.Ordinal) || !int.TryParse(port.Split(':')[1], out var number))
                throw new InvalidDataException("隔离副本必须只监听本机回环随机端口。");
            if (number != RestoredPort) throw new InvalidDataException("实际恢复端口与受控部署不一致。");
        }
        public async Task PauseSourceAsync()
        {
            if (!_sourceContainer.StartsWith("codex-f01-physical-source-", StringComparison.Ordinal))
                throw new InvalidOperationException("只允许暂停专用 F01 合成源容器。");
            await DockerAsync(["pause", _sourceContainer]);
            _sourcePaused = true;
        }
        public async Task ResumeSourceAsync()
        {
            if (!_sourcePaused) return;
            await DockerAsync(["unpause", _sourceContainer]);
            _sourcePaused = false;
        }
        public async ValueTask DisposeAsync()
        {
            await ResumeSourceAsync();
            if (_restoredCreated) await DockerAsync(["rm", "--force", "--volumes", _restoredContainer]);
            if (_sourceContainer.StartsWith("codex-f01-physical-source-", StringComparison.Ordinal))
                await DockerAsync(["exec", _sourceContainer, "rm", "--recursive", "--force", _remote]);
        }
    }
}
