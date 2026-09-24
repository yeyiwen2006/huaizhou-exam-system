using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;
using Exam.IntegrationTests.Postgres;

namespace Exam.IntegrationTests.ServiceManagement;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresBackupPersistenceTests(PostgresDatabaseFixture fixture) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"exam-backup-persistence-{Guid.NewGuid():N}");

    [Fact]
    public async Task RequestReplay_SurvivesCoordinatorRestart_AndDoesNotRunToolAgain()
    {
        var backupRoot = Path.Combine(_root, "backups");
        var resources = Path.Combine(_root, "resources");
        var protectedConfiguration = Path.Combine(_root, "protected");
        var restore = Path.Combine(_root, "restore");
        Directory.CreateDirectory(resources);
        Directory.CreateDirectory(protectedConfiguration);
        await File.WriteAllTextAsync(Path.Combine(resources, "paper.pkg"), "encrypted-fixture");
        await File.WriteAllTextAsync(Path.Combine(protectedConfiguration, "node.dat"), "protected-fixture");
        var runner = new FakePostgresTools();
        var options = new PostgresBackupOptions(
            Path.GetFullPath(_root),
            fixture.DataSource.ConnectionString,
            Path.GetFullPath(backupRoot),
            Path.GetFullPath(restore),
            Path.GetFullPath(resources),
            Path.GetFullPath(protectedConfiguration),
            TimeSpan.FromSeconds(5));
        var destination = Path.Combine(backupRoot, "backup-1");
        var request = new BackupRequest($"request-{Guid.NewGuid():N}", destination, "operator");

        var first = await new PostgresBackupCoordinator(
            options,
            runner,
            TimeProvider.System,
            fixture.DataSource).CreateAsync(request, default);
        var replay = await new PostgresBackupCoordinator(
            options,
            runner,
            TimeProvider.System,
            fixture.DataSource).CreateAsync(request, default);

        Assert.Equal(first, replay);
        Assert.Equal(1, runner.BaseBackupCalls);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresBackupCoordinator(
            options,
            runner,
            TimeProvider.System,
            fixture.DataSource).CreateAsync(request with { ActorId = "another-operator" }, default));

        var restoreRequest = new RestoreRequest(
            $"restore-{Guid.NewGuid():N}",
            first.BackupId,
            destination,
            "operator");
        await new PostgresBackupCoordinator(
            options,
            runner,
            TimeProvider.System,
            fixture.DataSource).RestoreAsync(restoreRequest, default);
        await new PostgresBackupCoordinator(
            options,
            runner,
            TimeProvider.System,
            fixture.DataSource).RestoreAsync(restoreRequest, default);
        Assert.Equal(3, runner.VerifyBackupCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PostgresBackupCoordinator(
            options,
            runner,
            TimeProvider.System,
            fixture.DataSource).RestoreAsync(
                restoreRequest with { ActorId = "another-operator" },
                default));
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(_root);
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(full).StartsWith("exam-backup-persistence-", StringComparison.Ordinal) &&
            Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
    }

    private sealed class FakePostgresTools : IExternalProcessRunner
    {
        public int BaseBackupCalls { get; private set; }

        public int VerifyBackupCalls { get; private set; }

        public Task<ExternalProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment,
            TimeSpan timeout,
            CancellationToken cancellationToken = default,
            string? workingDirectory = null)
        {
            if (Path.GetFileName(executable).Equals("pg_basebackup.exe", StringComparison.OrdinalIgnoreCase))
            {
                BaseBackupCalls++;
                var target = arguments[1];
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "backup_manifest"), "synthetic");
                File.WriteAllText(Path.Combine(target, "PG_VERSION"), "18");
            }
            else if (Path.GetFileName(executable).Equals("pg_verifybackup.exe", StringComparison.OrdinalIgnoreCase))
            {
                VerifyBackupCalls++;
            }

            return Task.FromResult(new ExternalProcessResult(0, string.Empty, string.Empty));
        }
    }
}
