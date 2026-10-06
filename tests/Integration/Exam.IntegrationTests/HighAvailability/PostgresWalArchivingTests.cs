using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.HighAvailability;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.IntegrationTests.HighAvailability;

public sealed class PostgresWalArchivingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wal-archive-" + Guid.NewGuid().ToString("N"));
    private HighAvailabilityManagementOptions Options()
    {
        Directory.CreateDirectory(_root);
        var scripts = Path.Combine(_root, "scripts"); Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(Path.Combine(_root, "archive"));
        foreach (var file in new[] { "Invoke-WalArchive.ps1", "Invoke-WalArchiveCommand.ps1", "Postgresql.Operations.psm1" })
            File.WriteAllText(Path.Combine(scripts, file), "# Synthetic binding only; never executed.");
        var nodes = new Dictionary<string, FailoverNodeDeployment> { ["node_b"] = new("node_b",
            "Host=127.0.0.1;Port=5444;Database=synthetic", Path.Combine(_root, "resources"), "synthetic", _root, "https://synthetic.test/") };
        return new("server", "node_b", nodes, Path.Combine(_root, "selection"), Path.Combine(_root, "rebuild"),
            Path.Combine(_root, "bin"), Path.Combine(_root, "data"), "SyntheticPostgres", "SyntheticBusiness", "synthetic",
            Path.Combine(_root, "postgresql.conf"), Path.Combine(_root, "archive"), scripts);
    }

    [Fact]
    public void RuntimeServicePinsArchiveCommandAndKeepsRecoveryHooksDisabled()
    {
        var options = Options();
        var args = PostgresHighAvailabilityManagementPlatform.BuildPostgresRuntimeArguments(options);
        Assert.Contains("archive_mode=on", args); Assert.DoesNotContain("archive_mode=off", args);
        Assert.Contains("archive_library=", args); Assert.Contains("restore_command=exit 1", args);
        Assert.Contains("archive_cleanup_command=", args); Assert.Contains("recovery_end_command=", args);
        Assert.Contains("fsync=on", args); Assert.Contains("full_page_writes=on", args);
        Assert.Contains("archive_command=" + PostgresWalArchiving.BuildCommand(options), args);
        var command = PostgresHighAvailabilityManagementPlatform.BuildPostgresServiceCommand(options);
        Assert.Contains("archive_mode=on", command); Assert.DoesNotContain("ExecutionPolicy", command);
        Assert.Contains("Invoke-WalArchiveCommand.ps1", command);
        Assert.DoesNotContain("listen_addresses=127.0.0.1", command);
        // Isolated recovery is not a runtime primary: its original boundary stays intact.
        Assert.Contains("archive_mode=off", ControlledRecoveryDatabase.BuildServiceCommand("SyntheticRecovery",
            options.PostgresBinDirectory, options.RebuildRoot, options.PostgresConfigurationPath, 5445));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("data")]
    [InlineData("rebuild")]
    [InlineData("resources")]
    [InlineData("scripts")]
    [InlineData("delimiter")]
    public void UnsafeOrMissingArchiveBindingsFailClosed(string problem)
    {
        var options = Options();
        options = options with { WalArchiveDirectory = problem switch {
            "missing" => Path.Combine(_root, "absent"), "data" => options.PostgresDataDirectory,
            "rebuild" => options.RebuildRoot, "resources" => options.Nodes["node_b"].ResourcesDirectory,
            "scripts" => options.WalArchiveScriptsDirectory, _ => Path.Combine(_root, "bad%PATH%") } };
        if (problem is "data" or "rebuild" or "resources") Directory.CreateDirectory(options.WalArchiveDirectory);
        Assert.Throws<IOException>(() => PostgresHighAvailabilityManagementPlatform.BuildPostgresServiceCommand(options));
    }

    [Fact]
    public async Task MissingBindingCannotProduceHealthyArchiveProbe()
    {
        var options = Options() with { WalArchiveScriptsDirectory = Path.Combine(_root, "missing") };
        var check = Assert.Single(await new PostgresWalArchiveReadinessProbe("Host=127.0.0.1;Port=1", options)
            .RunAsync(new("server", "node_b", "127.0.0.1", 1, _root, true, 1), default));
        Assert.Equal("wal-archiving", check.Name); Assert.False(check.Passed); Assert.True(check.BlocksWrites);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
