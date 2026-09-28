using System.IO;
using System.Net.Http;
using Exam.Contracts.Commands;

namespace Exam.Student.Core;

/// <summary>B06 在线更新协调器的对外阶段。</summary>
public enum PaperUpdatePhase
{
    /// <summary>没有待处理的试卷更新。</summary>
    None,
    /// <summary>已收到更新事件，尚未开始同步。</summary>
    Pending,
    /// <summary>正在下载/解锁/切换新版题包。</summary>
    Syncing,
    /// <summary>新版已切换完成，等待考生复核确认。</summary>
    Applied,
    /// <summary>本次同步失败，保留旧版继续作答，按退避自动重试。</summary>
    Failed,
}

public sealed record PaperUpdateStatusChangedEventArgs(
    PaperUpdatePhase Phase, string Message, long PendingVersion, long InstalledVersion);

/// <summary>
/// B06 在线更新编排：收到 PaperUpdated 事件后驱动管理器完成
/// 重新准备→下载→确认→解锁→原子切换，并按结果向服务器补报
/// Applied（含实际版本）或 ResourceUpdateFailed（含原因）。
/// 全程不阻塞作答：切换成功前旧版明文持续可用；失败自动退避重试；
/// Applied 确认丢失时保留补发清单直到服务器确认。
/// </summary>
public sealed class PaperUpdateCoordinator(ExamPackageManager manager, StudentExamRuntime runtime) : IAsyncDisposable
{
    private static readonly TimeSpan RetryBase = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryMax = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan AckRetryInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan WaitForUnlockPoll = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly List<CommandDelivery> _pending = [];
    private readonly List<(string EventId, long Version)> _unackedApplied = [];
    private TaskCompletionSource<bool> _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _wakeRequested;
    private Task? _loop;
    private bool _disposed;
    private int _retryCount;
    private PaperUpdatePhase _phase = PaperUpdatePhase.None;
    private string _statusText = string.Empty;

    /// <summary>触发 UI 刷新；事件已封送到 UI 线程。</summary>
    public event EventHandler<PaperUpdateStatusChangedEventArgs>? StatusChanged;

    /// <summary>新版切换成功（等待考生复核确认时由界面提示并记录）。</summary>
    public event EventHandler<(long Version, DateTimeOffset ReceivedAt, DateTimeOffset InstalledAt)>? UpdateInstalled;

    public PaperUpdatePhase Phase => _phase;
    public string StatusText => _statusText;

    /// <summary>当前待应用的最高试卷版本；无更新为 0。</summary>
    public long PendingVersion
    {
        get
        {
            lock (_sync)
            {
                return _pending.Count > 0 ? _pending.Max(item => item.ResourceVersion ?? item.StateVersion) : 0;
            }
        }
    }

    /// <summary>收到 PaperUpdated 事件后由运行时调用；多次调用合并到同一后台循环。</summary>
    public void Notify(CommandDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        if (_disposed) return;
        lock (_sync)
        {
            if (_pending.Any(item => item.EventId == delivery.EventId)) return;
            _pending.Add(delivery);
        }
        RequestWake();
        StartLoop();
        SetPhase(PaperUpdatePhase.Pending,
            $"已收到试卷更新（版本 {PendingVersion}），正在准备同步新版试题；当前版本可继续作答。");
    }

    private void StartLoop()
    {
        lock (_sync)
        {
            if (_loop is not null || _disposed) return;
            _loop = Task.Run(() => RunAsync(_lifetime.Token));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var delay = await AttemptAsync(ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) break;
                if (delay is not { } wait) break; // 无待处理事件，退出循环（下次 Notify 重新启动）。
                await WaitAsync(wait, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_sync) { _loop = null; }
        }
    }

    /// <summary>执行一轮同步尝试；返回 null 表示没有待处理事件，否则为下一轮等待间隔。</summary>
    private async Task<TimeSpan?> AttemptAsync(CancellationToken ct)
    {
        // 先补发此前未确认成功的 Applied 结果。
        if (await ResendUnackedAsync(ct).ConfigureAwait(false))
        {
            return AckRetryInterval;
        }

        List<CommandDelivery> snapshot;
        lock (_sync) { snapshot = _pending.ToList(); }
        if (snapshot.Count == 0)
        {
            if (_phase is not (PaperUpdatePhase.None or PaperUpdatePhase.Applied))
            {
                SetPhase(PaperUpdatePhase.None, string.Empty);
            }
            return null;
        }
        if (manager.ExamEnded || manager.CurrentPhase is ExamPackageManager.Phase.Revoked or
            ExamPackageManager.Phase.Ended or ExamPackageManager.Phase.Withdrawn)
        {
            // 终态：不再应用更新；未确认事件保持 Received，由考务端按未完成处理。
            lock (_sync) { _pending.Clear(); }
            SetPhase(PaperUpdatePhase.None, string.Empty);
            return null;
        }

        var target = snapshot.Max(item => item.ResourceVersion ?? item.StateVersion);
        var installed = manager.Descriptor?.PaperVersion ?? 0;

        if (manager.CurrentPhase != ExamPackageManager.Phase.Unlocked)
        {
            // 等待页阶段：等待重新准备与解锁把版本推上去，然后直接确认事件。
            if (installed >= target)
            {
                await AcknowledgeAsync(snapshot, applied: true, installed, ct).ConfigureAwait(false);
                lock (_sync) { _pending.Clear(); }
                return null;
            }
            SetPhase(PaperUpdatePhase.Pending,
                $"已收到试卷更新（版本 {target}），等待考试资料重新准备完成后自动同步。");
            return WaitForUnlockPoll;
        }

        if (installed >= target)
        {
            await AcknowledgeAsync(snapshot, applied: true, installed, ct).ConfigureAwait(false);
            lock (_sync) { _pending.Clear(); }
            return null;
        }

        SetPhase(PaperUpdatePhase.Syncing,
            $"正在同步新版试题（版本 {target}）；当前版本可继续作答，切换完成前答案与标记保持不变。");
        var progress = new Progress<PackageDownloadProgress>(value =>
            SetPhase(PaperUpdatePhase.Syncing,
                $"正在下载新版试题（{value.ReceivedBytes / 1024:F0} / {value.TotalBytes / 1024:F0} KB）；当前版本可继续作答。"));
        var result = await manager.ApplyPaperUpdateAsync(progress, ct).ConfigureAwait(false);
        if (result.Succeeded)
        {
            _retryCount = 0;
            var receivedAt = snapshot.Min(item => item.CreatedAt);
            var installedAt = DateTimeOffset.UtcNow;
            await AcknowledgeAsync(snapshot, applied: true, result.InstalledPaperVersion, ct).ConfigureAwait(false);
            lock (_sync) { _pending.Clear(); }
            // 切换后驱动运行时重建纸面并结转答案会话（B06 版本切换路径）复核状态与界面提示无关，
            // 无论 UI 是否订阅事件都必须落内存，供 ConfirmPaperUpdateReviewAsync 读取。
            await runtime.SynchronizeLocalAnswersAsync(CancellationToken.None).ConfigureAwait(false);
            runtime.RecordInstalledPaperUpdate(result.InstalledPaperVersion, receivedAt, installedAt);
            RaiseInstalled(result.InstalledPaperVersion, receivedAt, installedAt);
            SetPhase(PaperUpdatePhase.Applied,
                $"试题已修订为版本 {result.InstalledPaperVersion}，原答案、标记与当前题已保留，请复核修订后的试题。");
            return null;
        }

        _retryCount++;
        var backoff = TimeSpan.FromSeconds(Math.Min(
            RetryBase.TotalSeconds * Math.Pow(2, _retryCount - 1), RetryMax.TotalSeconds));
        await AcknowledgeAsync(snapshot, applied: false, 0, ct, result.Error).ConfigureAwait(false);
        SetPhase(PaperUpdatePhase.Failed,
            $"新版试题同步失败：{result.Error} 约 {Math.Ceiling(backoff.TotalSeconds)} 秒后自动重试；当前版本可继续作答。");
        return backoff;
    }

    /// <summary>补发此前 Applied 确认失败的事件；返回 true 表示仍有待补发项。</summary>
    private async Task<bool> ResendUnackedAsync(CancellationToken ct)
    {
        List<(string EventId, long Version)> snapshot;
        lock (_sync) { snapshot = _unackedApplied.ToList(); }
        if (snapshot.Count == 0) return false;
        foreach (var (eventId, version) in snapshot)
        {
            try
            {
                if (await manager.ReportUpdateOutcomeAsync(eventId, true, version, null, ct).ConfigureAwait(false))
                {
                    lock (_sync) { _unackedApplied.RemoveAll(item => item.EventId == eventId); }
                }
                else
                {
                    return true;
                }
            }
            catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or
                HttpRequestException or IOException or UnauthorizedAccessException)
            {
                if (ct.IsCancellationRequested) throw;
                return true;
            }
        }
        return _unackedApplied.Count > 0;
    }

    private async Task AcknowledgeAsync(
        List<CommandDelivery> events, bool applied, long actualVersion, CancellationToken ct,
        string? failureReason = null)
    {
        foreach (var delivery in events)
        {
            try
            {
                var acknowledged = await manager.ReportUpdateOutcomeAsync(
                    delivery.EventId, applied, actualVersion, failureReason, ct).ConfigureAwait(false);
                if (!acknowledged && applied)
                {
                    lock (_sync) { _unackedApplied.Add((delivery.EventId, actualVersion)); }
                }
            }
            catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or
                HttpRequestException or IOException or UnauthorizedAccessException)
            {
                if (ct.IsCancellationRequested) throw;
                // 确认失败不阻断更新流程；Applied 结果进入补发清单，失败结果由下一轮重试重新上报。
                if (applied)
                {
                    lock (_sync) { _unackedApplied.Add((delivery.EventId, actualVersion)); }
                }
                break;
            }
        }
    }

    private void RaiseInstalled(long version, DateTimeOffset receivedAt, DateTimeOffset installedAt)
    {
        var handler = UpdateInstalled;
        if (handler is null) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            handler(this, (version, receivedAt, installedAt));
        }
        else if (!dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_disposed) handler(this, (version, receivedAt, installedAt));
            }));
        }
    }

    private void SetPhase(PaperUpdatePhase phase, string text)
    {
        _phase = phase;
        _statusText = text;
        RaiseStatusChanged();
    }

    private void RaiseStatusChanged()
    {
        var handler = StatusChanged;
        if (handler is null) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            handler(this, new PaperUpdateStatusChangedEventArgs(_phase, _statusText, PendingVersion,
                manager.Descriptor?.PaperVersion ?? 0));
        }
        else if (!dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_disposed)
                {
                    handler(this, new PaperUpdateStatusChangedEventArgs(_phase, _statusText, PendingVersion,
                        manager.Descriptor?.PaperVersion ?? 0));
                }
            }));
        }
    }

    private void RequestWake()
    {
        lock (_sync)
        {
            _wakeRequested = true;
            _wake.TrySetResult(true);
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        Task wakeTask;
        lock (_sync)
        {
            if (_wakeRequested)
            {
                _wakeRequested = false;
                _wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                return;
            }
            wakeTask = _wake.Task;
        }
        var completed = await Task.WhenAny(wakeTask, Task.Delay(delay, ct)).ConfigureAwait(false);
        lock (_sync)
        {
            _wakeRequested = false;
            if (ReferenceEquals(completed, wakeTask))
            {
                _wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        lock (_sync) { _wake.TrySetResult(false); }
        try
        {
            Task? loop;
            lock (_sync) { loop = _loop; }
            if (loop is not null) await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _lifetime.Dispose();
        }
    }
}
