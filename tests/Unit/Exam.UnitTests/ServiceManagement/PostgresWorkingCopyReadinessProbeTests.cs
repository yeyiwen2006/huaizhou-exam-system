using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class PostgresWorkingCopyReadinessProbeTests
{
    [Theory]
    [InlineData("127.0.0.1", 5432, "synthetic", 1)]
    [InlineData("localhost", 5432, "synthetic", 1)]
    [InlineData("remote.invalid", 5433, "synthetic", 1)]
    [InlineData("127.0.0.1", 5433, "other", 1)]
    [InlineData("127.0.0.1", 5433, "synthetic", 0)]
    public async Task RejectsOriginalRemoteWrongDatabaseOrUnknownEpochBeforeConnecting(string host, int port, string database, long epoch)
    {
        var target = $"Host={host};Port={port};Database={database};Password=synthetic-secret";
        var probe = new PostgresWorkingCopyReadinessProbe("Host=localhost;Port=5432;Database=synthetic", target, "", epoch);
        var configuration = new ServiceConfiguration("server", "node", "127.0.0.1", 8443, "/synthetic", false, 1);
        var copy = new RecoveryWorkingCopy("request", "operator", "backup", "/synthetic-backup", "/synthetic-copy", DateTimeOffset.UtcNow);
        var result = Assert.Single(await probe.RunAsync(configuration, copy, default));
        Assert.False(result.Passed);
        Assert.True(result.BlocksWrites);
        Assert.DoesNotContain("synthetic-secret", result.Message);
        Assert.Equal("recovery-target", result.Name);
    }
}
