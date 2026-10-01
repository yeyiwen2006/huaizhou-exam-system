using System.Globalization;
using System.Text;
using Exam.Contracts.HighAvailability;
using Exam.Infrastructure.ServiceManagement;
using Npgsql;

namespace Exam.Infrastructure.HighAvailability;

/// <summary>Only an actual replication-protocol read can construct a timeline continuation.
/// A larger numeric LSN or a caller-supplied history is not permission to replace the old head.</summary>
internal sealed class VerifiedTimelineContinuation
{
    private VerifiedTimelineContinuation(DurableConfirmation previous, int timeline)
    { Previous = previous; Timeline = timeline; }
    internal DurableConfirmation Previous { get; }
    internal int Timeline { get; }

    internal static async Task<VerifiedTimelineContinuation> ReadAsync(NpgsqlDataSource source,
        DurableConfirmation previous, int timeline, string? replicationConnection, string? postgresBinDirectory, CancellationToken token)
    {
        if (timeline <= previous.Timeline) throw new IOException("恢复时间线没有合法后继关系。");
        var database = new NpgsqlConnectionStringBuilder(source.ConnectionString);
        var replication = new NpgsqlConnectionStringBuilder(replicationConnection ?? source.ConnectionString)
        { Pooling = false, Timeout = 3, CommandTimeout = 10 };
        if (!string.Equals(database.Host, replication.Host, StringComparison.OrdinalIgnoreCase) || database.Port != replication.Port ||
            database.Database != replication.Database || database.SslMode != replication.SslMode)
            throw new IOException("时间线连接必须指向相同数据库端点及 TLS 边界。");
        if (string.IsNullOrWhiteSpace(postgresBinDirectory) || !Path.IsPathFullyQualified(postgresBinDirectory))
            throw new IOException("时间线核对需要受保护的 PostgreSQL 工具目录。");
        var executable = Path.Combine(postgresBinDirectory, OperatingSystem.IsWindows() ? "psql.exe" : "psql");
        PathBoundary.RejectExistingReparsePoints(Path.GetPathRoot(executable)!, executable);
        // libpq's simple replication protocol avoids Npgsql 10.0.3 TimelineHistory's
        // string-to-byte[] cast. No SQL file-reading grants or caller-provided history are used.
        var environment = new Dictionary<string, string>
        {
            ["PGHOST"] = replication.Host ?? throw new IOException("时间线数据库主机缺失。"),
            ["PGPORT"] = replication.Port.ToString(CultureInfo.InvariantCulture),
            ["PGUSER"] = replication.Username ?? throw new IOException("时间线用户缺失。"),
            ["PGPASSWORD"] = replication.Password ?? "",
            ["PGSSLMODE"] = replication.SslMode switch
            {
                SslMode.VerifyCA => "verify-ca", SslMode.VerifyFull => "verify-full",
                _ => replication.SslMode.ToString().ToLowerInvariant()
            },
            ["PGSSLROOTCERT"] = replication.RootCertificate ?? "",
            ["PGSSLCERT"] = replication.SslCertificate ?? "",
            ["PGSSLKEY"] = replication.SslKey ?? "",
            ["PGCONNECT_TIMEOUT"] = "3",
            ["PGCLIENTENCODING"] = "UTF8",
            ["PGOPTIONS"] = ""
        };
        static string Quote(string value) => "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        var result = await new ExternalProcessRunner().RunAsync(executable,
            ["-X", "-w", "-A", "-t", "-F", "\t", "-v", "ON_ERROR_STOP=1",
             "--dbname", "replication=true dbname=" + Quote(replication.Database ?? "postgres"),
             "-c", "IDENTIFY_SYSTEM", "-c", $"TIMELINE_HISTORY {timeline.ToString(CultureInfo.InvariantCulture)}"],
            environment, TimeSpan.FromSeconds(10), token);
        if (result.ExitCode != 0 || Encoding.UTF8.GetByteCount(result.StandardOutput) > 17000)
            throw new IOException("实际 PostgreSQL 时间线证据读取失败。");
        var output = result.StandardOutput.Replace("\r\n", "\n");
        var identityEnd = output.IndexOf('\n');
        if (identityEnd < 0) throw new IOException("实际集群身份缺失。");
        var identity = output[..identityEnd].Split('\t');
        if (identity.Length != 4 || identity[0] != previous.SystemIdentifier ||
            identity[1] != timeline.ToString(CultureInfo.InvariantCulture) || identity[3].Length != 0)
            throw new IOException("时间线证据不属于实际恢复集群。");
        var history = output[(identityEnd + 1)..];
        var prefix = $"{timeline:X8}.history\t";
        if (!history.StartsWith(prefix, StringComparison.Ordinal) ||
            !TimelineHistoryCoverage.Covers(Encoding.UTF8.GetBytes(history[prefix.Length..]), timeline,
                previous.Timeline, FileConfirmationStore.Lsn(previous.FlushLsn)))
            throw new IOException("实际恢复分叉点未覆盖旧持久确认。");
        return new(previous, timeline);
    }
}

/// <summary>Strict bounded parser only; parsing does not create a store continuation permission.</summary>
public static class TimelineHistoryCoverage
{
    public static bool Covers(byte[] content, int timeline, int predecessor, ulong requiredPosition)
    {
        if (content.Length is < 1 or > 16384 || predecessor < 1 || timeline <= predecessor || requiredPosition == 0) return false;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(content);
            int priorTimeline = 0, entries = 0; ulong priorPosition = 0; bool covered = false;
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                var parts = trimmed.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
                if (++entries > 256 || parts.Length < 2 ||
                    !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ancestor) ||
                    ancestor <= priorTimeline || ancestor >= timeline) return false;
                var position = FileConfirmationStore.Lsn(parts[1]);
                if (position == 0 || position < priorPosition) return false;
                if (ancestor == predecessor) covered = position >= requiredPosition;
                priorTimeline = ancestor; priorPosition = position;
            }
            return covered;
        }
        catch (Exception error) when (error is DecoderFallbackException or InvalidDataException) { return false; }
    }
}
