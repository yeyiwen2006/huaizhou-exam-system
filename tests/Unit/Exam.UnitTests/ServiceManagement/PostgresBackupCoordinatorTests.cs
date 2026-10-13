using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;
using System.Text.Json.Nodes;

namespace Exam.UnitTests.ServiceManagement;

public sealed class PostgresBackupCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"exam-backup-test-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("valid")]
    [InlineData("outside-root")]
    [InlineData("invalid-manifest")]
    public async Task BackupInfoReadsOnlyMetadataWithinTheAllowedRoot(string scenario)
    {
        var root = Path.Combine(_root, "backups");
        var selected = Path.Combine(root, "selected");
        var completed = new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.FromHours(8));
        var manifest = System.Text.Json.JsonSerializer.Serialize(new
        {
            formatVersion = 2, backupId = "known-backup", requestId = "original-request", completedAt = completed,
            actorId = "operator", databaseDirectory = "database", resourcesDirectory = "resources",
            protectedConfigurationDirectory = "protected-configuration",
            resourceFiles = new[] { new { relativePath = "paper.pkg", length = 1, sha256 = "metadata-only" } },
            protectedConfigurationFiles = new[] { new { relativePath = "configuration.json", length = 1, sha256 = "metadata-only" } }
        });
        WriteFixture(selected, "exam-backup.json", scenario == "invalid-manifest" ? "{}" : manifest);
        var runner = new FakePostgresTools();
        var reader = new PostgresBackupCoordinator(new PostgresBackupOptions("", "", root, "", "", "", TimeSpan.FromSeconds(5)), runner);
        if (scenario == "outside-root")
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ReadBackupInfoAsync(Path.Combine(_root, "other"), default));
        else if (scenario == "invalid-manifest")
            await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadBackupInfoAsync(selected, default));
        else
        {
            var info = await reader.ReadBackupInfoAsync(Path.Combine(root, "unused", "..", "selected"), default);
            Assert.Equal(new BackupInfo("known-backup", Path.GetFullPath(selected), completed), info);
            Assert.Equal(manifest, await File.ReadAllTextAsync(Path.Combine(selected, "exam-backup.json")));
        }
        Assert.Empty(runner.Calls);
        Assert.Single(Directory.EnumerateFiles(selected));
        Assert.Empty(Directory.EnumerateDirectories(selected));
    }

    [Theory]
    [InlineData("replay")]
    [InlineData("different-actor")]
    [InlineData("different-request")]
    [InlineData("partial")]
    [InlineData("overlap")]
    public async Task RecoveryWorkingCopy_IsBoundAndPreservedWithoutOriginalSourceMaterials(string scenario)
    {
        var root = Path.Combine(_root, "backups");
        var resources = Path.Combine(_root, "resources");
        var protectedConfig = Path.Combine(_root, "protected");
        WriteFixture(resources, "paper.pkg", "synthetic ciphertext");
        WriteFixture(protectedConfig, "key.dat", "synthetic key");
        var service = CreateService(root, resources, protectedConfig, Path.Combine(_root, "staging"), new FakePostgresTools());
        var backup = await service.CreateAsync(new("backup", Path.Combine(root, "selected"), "operator"), default);
        Directory.Delete(resources, true);
        Directory.Delete(protectedConfig, true);
        var request = new RestoreRequest("restore", backup.BackupId, backup.DestinationDirectory, "operator");
        await service.RestoreAsync(request, default); // Restore must use the backup, not demand healthy original sources.
        var staged = Path.Combine(_root, "staging", backup.BackupId);
        var stagedMarker = File.ReadAllBytes(Path.Combine(staged, "restore-verified.json"));
        var workingRoot = scenario == "overlap" ? root : Path.Combine(_root, "working");
        Directory.CreateDirectory(workingRoot);
        if (scenario == "overlap")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareRecoveryWorkingCopyAsync(request, workingRoot, default));
            return;
        }
        var working = Path.Combine(workingRoot, "slot");
        if (scenario == "partial")
        {
            WriteFixture(working, "keep.dat", "interrupted copy");
            await Assert.ThrowsAnyAsync<IOException>(() => service.PrepareRecoveryWorkingCopyAsync(request, workingRoot, default));
            Assert.Equal("interrupted copy", File.ReadAllText(Path.Combine(working, "keep.dat")));
            Assert.False(File.Exists(Path.Combine(working, "working-copy.json")));
            return;
        }
        var prepared = await service.PrepareRecoveryWorkingCopyAsync(request, workingRoot, default);
        Assert.True(Directory.Exists(Path.Combine(working, "database", "pg_notify")));
        Assert.Equal(stagedMarker, File.ReadAllBytes(Path.Combine(staged, "restore-verified.json")));
        File.WriteAllText(Path.Combine(working, "database", "PG_VERSION"), "synthetic changed working state");
        if (scenario == "replay")
            Assert.Equal(prepared, await service.PrepareRecoveryWorkingCopyAsync(request, workingRoot, default));
        else
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.PrepareRecoveryWorkingCopyAsync(
                scenario == "different-actor" ? request with { ActorId = "another" } : request with { RequestId = "another" }, workingRoot, default));
        Assert.Equal("synthetic changed working state", File.ReadAllText(Path.Combine(working, "database", "PG_VERSION")));
        Assert.Equal("18", File.ReadAllText(Path.Combine(staged, "database", "PG_VERSION")));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-id")]
    [InlineData("resource-corrupt")]
    [InlineData("protected-missing")]
    [InlineData("tool-failure")]
    public async Task StartupBackupIntegrity_UsesOnlySelectedBackupAndNeverCreatesRestoreTarget(string scenario)
    {
        var root = Path.Combine(_root, "backups");
        var resources = Path.Combine(_root, "resources");
        var protectedConfig = Path.Combine(_root, "protected");
        WriteFixture(resources, "paper.pkg", "synthetic ciphertext");
        WriteFixture(protectedConfig, "key.dat", "synthetic recovery material");
        var destination = Path.Combine(root, "selected");
        var created = await CreateService(root, resources, protectedConfig, Path.Combine(_root, "staging"),
            new FakePostgresTools()).CreateAsync(new("backup-request", destination, "operator"), default);
        var manifest = File.ReadAllBytes(Path.Combine(destination, "exam-backup.json"));
        Directory.Delete(resources, true);
        Directory.Delete(protectedConfig, true);
        if (scenario == "resource-corrupt") File.WriteAllText(Path.Combine(destination, "resources", "paper.pkg"), "corrupt");
        if (scenario == "protected-missing") File.Delete(Path.Combine(destination, "protected-configuration", "key.dat"));
        var tools = new FakePostgresTools { FailedTool = scenario == "tool-failure" ? "pg_verifybackup.exe" : null,
            FailureOutput = "synthetic-secret-do-not-report" };
        var verifier = new PostgresBackupCoordinator(new PostgresBackupOptions(
            Path.GetDirectoryName(Environment.ProcessPath!)!, "", root, "", "", "", TimeSpan.FromSeconds(5)), tools);
        var probe = new BackupIntegrityStartupReadinessProbe(() => verifier, destination,
            scenario == "wrong-id" ? "another-backup" : created.BackupId);
        var result = Assert.Single(await probe.RunAsync(new("server", "node", "127.0.0.1", 443, _root, true, 1), default));
        Assert.Equal("backup-integrity", result.Name);
        Assert.Equal(scenario == "valid", result.Passed);
        Assert.True(result.BlocksWrites);
        Assert.DoesNotContain("synthetic-secret", result.Message);
        Assert.Equal(manifest, File.ReadAllBytes(Path.Combine(destination, "exam-backup.json")));
        Assert.False(Directory.Exists(Path.Combine(_root, "staging")));
        Assert.DoesNotContain("pg_basebackup.exe", tools.Calls);
        Assert.Equal(scenario is "valid" or "tool-failure" ? 1 : 0, tools.Calls.Count);
    }

    [Fact]
    public async Task StartupBackupIntegrity_MissingSelectionDoesNotLoadDependencies_AndCancellationPropagates()
    {
        var probe = new BackupIntegrityStartupReadinessProbe(() => throw new Exception("Must stay deferred"), null, null);
        var configuration = new ServiceConfiguration("server", "node", "127.0.0.1", 443, _root, true, 1);
        Assert.False(Assert.Single(await probe.RunAsync(configuration, default)).Passed);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.RunAsync(configuration, cancellation.Token));
    }

    [Fact]
    public async Task BackupAndRestore_UseCompleteIsolatedDirectories()
    {
        var backupRoot = Path.Combine(_root, "backups");
        var resources = Path.Combine(_root, "resources");
        var protectedConfig = Path.Combine(_root, "protected");
        var staging = Path.Combine(_root, "staging");
        WriteFixture(resources, "paper.pkg", "encrypted");
        WriteFixture(protectedConfig, "key-recovery.dat", "protected");
        var runner = new FakePostgresTools();
        var service = CreateService(backupRoot, resources, protectedConfig, staging, runner);
        var destination = Path.Combine(backupRoot, "backup-1");

        var result = await service.CreateAsync(new BackupRequest("request-1", destination, "operator"), default);

        Assert.True(result.IncludesDatabase);
        Assert.True(result.IncludesProtectedConfiguration);
        Assert.True(result.IncludesVersionedResources);
        Assert.True(File.Exists(Path.Combine(destination, "exam-backup.json")));
        Assert.True(File.Exists(Path.Combine(destination, "resources", "paper.pkg")));

        await service.RestoreAsync(
            new RestoreRequest("restore-1", result.BackupId, destination, "operator"),
            default);
        await service.RestoreAsync(
            new RestoreRequest("restore-1", result.BackupId, destination, "operator"),
            default);

        Assert.True(File.Exists(Path.Combine(staging, result.BackupId, "database", "backup_manifest")));
        Assert.True(Directory.Exists(Path.Combine(staging, result.BackupId, "database", "pg_notify")));
        Assert.True(Directory.Exists(Path.Combine(staging, result.BackupId, "database", "pg_wal", "archive_status")));
        Assert.True(File.Exists(Path.Combine(staging, result.BackupId, "protected-configuration", "key-recovery.dat")));
        Assert.True(File.Exists(Path.Combine(staging, result.BackupId, "restore-verified.json")));
        Assert.Equal(4, runner.Calls.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(
            new RestoreRequest("restore-1", result.BackupId, destination, "another-operator"),
            default));
    }

    [Theory]
    [InlineData("pg_basebackup.exe")]
    [InlineData("pg_verifybackup.exe")]
    public async Task FailedPostgresTool_DoesNotExposeCapturedOutput(string failedTool)
    {
        const string secret = "synthetic-secret-do-not-report";
        var backupRoot = Path.Combine(_root, "backups");
        var resources = Path.Combine(_root, "resources");
        var protectedConfig = Path.Combine(_root, "protected");
        WriteFixture(resources, "paper.pkg", "synthetic ciphertext");
        WriteFixture(protectedConfig, "key.dat", "synthetic protected material");
        var runner = new FakePostgresTools
        {
            FailedTool = failedTool,
            FailureOutput = secret
        };
        var service = CreateService(backupRoot, resources, protectedConfig,
            Path.Combine(_root, "staging"), runner);
        var destination = Path.Combine(backupRoot, "backup-1");

        var exception = await Assert.ThrowsAsync<IOException>(async () =>
        {
            var backup = await service.CreateAsync(
                new BackupRequest("backup-request", destination, "operator"), default);
            await service.RestoreAsync(
                new RestoreRequest("restore-request", backup.BackupId, destination, "operator"), default);
        });

        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.Contains("退出码 17", exception.Message, StringComparison.Ordinal);
        if (failedTool == "pg_basebackup.exe")
        {
            Assert.False(File.Exists(Path.Combine(destination, "exam-backup.json")));
        }
        else
        {
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "staging"),
                "restore-verified.json", SearchOption.AllDirectories));
        }
    }

    [Fact]
    public async Task Backup_RejectsDestinationOutsideConfiguredRoot()
    {
        var backupRoot = Path.Combine(_root, "backups");
        var resources = Path.Combine(_root, "resources");
        var protectedConfig = Path.Combine(_root, "protected");
        WriteFixture(resources, "paper.pkg", "encrypted");
        WriteFixture(protectedConfig, "key.dat", "protected");
        var service = CreateService(
            backupRoot,
            resources,
            protectedConfig,
            Path.Combine(_root, "staging"),
            new FakePostgresTools());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(
            new BackupRequest("request-escape", Path.Combine(_root, "outside"), "operator"),
            default));
    }

    [Fact]
    public async Task Backup_RejectsDestinationThroughDirectoryLink()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var backupRoot = Path.Combine(_root, "backups");
        var outside = Path.Combine(_root, "outside");
        var link = Path.Combine(backupRoot, "linked");
        var resources = Path.Combine(_root, "resources");
        var protectedConfig = Path.Combine(_root, "protected");
        Directory.CreateDirectory(backupRoot);
        Directory.CreateDirectory(outside);
        WriteFixture(resources, "paper.pkg", "encrypted");
        WriteFixture(protectedConfig, "key.dat", "protected");

        try
        {
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        var service = CreateService(
            backupRoot,
            resources,
            protectedConfig,
            Path.Combine(_root, "staging"),
            new FakePostgresTools());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(
            new BackupRequest("request-link", Path.Combine(link, "escaped"), "operator"),
            default));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    public async Task Restore_RejectsAmbiguousOrIncompleteManifest(string mutation)
    {
        var backupRoot = Path.Combine(_root, mutation, "backups");
        var resources = Path.Combine(_root, mutation, "resources");
        var protectedConfig = Path.Combine(_root, mutation, "protected");
        WriteFixture(resources, "paper.pkg", "synthetic ciphertext");
        WriteFixture(protectedConfig, "key.dat", "synthetic protected material");
        var runner = new FakePostgresTools();
        var service = CreateService(backupRoot, resources, protectedConfig,
            Path.Combine(_root, mutation, "staging"), runner);
        var destination = Path.Combine(backupRoot, "backup-1");
        var result = await service.CreateAsync(new BackupRequest("backup-request", destination, "operator"), default);
        var manifestPath = Path.Combine(destination, "exam-backup.json");
        var json = await File.ReadAllTextAsync(manifestPath);
        var changed = mutation switch
        {
            "duplicate" => json.Insert(json.LastIndexOf('}'), $",\"backupId\":\"{result.BackupId}\""),
            "unknown" => json.Insert(json.LastIndexOf('}'), ",\"ignored\":true"),
            _ => RemoveProperty(json, "backupId")
        };
        await File.WriteAllTextAsync(manifestPath, changed);
        var callsBeforeRestore = runner.Calls.Count;

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(
            new RestoreRequest("restore-request", result.BackupId, destination, "operator"), default));

        Assert.Equal(callsBeforeRestore, runner.Calls.Count);
    }

    [Fact]
    public async Task RestoreReplay_RejectsAmbiguousCompletionMarker()
    {
        var backupRoot = Path.Combine(_root, "backups");
        var resources = Path.Combine(_root, "resources");
        var protectedConfig = Path.Combine(_root, "protected");
        var staging = Path.Combine(_root, "staging");
        WriteFixture(resources, "paper.pkg", "synthetic ciphertext");
        WriteFixture(protectedConfig, "key.dat", "synthetic protected material");
        var service = CreateService(backupRoot, resources, protectedConfig, staging, new FakePostgresTools());
        var destination = Path.Combine(backupRoot, "backup-1");
        var result = await service.CreateAsync(new BackupRequest("backup-request", destination, "operator"), default);
        var restore = new RestoreRequest("restore-request", result.BackupId, destination, "operator");
        await service.RestoreAsync(restore, default);
        var markerPath = Path.Combine(staging, result.BackupId, "restore-verified.json");
        var json = await File.ReadAllTextAsync(markerPath);
        await File.WriteAllTextAsync(markerPath, json.Insert(json.LastIndexOf('}'), ",\"actorId\":\"operator\""));

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(restore, default));
    }

    [Fact]
    public async Task ExistingFileEvidenceIsObservedWithoutDatabaseOrRepeatingSideEffects()
    {
        var backupRoot = Path.Combine(_root, "backups");
        var resources = Path.Combine(_root, "resources");
        var protectedConfig = Path.Combine(_root, "protected");
        var staging = Path.Combine(_root, "staging");
        WriteFixture(resources, "paper.pkg", "synthetic ciphertext");
        WriteFixture(protectedConfig, "key.dat", "synthetic material");
        var runner = new FakePostgresTools();
        var service = CreateService(backupRoot, resources, protectedConfig, staging, runner);
        var request = new BackupRequest("backup", Path.Combine(backupRoot, "snapshot"), "operator");
        Assert.Null(await service.FindCompletedBackupAsync(request, default));
        var backup = await service.CreateAsync(request, default);
        var restore = new RestoreRequest("restore", backup.BackupId, request.DestinationDirectory, "operator");
        Assert.False(await service.FindStagedRestoreAsync(restore, default));
        await service.RestoreAsync(restore, default);
        var markerPath = Path.Combine(staging, backup.BackupId, "restore-verified.json");
        var marker = await File.ReadAllBytesAsync(markerPath);
        var restarted = CreateService(backupRoot, resources, protectedConfig, staging, runner);
        Assert.Equal(backup, await restarted.FindCompletedBackupAsync(request, default));
        Assert.True(await restarted.FindStagedRestoreAsync(restore, default));
        Assert.Equal(marker, await File.ReadAllBytesAsync(markerPath));
        Assert.Equal(1, runner.Calls.Count(c => c == "pg_basebackup.exe"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.FindCompletedBackupAsync(request with { ActorId = "forged" }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.FindStagedRestoreAsync(restore with { RequestId = "other" }, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostMarkerOrDamagedStagedResourceCannotBecomeValidatedEvidence(bool damageResource)
    {
        var backupRoot = Path.Combine(_root, "backups");
        var resources = Path.Combine(_root, "resources");
        var protectedConfig = Path.Combine(_root, "protected");
        var staging = Path.Combine(_root, "staging");
        WriteFixture(resources, "paper.pkg", "synthetic ciphertext");
        WriteFixture(protectedConfig, "key.dat", "synthetic material");
        var runner = new FakePostgresTools();
        var service = CreateService(backupRoot, resources, protectedConfig, staging, runner);
        var backup = await service.CreateAsync(new("backup", Path.Combine(backupRoot, "snapshot"), "operator"), default);
        var restore = new RestoreRequest("restore", backup.BackupId, backup.DestinationDirectory, "operator");
        await service.RestoreAsync(restore, default);
        var target = Path.Combine(staging, backup.BackupId);
        if (damageResource) await File.WriteAllTextAsync(Path.Combine(target, "resources", "paper.pkg"), "damaged synthetic file");
        else File.Delete(Path.Combine(target, "restore-verified.json"));
        if (damageResource) await Assert.ThrowsAsync<InvalidDataException>(() => service.FindStagedRestoreAsync(restore, default));
        else await Assert.ThrowsAsync<IOException>(() => service.FindStagedRestoreAsync(restore, default));
        Assert.Equal(1, runner.Calls.Count(c => c == "pg_basebackup.exe"));
    }

    private static string RemoveProperty(string json, string name)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove(name);
        return node.ToJsonString();
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(_root);
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(full).StartsWith("exam-backup-test-", StringComparison.Ordinal) &&
            Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }

    private static PostgresBackupCoordinator CreateService(
        string backupRoot,
        string resources,
        string protectedConfig,
        string staging,
        IExternalProcessRunner runner) =>
        new(
            new PostgresBackupOptions(
                Path.GetDirectoryName(Environment.ProcessPath!)!,
                "Host=127.0.0.1;Username=fixture;Database=postgres;SSL Mode=Disable",
                backupRoot,
                staging,
                resources,
                protectedConfig,
                TimeSpan.FromSeconds(5)),
            runner);

    private static void WriteFixture(string directory, string name, string value)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), value);
    }

    private sealed class FakePostgresTools : IExternalProcessRunner
    {
        public List<string> Calls { get; } = [];
        public string? FailedTool { get; init; }
        public string FailureOutput { get; init; } = string.Empty;

        public Task<ExternalProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment,
            TimeSpan timeout,
            CancellationToken cancellationToken = default,
            string? workingDirectory = null,
            ExternalProcessLimits? limits = null)
        {
            Calls.Add(Path.GetFileName(executable));
            if (Path.GetFileName(executable).Equals(FailedTool, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new ExternalProcessResult(17, FailureOutput, FailureOutput));
            }
            if (Path.GetFileName(executable).Equals("pg_basebackup.exe", StringComparison.OrdinalIgnoreCase))
            {
                var target = arguments[1];
                Directory.CreateDirectory(target);
                Directory.CreateDirectory(Path.Combine(target, "pg_notify"));
                Directory.CreateDirectory(Path.Combine(target, "pg_wal", "archive_status"));
                File.WriteAllText(Path.Combine(target, "backup_manifest"), "synthetic");
                File.WriteAllText(Path.Combine(target, "PG_VERSION"), "18");
            }

            return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
        }
    }
}
