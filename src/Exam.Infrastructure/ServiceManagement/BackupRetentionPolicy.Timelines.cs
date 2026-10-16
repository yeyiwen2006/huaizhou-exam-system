using System.Globalization;

namespace Exam.Infrastructure.ServiceManagement;

public sealed record BackupWalRange(uint Timeline, string StartLsn, string EndLsn);
public sealed record ArchiveRetentionPlan(IReadOnlyList<string> ObsoleteWal,
    IReadOnlyList<string> RequiredWal, ulong OldestRequiredLsn);

public static partial class BackupRetentionPolicy
{
    /// <summary>
    /// Prove a continuous archive on the current timeline's ancestry before selecting any deletion.
    /// Unknown branches, history files, partial files and post-fork abandoned WAL are never selected.
    /// </summary>
    public static ArchiveRetentionPlan PlanArchiveCleanup(IEnumerable<string> filenames,
        IReadOnlyDictionary<uint, string> histories, IReadOnlyCollection<RetainedBackup> retained,
        uint currentTimeline, ulong segmentBytes, string currentLsn, IReadOnlyCollection<string> replicationPositions)
    {
        if (segmentBytes < 1024 * 1024 || segmentBytes > 1024UL * 1024 * 1024 ||
            (segmentBytes & (segmentBytes - 1)) != 0 || currentTimeline == 0 || retained.Count == 0)
            throw new InvalidDataException("恢复链缺少有效的备份、时间线或 WAL 段大小。");
        var current = CheckedLsn(currentLsn);
        var lineage = ReadLineage(histories, currentTimeline, current);
        var index = lineage.Select((span, i) => (span.Timeline, i)).ToDictionary(x => x.Timeline, x => x.i);
        var names = filenames.Where(IsWal).ToHashSet(StringComparer.Ordinal);
        foreach (var name in names)
            if (ulong.Parse(name[16..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) >= 0x100000000UL / segmentBytes)
                throw new InvalidDataException("归档包含无效的 WAL 段编号，未执行清理。");

        var earliestIndex = lineage.Count - 1;
        var earliestSegment = current / segmentBytes;
        void RequireFrom(int i, ulong segment)
        {
            if (i < earliestIndex || i == earliestIndex && segment < earliestSegment)
            { earliestIndex = i; earliestSegment = segment; }
        }
        foreach (var backup in retained)
        {
            if (!IsWal(backup.StartWal) || !index.TryGetValue(Timeline(backup.StartWal), out var i))
                throw new InvalidDataException("保留备份不属于当前时间线的祖先链，未删除相关恢复材料。");
            var start = Segment(backup.StartWal, segmentBytes);
            if (start > current / segmentBytes)
                throw new InvalidDataException("备份起点晚于当前数据库位置，未执行清理。");
            if (backup.WalRanges is not { Count: > 0 } ranges)
                throw new InvalidDataException("备份缺少原生 WAL 起止范围，无法核对跨时间线恢复链。");
            if (ranges[0].Timeline != Timeline(backup.StartWal) || CheckedLsn(ranges[0].StartLsn) / segmentBytes != start)
                throw new InvalidDataException("备份标签与原生 WAL 范围不一致。");
            ulong previousEnd = 0;
            var previousIndex = -1;
            foreach (var range in ranges)
            {
                if (!index.TryGetValue(range.Timeline, out var ri))
                    throw new InvalidDataException("备份 WAL 范围包含另一条时间线分支。");
                var begin = CheckedLsn(range.StartLsn);
                var end = CheckedLsn(range.EndLsn);
                var span = lineage[ri];
                if (end <= begin || begin < span.Start || end > span.End || ri < previousIndex ||
                    previousIndex >= 0 && begin != previousEnd)
                    throw new InvalidDataException("备份结束位置越过分叉点或 WAL 范围不连续，不能用它证明当前恢复链。");
                previousEnd = end; previousIndex = ri;
            }
            RequireFrom(i, start);
        }
        foreach (var position in replicationPositions)
        {
            var lsn = CheckedLsn(position);
            if (lsn > current) throw new InvalidDataException("复制保留位置晚于当前主库，未执行清理。");
            var i = lineage.FindIndex(span => lsn < span.End);
            if (i < 0) i = lineage.Count - 1;
            RequireFrom(i, lsn / segmentBytes);
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        ulong inspections = 0;
        for (var i = earliestIndex; i < lineage.Count; i++)
        {
            var span = lineage[i];
            var first = i == earliestIndex ? earliestSegment : span.Start / segmentBytes;
            // A closed ancestor includes its fork segment; the current open segment is not archived yet.
            var exclusive = i == lineage.Count - 1 ? current / segmentBytes :
                span.End / segmentBytes + (span.End % segmentBytes == 0 ? 0UL : 1UL);
            if (exclusive < first) throw new InvalidDataException("WAL 恢复边界无效。");
            inspections += exclusive - first;
            if (inspections > (ulong)names.Count + (ulong)lineage.Count)
                throw new InvalidDataException("归档数量不足以覆盖保留窗口；缺失恢复段时不执行清理。");
            for (var segment = first; segment < exclusive; segment++)
            {
                var name = WalName(span.Timeline, segment, segmentBytes);
                if (names.Contains(name)) { required.Add(name); continue; }
                // PostgreSQL copies the ancestor prefix into the child's first WAL segment.
                // A full descendant fork segment can supply that prefix, but never a .partial file.
                var replacement = lineage.Skip(i + 1)
                    .Where(child => child.Start / segmentBytes == segment)
                    .Select(child => WalName(child.Timeline, segment, segmentBytes))
                    .LastOrDefault(names.Contains);
                if (replacement is null)
                    throw new InvalidDataException($"恢复归档缺少 {name}，本轮不删除任何备份或 WAL。");
                required.Add(replacement);
            }
        }
        var obsolete = new List<string>();
        foreach (var name in names)
        {
            if (required.Contains(name) || !index.TryGetValue(Timeline(name), out var i)) continue;
            var span = lineage[i];
            var boundary = i < earliestIndex
                ? span.End / segmentBytes + (span.End % segmentBytes == 0 ? 0UL : 1UL)
                : i == earliestIndex ? earliestSegment : span.Start / segmentBytes;
            if (Segment(name, segmentBytes) < boundary) obsolete.Add(name);
        }
        return new(obsolete.Order(StringComparer.Ordinal).ToArray(), required.Order(StringComparer.Ordinal).ToArray(),
            earliestSegment * segmentBytes);
    }

    private sealed record TimelineSpan(uint Timeline, ulong Start, ulong End);
    private static List<TimelineSpan> ReadLineage(IReadOnlyDictionary<uint, string> histories, uint current, ulong currentLsn)
    {
        var result = new List<TimelineSpan>();
        ulong start = 0;
        uint previous = 0;
        if (current > 1)
        {
            if (!histories.TryGetValue(current, out var history) || history.Length > 1024 * 1024)
                throw new InvalidDataException("当前时间线历史尚未完整归档，保留恢复材料并等待下一轮。");
            foreach (var raw in history.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2 || !uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var tli) ||
                    tli <= previous || tli >= current || result.Count >= 1024)
                    throw new InvalidDataException("时间线历史结构无效，未执行清理。");
                var end = CheckedLsn(fields[1]);
                if (end < start || end > currentLsn || result.Count == 0 && tli != 1)
                    throw new InvalidDataException("时间线历史的分叉位置或祖先信息无效。");
                result.Add(new(tli, start, end));
                previous = tli; start = end;
            }
            if (result.Count == 0) throw new InvalidDataException("当前时间线缺少祖先信息。");
        }
        result.Add(new(current, start, currentLsn));
        return result;
    }

    private static ulong CheckedLsn(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || !uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var high) ||
            !uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var low))
            throw new InvalidDataException("数据库位置不是有效的 PostgreSQL LSN。");
        return ((ulong)high << 32) | low;
    }
}
