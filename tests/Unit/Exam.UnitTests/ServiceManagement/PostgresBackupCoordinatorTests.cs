using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;
using System.Text.Json.Nodes;

namespace Exam.UnitTests.ServiceManagement;

public sealed class PostgresBackupCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"exam-backup-test-{Guid.NewGuid():N}");

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
                File.WriteAllText(Path.Combine(target, "backup_manifest"), "synthetic");
                File.WriteAllText(Path.Combine(target, "PG_VERSION"), "18");
            }

            return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
        }
    }
}
