using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>Writes only fixed metadata. Formatted messages, exception text and state values never enter this file.</summary>
public sealed class ProtectedRuntimeLogWriter
{
    private const long MaximumBytes = 8 * 1024 * 1024;
    private readonly string _path;
    private readonly string _source;
    private readonly object _gate = new();

    public ProtectedRuntimeLogWriter(string path, string source = "business")
    {
        if (source is not ("business" or "management")) throw new ArgumentException("受保护日志来源无效。");
        _source = source;
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("受保护日志路径无效。");
        _path = Path.GetFullPath(path);
        Check();
    }

    public void Write(string category, string level, int eventId)
    {
        if (level is not ("Warning" or "Error" or "Critical") || !ValidCategory(category)) return;
        var entry = new ProtectedRuntimeLogEntry(DateTimeOffset.UtcNow, _source, level, category, eventId);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(entry, JsonDefaults.Create());
        lock (_gate)
        {
            try
            {
                Check();
                if (File.Exists(_path) && new FileInfo(_path).Length + bytes.Length + 1 > MaximumBytes)
                {
                    var previous = _path + ".previous";
                    PathBoundary.RejectExistingReparsePoints(Path.GetDirectoryName(_path)!, previous);
                    File.Move(_path, previous, overwrite: true);
                }
                using var output = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read,
                    4096, FileOptions.WriteThrough);
                output.Write(bytes);
                output.WriteByte((byte)'\n');
                output.Flush(flushToDisk: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Logging must not change the outcome of an exam transaction. A missing/unreadable
                // configured log is surfaced by the management read endpoint instead of logging secrets elsewhere.
            }
        }
    }

    private void Check()
    {
        var directory = Path.GetDirectoryName(_path)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("受保护日志目录不存在。");
        PathBoundary.RejectExistingReparsePoints(directory, _path);
    }

    public static bool ValidCategory(string category) => category.Length is > 0 and <= 200 &&
        category.StartsWith("Exam.", StringComparison.Ordinal) &&
        category.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '+');

}

public static class ProtectedRuntimeLogReader
{
    private const long MaximumBytes = 8 * 1024 * 1024;

    public static async Task<IReadOnlyList<ProtectedRuntimeLogEntry>> ReadAsync(string path, string source,
        int limit, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path) || source is not ("business" or "management") || limit is < 1 or > 100)
            throw new ArgumentException("受保护日志查询参数无效。");
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("受保护日志目录不存在。");
        var entries = new List<ProtectedRuntimeLogEntry>();
        foreach (var candidate in new[] { fullPath + ".previous", fullPath })
        {
            PathBoundary.RejectExistingReparsePoints(directory, candidate);
            if (!File.Exists(candidate)) continue;
            await using var input = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > MaximumBytes) throw new InvalidDataException("受保护日志超过读取上限。");
            using var reader = new StreamReader(input);
            string? line;
            while ((line = await reader.ReadLineAsync(token)) is not null)
            {
                if (line.Length > 4096) throw new InvalidDataException("受保护日志条目过大。");
                ProtectedRuntimeLogEntry? entry;
                try { entry = JsonSerializer.Deserialize<ProtectedRuntimeLogEntry>(line, JsonDefaults.Create()); }
                catch (JsonException) { throw new InvalidDataException("受保护日志格式无效。"); }
                if (entry is null || entry.Source != source || entry.Level is not ("Warning" or "Error" or "Critical") ||
                    !ProtectedRuntimeLogWriter.ValidCategory(entry.Category) || entry.Timestamp == default)
                    throw new InvalidDataException("受保护日志内容无效。");
                entries.Add(entry);
            }
        }
        return entries.OrderByDescending(x => x.Timestamp).Take(limit).ToArray();
    }
}
