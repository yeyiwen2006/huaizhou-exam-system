namespace Exam.Student.Core;

/// <summary>本次页面会话的一段失焦；不是服务器留痕或作弊判定。</summary>
public sealed record FocusLossEvent(DateTimeOffset LostAt, TimeSpan Duration)
{
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");
}

/// <summary>
/// 记录本次页面会话；失焦开始和结束共用事件标识，由运行时发件箱保存并上报。
/// 发生时刻使用 UTC，持续时长使用单调时钟，系统校时不产生负时长。
/// </summary>
public sealed class FocusTracker(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly List<FocusLossEvent> _events = [];
    private DateTimeOffset? _lostAt;
    private DateTimeOffset? _lastRegainedAt;
    private long _lostTimestamp;
    private string? _currentEventId;
    private long _revision;

    public int FocusLostCount { get { lock (_sync) return _events.Count + (_lostAt is null ? 0 : 1); } }
    public DateTimeOffset? LastFocusRegainedAt { get { lock (_sync) return _lastRegainedAt; } }
    public bool IsUnfocused { get { lock (_sync) return _lostAt is not null; } }
    public long Revision { get { lock (_sync) return _revision; } }

    public TimeSpan TotalUnfocusedDuration
    {
        get
        {
            lock (_sync)
            {
                var total = TimeSpan.Zero;
                foreach (var item in _events) total += item.Duration;
                if (_lostAt is not null) total += _time.GetElapsedTime(_lostTimestamp);
                return total;
            }
        }
    }

    public IReadOnlyList<FocusLossEvent> Snapshot() { lock (_sync) return _events.ToArray(); }

    /// <summary>切出时立即落盘的起始记录；复焦后用同一标识补齐时长。</summary>
    public FocusLossEvent? CurrentLoss
    {
        get { lock (_sync) return _lostAt is { } lost && _currentEventId is { } id
            ? new FocusLossEvent(lost, TimeSpan.Zero) { EventId = id } : null; }
    }

    public bool OnFocusLost()
    {
        lock (_sync)
        {
            if (_lostAt is not null) return false;
            _lostAt = _time.GetUtcNow();
            _currentEventId = Guid.NewGuid().ToString("N");
            _lostTimestamp = _time.GetTimestamp();
            _revision++;
            return true;
        }
    }

    public bool OnFocusRegained()
    {
        lock (_sync)
        {
            if (_lostAt is not { } lost) return false;
            _events.Add(new(lost, _time.GetElapsedTime(_lostTimestamp)) { EventId = _currentEventId! });
            _lastRegainedAt = _time.GetUtcNow();
            _lostAt = null;
            _currentEventId = null;
            _revision++;
            return true;
        }
    }

    public void Close() => OnFocusRegained();
}
