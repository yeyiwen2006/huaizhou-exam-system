using Exam.Infrastructure.HighAvailability;
using Exam.Infrastructure.ServiceManagement;
using Npgsql;

namespace Exam.UnitTests.ServiceManagement;

public sealed class PostgresNativeTlsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exam-native-tls-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, SslMode.VerifyCA, "verify-ca")]
    [InlineData(false, SslMode.VerifyFull, "verify-full")]
    [InlineData(false, SslMode.Disable, "disable")]
    [InlineData(true, SslMode.VerifyCA, "verify-ca")]
    [InlineData(true, SslMode.VerifyFull, "verify-full")]
    [InlineData(true, SslMode.Disable, "disable")]
    public async Task BackupAndRebuildPassNativeTlsModeAndRootCertificate(bool rebuild, SslMode mode, string expectedMode)
    {
        var certificate = mode == SslMode.Disable ? null : Path.Combine(_root, "public-ca.cer");
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = 5444, Username = "synthetic", Database = "postgres",
            SslMode = mode, RootCertificate = certificate
        };
        var runner = new CapturingRunner();
        var tools = Path.Combine(_root, "bin");
        if (rebuild)
        {
            var service = new StagedPostgresNodeRebuilder(new(tools, Path.Combine(_root, "rebuild"),
                new Dictionary<string, PostgresNodeConnection> { ["source"] = new("source", connection.ConnectionString) },
                TimeSpan.FromSeconds(5)), runner);
            await Assert.ThrowsAsync<IOException>(() => service.PrepareRebuildAsync("standby", "source", default));
        }
        else
        {
            var resources = Path.Combine(_root, "resources");
            var configuration = Path.Combine(_root, "protected");
            Directory.CreateDirectory(resources);
            Directory.CreateDirectory(configuration);
            File.WriteAllText(Path.Combine(resources, "synthetic.txt"), "synthetic resource");
            File.WriteAllText(Path.Combine(configuration, "synthetic.txt"), "synthetic configuration");
            var backups = Path.Combine(_root, "backups");
            var service = new PostgresBackupCoordinator(new(tools, connection.ConnectionString, backups,
                Path.Combine(_root, "restore"), resources, configuration, TimeSpan.FromSeconds(5)), runner);
            await Assert.ThrowsAsync<IOException>(() => service.CreateAsync(new("request", Path.Combine(backups, "selected"), "operator"), default));
        }

        Assert.Equal(1, runner.Calls);
        Assert.Equal(OperatingSystem.IsWindows() ? "pg_basebackup.exe" : "pg_basebackup", runner.Tool);
        Assert.Equal(expectedMode, runner.Environment["PGSSLMODE"]);
        if (certificate is null)
            Assert.True(string.IsNullOrEmpty(runner.Environment.GetValueOrDefault("PGSSLROOTCERT")));
        else
            Assert.Equal(certificate, runner.Environment["PGSSLROOTCERT"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // Capture the native process boundary without connecting to PostgreSQL or creating a backup.
    private sealed class CapturingRunner : IExternalProcessRunner
    {
        public int Calls { get; private set; }
        public string Tool { get; private set; } = "";
        public IReadOnlyDictionary<string, string> Environment { get; private set; } = new Dictionary<string, string>();

        public Task<ExternalProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken = default,
            string? workingDirectory = null, ExternalProcessLimits? limits = null)
        {
            Calls++;
            Tool = Path.GetFileName(executable);
            Environment = new Dictionary<string, string>(environment);
            return Task.FromResult(new ExternalProcessResult(1, "", "Synthetic stop after parameter capture."));
        }
    }
}
