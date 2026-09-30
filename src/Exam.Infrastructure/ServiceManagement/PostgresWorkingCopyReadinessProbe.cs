using Exam.Contracts.ServiceManagement;
using Exam.Application.ServiceManagement;
using Exam.Infrastructure.Postgres;
using Npgsql;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>Verifies the actual isolated database location before running domain checks against that database.</summary>
public sealed class PostgresWorkingCopyReadinessProbe(string originalConnectionString, string recoveryConnectionString,
    string certificateThumbprint, long expectedEpoch, IStartupReadinessProbe? recoveryReadiness = null,
    NpgsqlDataSource? actualSource = null)
{
    public async Task<IReadOnlyList<StartupCheckResult>> RunAsync(ServiceConfiguration configuration,
        RecoveryWorkingCopy workingCopy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var original = new NpgsqlConnectionStringBuilder(originalConnectionString);
            var target = new NpgsqlConnectionStringBuilder(recoveryConnectionString);
            static bool IsLocal(string? host) => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                host is "127.0.0.1" or "::1";
            if (!IsLocal(target.Host) || IsLocal(original.Host) && original.Port == target.Port ||
                !string.Equals(original.Database, target.Database, StringComparison.Ordinal) || expectedEpoch < 1)
                throw new InvalidOperationException("恢复数据库必须为独立回环目标且使用原数据库标识。");
            var expectedDirectory = Path.GetFullPath(Path.Combine(workingCopy.WorkingDirectory, "database"));
            PathBoundary.RejectExistingReparsePoints(workingCopy.WorkingDirectory, expectedDirectory);
            await using var ownedSource = actualSource is null ? ManagementProbeConnection.Create(recoveryConnectionString, 10) : null;
            var source = actualSource ?? ownedSource!;
            await using (var connection = await source.OpenConnectionAsync(cancellationToken))
            await using (var command = new NpgsqlCommand("SELECT current_database(), current_setting('data_directory'), inet_server_port(), pg_is_in_recovery()", connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != target.Database ||
                    !string.Equals(Path.GetFullPath(reader.GetString(1)), expectedDirectory, comparison) ||
                    reader.GetInt32(2) != target.Port || reader.GetBoolean(3))
                    throw new InvalidDataException("实际恢复数据库目标不匹配。");
            }
            var checks = new List<StartupCheckResult> { new("recovery-target", true, "实际数据库、端口及独立工作目录已核实。", true) };
            checks.AddRange(await new ManagementDatabaseStartupProbe(recoveryConnectionString, expectedEpoch).RunAsync(configuration, cancellationToken));
            checks.AddRange(await (recoveryReadiness ?? new ConfiguredRecoveryReadinessProbe(recoveryConnectionString, certificateThumbprint))
                .RunAsync(configuration, cancellationToken));
            return checks;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return [new("recovery-target", false, "无法核实独立恢复数据库目标，未对原数据库执行恢复检查或开放业务。", true)];
        }
    }
}
