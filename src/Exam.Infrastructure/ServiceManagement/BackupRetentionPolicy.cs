using System.Globalization;
using System.Text.RegularExpressions;

namespace Exam.Infrastructure.ServiceManagement;

public sealed record RetainedBackup(string Directory, string BackupId, DateTimeOffset CompletedAt,
    string StartWal, string SystemIdentifier, bool Automatic);

/// <summary>Retention is determined by recovery positions, never by archive file modification time.</summary>
public static partial class BackupRetentionPolicy
{
    public static IReadOnlyList<RetainedBackup> Keep(IEnumerable<RetainedBackup> source, DateTimeOffset now, int days)
    {
        if (days < 1) throw new ArgumentOutOfRangeException(nameof(days));
        var all = source.OrderBy(b => b.CompletedAt).ToArray();
        var cutoff = now.AddDays(-days);
        var keep = all.Where(b => b.CompletedAt >= cutoff).ToHashSet();
        if (all.LastOrDefault(b => b.CompletedAt < cutoff) is { } anchor) keep.Add(anchor);
        foreach (var backup in all.TakeLast(2)) keep.Add(backup);
        return keep.OrderBy(b => b.CompletedAt).ToArray();
    }

    public static ulong Lsn(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2) throw new InvalidDataException("Invalid WAL position.");
        return ulong.Parse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture) * 0x100000000UL
               + ulong.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public static bool IsWal(string name) => WalPattern().IsMatch(name);
    public static uint Timeline(string name) => uint.Parse(name[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    public static ulong Segment(string name, ulong size) =>
        ulong.Parse(name[8..16], NumberStyles.HexNumber, CultureInfo.InvariantCulture) * (0x100000000UL / size)
        + ulong.Parse(name[16..24], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    public static string WalName(uint timeline, ulong segment, ulong size) =>
        $"{timeline:X8}{segment / (0x100000000UL / size):X8}{segment % (0x100000000UL / size):X8}";

    public static IReadOnlyList<string> ObsoleteWal(IEnumerable<string> source, uint timeline, ulong size,
        ulong floor, ulong current)
    {
        var names = source.Where(IsWal).Where(n => Timeline(n) == timeline).ToArray();
        var available = names.Select(n => Segment(n, size)).ToHashSet();
        for (var segment = floor; segment < current; segment++)
            if (!available.Contains(segment)) throw new InvalidDataException("恢复所需的连续归档不完整，已暂停清理。");
        return names.Where(n => Segment(n, size) < floor).Order(StringComparer.Ordinal).ToArray();
    }

    public static string Child(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        path = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, path);
        if (relative == "." || Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
            throw new InvalidDataException("维护路径超出允许的目录。");
        RejectLinks(path);
        return path;
    }

    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("维护目录不能通过链接跳转。");
    }

    [GeneratedRegex("^[0-9A-F]{24}$", RegexOptions.CultureInvariant)]
    private static partial Regex WalPattern();
}
