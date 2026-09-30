using Exam.Contracts.ServiceManagement;
using Exam.Application.ServiceManagement;
using Exam.Infrastructure.Postgres;
using Npgsql;
using System.Text.Json;

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
            var manifestPath = Path.Combine(expectedDirectory, "backup_manifest");
            PathBoundary.RejectExistingReparsePoints(workingCopy.WorkingDirectory, manifestPath);
            await using var manifestFile = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (manifestFile.Length is <= 0 or > 16 * 1024 * 1024) throw new IOException("恢复源物理清单无效。");
            using var manifest = await JsonDocument.ParseAsync(manifestFile, cancellationToken: cancellationToken);
            var systemIdentifier = manifest.RootElement.GetProperty("System-Identifier").GetRawText();
            var ranges = manifest.RootElement.GetProperty("WAL-Ranges");
            if (ranges.ValueKind != JsonValueKind.Array || ranges.GetArrayLength() is < 1 or > 256)
                throw new IOException("恢复源时间线范围无效。");
            var sourceTimeline = ranges.EnumerateArray().Max(range => range.GetProperty("Timeline").GetInt32());
            if (sourceTimeline < 1) throw new IOException("恢复源时间线无效。");
            await using var ownedSource = actualSource is null ? ManagementProbeConnection.Create(recoveryConnectionString, 10) : null;
            var source = actualSource ?? ownedSource!;
            await using (var connection = await source.OpenConnectionAsync(cancellationToken))
            await using (var command = new NpgsqlCommand("""
                SELECT current_database(),current_setting('data_directory'),inet_server_port(),pg_is_in_recovery(),
                       system_identifier::text,timeline_id FROM pg_control_system() CROSS JOIN pg_control_checkpoint()
                """, connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!await reader.ReadAsync(cancellationToken) || reader.GetString(0) != target.Database ||
                    !string.Equals(Path.GetFullPath(reader.GetString(1)), expectedDirectory, comparison) ||
                    reader.GetInt32(2) != target.Port || reader.GetBoolean(3) || reader.GetString(4) != systemIdentifier ||
                    reader.GetInt32(5) <= sourceTimeline)
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
