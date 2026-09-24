using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

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
