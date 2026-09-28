namespace Exam.Student.Core;

/// <summary>一次失焦事件的记录（发生时间与持续时长）。</summary>
public sealed record FocusLossEvent(DateTimeOffset LostAt, TimeSpan Duration);

/// <summary>
/// B06 失焦检测：考试进行页窗口失焦/复焦的计数与时长记录，
/// 由考试页挂接窗口事件驱动；重新获得焦点时通知同步服务立即补报（不必等 60 秒周期）。
/// 纯内存会话级记录；上报经 SyncProgressRequest 的可选字段随进度同步。
/// </summary>
public sealed class FocusTracker(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly List<FocusLossEvent> _events = [];
    private DateTimeOffset? _lostAt;

    /// <summary>考试进行页累计失焦次数（当前正在失焦的也计入）。</summary>
    public int FocusLostCount
    {
        get { lock (_sync) return _events.Count; }
    }

    /// <summary>最近一次重新获得焦点的时间；尚未发生失焦为 null。</summary>
    public DateTimeOffset? LastFocusRegainedAt
    {
        get { lock (_sync) return _events.Count > 0 ? _events[^1].LostAt + _events[^1].Duration : null; }
    }

    /// <summary>当前是否处于失焦状态。</summary>
    public bool IsUnfocused
    {
        get { lock (_sync) return _lostAt is not null; }
    }

    /// <summary>累计失焦总时长（进行中的失焦按当前时刻累计）。</summary>
    public TimeSpan TotalUnfocusedDuration
    {
        get
        {
            lock (_sync)
            {
                var total = TimeSpan.Zero;
                foreach (var item in _events) total += item.Duration;
                if (_lostAt is { } lost) total += _time.GetUtcNow() - lost;
                return total;
            }
        }
    }

    /// <summary>已结束的失焦事件快照（按发生顺序）。</summary>
    public IReadOnlyList<FocusLossEvent> Snapshot()
    {
        lock (_sync) return _events.ToArray();
    }

    /// <summary>窗口失去焦点。重复调用（仍处于失焦）不重复计数。</summary>
    /// <returns>本次是否由"有焦点"转为"失焦"。</returns>
    public bool OnFocusLost()
    {
        lock (_sync)
        {
            if (_lostAt is not null)
            {
                return false;
            }
            _lostAt = _time.GetUtcNow();
            return true;
        }
    }

    /// <summary>窗口重新获得焦点。未处于失焦状态时调用无效。</summary>
    /// <returns>本次是否结束了一段有效失焦（即需要立即补报）。</returns>
    public bool OnFocusRegained()
    {
        lock (_sync)
        {
            if (_lostAt is not { } lost)
            {
                return false;
            }
            var now = _time.GetUtcNow();
            _events.Add(new FocusLossEvent(lost, now - lost));
            _lostAt = null;
            return true;
        }
    }

    /// <summary>离开考试页时收尾：把仍在进行的失焦按当前时刻记完。</summary>
    public void Close()
    {
        lock (_sync)
        {
            if (_lostAt is { } lost)
            {
                _events.Add(new FocusLossEvent(lost, _time.GetUtcNow() - lost));
                _lostAt = null;
            }
        }
    }
}
