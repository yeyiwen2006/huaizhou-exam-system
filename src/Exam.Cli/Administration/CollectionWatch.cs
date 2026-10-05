using Exam.Contracts.Submissions;

/// <summary>
/// “本场考试全部答卷已成功回收”事件的 CLI 侧状态机：按名单版本推进游标，
/// 每个名单版本只发出一次，零参试者不触发；校验口径与考务端弹窗一致
/// （同一端点 <c>GET /api/v1/submissions/collection/events</c>）。
/// </summary>
public sealed class CollectionWatch
{
    private readonly Dictionary<string, CollectionCompletedNotification> _emittedById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _versionByEventId = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _eventIdByVersion = [];

    /// <summary>从指定名单版本之后开始读取；默认从头读取（0）。</summary>
    public CollectionWatch(long cursor = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cursor);
        Cursor = cursor;
    }

    /// <summary>已接收到的最大名单版本；下一次按此游标继续读取。</summary>
    public long Cursor { get; private set; }

    /// <summary>
    /// 校验并接收一页收齐事件，返回本次应当发出的事件（按名单版本升序）。
    /// 校验不通过时抛出 <see cref="InvalidOperationException"/>，且不改变游标与已发出记录。
    /// </summary>
    public IReadOnlyList<CollectionCompletedNotification> Accept(
        string examId, IReadOnlyList<CollectionCompletedNotification> page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        ArgumentNullException.ThrowIfNull(page);
        // 零参试不得触发；考试、标识、名单版本与收齐时间必须有效。
        if (page.Any(item => item is null || !StringComparer.Ordinal.Equals(item.ExamId, examId) ||
                string.IsNullOrWhiteSpace(item.EventId) || item.RosterVersion < 1 || item.ParticipantCount <= 0 ||
                item.CompletedAt == default))
            throw new InvalidOperationException("收齐事件的考试、标识、名单版本或参试人数无效，未发出该页结果。");
        // 同一事件重复投递时内容必须一致：内容变过的重投与考务端弹窗同口径地拒绝整页。
        if (page.Any(item => _emittedById.TryGetValue(item.EventId, out var previous) && previous != item))
            throw new InvalidOperationException("收齐事件的内容与已发出的不一致，未发出该页结果。");
        // 同一名单版本只对应一个事件，同一事件只对应一个名单版本。
        if (page.GroupBy(item => item.RosterVersion).Any(group => group.Distinct().Count() != 1) ||
            page.GroupBy(item => item.EventId, StringComparer.Ordinal).Any(group => group.Distinct().Count() != 1) ||
            page.Any(item => _eventIdByVersion.TryGetValue(item.RosterVersion, out var known) &&
                !StringComparer.Ordinal.Equals(known, item.EventId)) ||
            page.Any(item => _versionByEventId.TryGetValue(item.EventId, out var seen) && seen != item.RosterVersion))
            throw new InvalidOperationException("收齐事件的标识与名单版本不一致，未发出该页结果。");

        var emitted = new List<CollectionCompletedNotification>();
        foreach (var item in page.Distinct().OrderBy(item => item.RosterVersion))
        {
            if (item.RosterVersion <= Cursor) continue;
            if (_versionByEventId.TryAdd(item.EventId, item.RosterVersion))
            {
                _emittedById[item.EventId] = item;
                emitted.Add(item);
            }
            _eventIdByVersion[item.RosterVersion] = item.EventId;
            Cursor = item.RosterVersion;
        }
        return emitted;
    }
}
