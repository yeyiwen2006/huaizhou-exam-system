using System.Text;
using Exam.Application.ServiceManagement;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Infrastructure.HighAvailability;

/// <summary>Only a deployment-owned archive script outside replaceable database trees may run.</summary>
public static class PostgresWalArchiving
{
    public static string BuildCommand(HighAvailabilityManagementOptions options)
    {
        var archive = DirectoryPath(options.WalArchiveDirectory);
        var scripts = DirectoryPath(options.WalArchiveScriptsDirectory);
        foreach (var path in new[] { archive, scripts })
            foreach (var replaced in new[] { options.PostgresDataDirectory, options.RebuildRoot, options.Nodes[options.LocalNodeId].ResourcesDirectory })
                RequireDisjoint(path, DirectoryPath(replaced, requireExists: false));
        RequireDisjoint(archive, scripts);
        foreach (var name in new[] { "Invoke-WalArchiveCommand.ps1", "Invoke-WalArchive.ps1", "Postgresql.Operations.psm1" })
        {
            var file = Path.Combine(scripts, name);
            PathBoundary.RejectExistingReparsePoints(Path.GetPathRoot(file)!, file);
            if (!File.Exists(file)) throw new IOException("缺少受保护的 WAL 归档工具，禁止生成服务启动命令。");
        }
        var shell = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") : "/usr/bin/pwsh";
        // No execution-policy override, no inherited command or PATH lookup on Windows.
        return $"\"{shell.Replace('\\', '/')}\" -NoProfile -NonInteractive -File \"{scripts.Replace('\\', '/')}/Invoke-WalArchiveCommand.ps1\" \"%p\" \"%f\" \"{archive.Replace('\\', '/')}\"";
    }

    private static string DirectoryPath(string path, bool requireExists = true)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) ||
            path.IndexOfAny(['\"', '\'', '%', '&', '|', '<', '>', '^', '!', '\r', '\n', '\0']) >= 0)
            throw new IOException("归档部署须使用无命令分隔符的本机绝对目录。");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full == Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ||
            full[Path.GetPathRoot(full)!.Length..].Contains(':')) throw new IOException("归档目录不能是卷根或附加数据流。");
        PathBoundary.RejectExistingReparsePoints(Path.GetPathRoot(full)!, full);
        if (requireExists && !Directory.Exists(full)) throw new IOException("归档及工具目录须由部署人员预先准备。");
        return full;
    }

    private static void RequireDisjoint(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (left.Equals(right, comparison) || left.StartsWith(right + Path.DirectorySeparatorChar, comparison) ||
            right.StartsWith(left + Path.DirectorySeparatorChar, comparison))
            throw new IOException("归档、工具与可替换数据库、资源或重建目录必须互相独立。");
    }

    // Windows CRT quoting, applied once for postgres arguments and again for pg_ctl's -o.
    public static string QuoteArgument(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var output = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            output.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); output.Append(c); slashes = 0;
        }
        return output.Append('\\', slashes * 2).Append('"').ToString();
    }
}

/// <summary>Reads actual runtime settings and archive receipts; neither edits SQL settings nor starts services.</summary>
public sealed class PostgresWalArchiveReadinessProbe(string connectionString, HighAvailabilityManagementOptions options)
    : IStartupReadinessProbe
{
    public async Task<IReadOnlyList<StartupCheckResult>> RunAsync(ServiceConfiguration configuration, CancellationToken cancellationToken)
    {
        try
        {
            var commandText = PostgresWalArchiving.BuildCommand(options);
            // Windows postgres stores startup command bytes in the native code page, not necessarily database UTF-8.
            // Compare ASCII hex of textsend rather than decoding native path bytes as UTF-8.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var nativeEncoding = OperatingSystem.IsWindows() ? Encoding.GetEncoding(0) : Encoding.UTF8;
            var expected = Convert.ToHexString(nativeEncoding.GetBytes(commandText)).ToLowerInvariant();
            await using var source = ManagementProbeConnection.Create(connectionString, 5);
            await using var command = source.CreateCommand("""
                SELECT pg_is_in_recovery(),current_setting('archive_mode'),encode(pg_catalog.textsend(current_setting('archive_command')),'hex'),
                       current_setting('archive_library'),current_setting('fsync'),current_setting('full_page_writes'),
                       archived_count,last_archived_time,last_failed_time
                FROM pg_stat_archiver
                """);
            await using var row = await command.ExecuteReaderAsync(cancellationToken);
            var passed = await row.ReadAsync(cancellationToken) && row.GetString(1) == "on" && row.GetString(2) == expected &&
                row.GetString(3) == "" && row.GetString(4) == "on" && row.GetString(5) == "on" &&
                (row.GetBoolean(0) || row.GetInt64(6) > 0 && !row.IsDBNull(7) &&
                    (row.IsDBNull(8) || row.GetDateTime(7) >= row.GetDateTime(8)));
            return [new("wal-archiving", passed, passed ? "受控连续归档配置已生效；活动主库的归档回执已核实。" :
                "连续归档未生效、命令不符或主库没有有效归档回执，禁止认作就绪。", true)];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return [new("wal-archiving", false, "无法核对受控 WAL 归档目录、命令或实际数据库状态。", true)];
        }
    }
}
