using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Exam.Contracts.Commands;

namespace Exam.Student.Core;

public enum PaperUpdatePhase { None, Pending, Syncing, Applied, Failed }
public sealed record PaperUpdateStatusChangedEventArgs(
    PaperUpdatePhase Phase, string Message, long PendingVersion, long InstalledVersion);

/// <summary>
/// 从管理器的持久指令日志恢复更新任务。下载、答案事务和版本发布成功后才确认 Applied；
/// 网络确认失败不撤销已提交内容，不阻塞后续版本。没有第二份只在内存中的待确认清单。
/// </summary>
public sealed class PaperUpdateCoordinator : IAsyncDisposable
{
    private readonly ExamPackageManager _manager;
    private readonly StudentExamRuntime _runtime;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _sync = new();
    private Task? _loop;
    private Task? _disposeTask;
    private bool _disposed;
    private int _retryCount;
    private long _announcedVersion;
    private volatile PaperUpdatePhase _phase;
    private string _statusText = string.Empty;

    public PaperUpdateCoordinator(ExamPackageManager manager, StudentExamRuntime runtime)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        Retry();
    }

    public event EventHandler<PaperUpdateStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<(long Version, DateTimeOffset ReceivedAt, DateTimeOffset InstalledAt, IReadOnlyList<string> ChangedQuestionIds)>? UpdateInstalled;
    public PaperUpdatePhase Phase => _phase;
    public string StatusText => Volatile.Read(ref _statusText);
    public long PendingVersion => _manager.PendingPaperUpdates
        .Select(item => item.Delivery.ResourceVersion!.Value).DefaultIfEmpty(0).Max();

    public void Notify(CommandDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        // 调用方或 UI 传入一条形似更新的事件并不构成授权。
        if (_manager.IsRecordedPaperUpdate(delivery)) Retry();
    }

    /// <summary>网络恢复或本地保存重试后唤醒同一循环，不丢失循环空闲边界上的通知。</summary>
    public void Retry()
    {
        lock (_sync)
        {
            if (_disposed) return;
            if (_loop is null) _loop = Task.Run(() => RunAsync(_lifetime.Token));
            else if (_wake.CurrentCount == 0) _wake.Release();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TimeSpan delay;
                try { delay = await AttemptAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or
                    InvalidOperationException or UnauthorizedAccessException or CryptographicException or
                    System.Text.Json.JsonException or System.Data.Common.DbException or ArgumentException or
                    TimeoutException or OperationCanceledException)
                {
                    SetPhase(PaperUpdatePhase.Failed,
                        "试卷更新尚未完成，原答案与恢复记录保留；将重试网络或本地存储操作。");
                    delay = RetryDelay();
                }
                await _wake.WaitAsync(delay, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private bool IsTerminal => _manager.ExamEnded || _runtime.HasFixedSubmission ||
        _manager.CurrentPhase is ExamPackageManager.Phase.Revoked or ExamPackageManager.Phase.Ended or
            ExamPackageManager.Phase.Withdrawn;

    private async Task<TimeSpan> AttemptAsync(CancellationToken ct)
    {
        var pending = _manager.PendingPaperUpdates;
        // 已提交结果的补报始终沿用日志中的请求。旧结果未收到回执不阻止安装后续修订。
        foreach (var item in pending.Where(item => item.Acknowledgement.ProcessingResult == CommandProcessingResult.Applied))
            await _manager.ReportUpdateOutcomeAsync(item.Delivery.EventId, true,
                item.Acknowledgement.ActualPaperVersion!.Value, null, ct).ConfigureAwait(false);

        if (IsTerminal)
        {
            SetPhase(PaperUpdatePhase.None, string.Empty);
            return _manager.PendingPaperUpdates.Any(item =>
                item.Acknowledgement.ProcessingResult == CommandProcessingResult.Applied)
                ? TimeSpan.FromSeconds(15) : Timeout.InfiniteTimeSpan;
        }
        pending = _manager.PendingPaperUpdates;
        var awaiting = pending.Where(item => item.Acknowledgement.ProcessingResult != CommandProcessingResult.Applied).ToArray();
        var installed = _manager.Descriptor?.PaperVersion ?? 0;
        if (awaiting.Length == 0)
        {
            AnnounceReview();
            return pending.Count > 0 ? TimeSpan.FromSeconds(15) : Timeout.InfiniteTimeSpan;
        }
        var target = awaiting.Max(item => item.Delivery.ResourceVersion!.Value);
        if (_manager.CurrentPhase != ExamPackageManager.Phase.Unlocked)
        {
            SetPhase(PaperUpdatePhase.Pending, $"已收到试卷版本 {target}；等待资料完整下载并解锁，不提前确认安装成功。");
            return TimeSpan.FromSeconds(15);
        }
        if (installed < target)
        {
            SetPhase(PaperUpdatePhase.Syncing, $"正在同步试卷版本 {target}；提交完成前保留当前题包和答案。");
            var result = await _runtime.ApplyPaperUpdateAsync(ct: ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                if (IsTerminal) { SetPhase(PaperUpdatePhase.None, string.Empty); return Timeout.InfiniteTimeSpan; }
                foreach (var item in awaiting)
                    await _manager.ReportUpdateOutcomeAsync(item.Delivery.EventId, false, 0, result.Error, ct).ConfigureAwait(false);
                SetPhase(PaperUpdatePhase.Failed, $"试卷更新未完成：{result.Error} 原记录保留，将自动重试。");
                return RetryDelay();
            }
            installed = result.InstalledPaperVersion;
        }
        if (!_runtime.IsPaperVersionReady(installed))
        {
            SetPhase(PaperUpdatePhase.Pending, "题包和答案版本尚未共同就绪，不能确认更新成功；原记录保留。");
            return TimeSpan.FromSeconds(15);
        }
        var eligible = awaiting.Where(item => item.Delivery.ResourceVersion <= installed).ToArray();
        if (eligible.Length == 0) return TimeSpan.FromSeconds(15);
        var receivedAt = eligible.Min(item => item.ReceivedAt ?? item.Delivery.CreatedAt);
        if (await _runtime.EnsureInstalledReviewAsync(installed, receivedAt, ct).ConfigureAwait(false) is null)
            return TimeSpan.FromSeconds(15);
        foreach (var item in eligible)
        {
            // 每次发送前重新核对：前一回执等待期间可能发生 END、固定交卷或撤权。
            if (!_runtime.IsPaperVersionReady(installed)) break;
            await _manager.ReportUpdateOutcomeAsync(item.Delivery.EventId, true, installed, null, ct).ConfigureAwait(false);
        }
        _retryCount = 0;
        if (!IsTerminal)
        {
            AnnounceReview();
            SetPhase(PaperUpdatePhase.Applied,
                $"试卷版本 {installed} 与答案已保存，原选择和标记保留；更新回执未确认时继续补发，请复核修订试题。");
        }
        return _manager.PendingPaperUpdates.Any(item => item.Delivery.ResourceVersion > installed)
            ? TimeSpan.Zero : _manager.PendingPaperUpdates.Count > 0 ? TimeSpan.FromSeconds(15) : Timeout.InfiniteTimeSpan;
    }

    private TimeSpan RetryDelay() => TimeSpan.FromSeconds(30 * Math.Pow(2, Math.Min(_retryCount++, 2)));

    private void AnnounceReview()
    {
        if (_runtime.PendingReviewVersion is not { } version || version <= _announcedVersion ||
            !_runtime.IsPaperVersionReady(version) || _manager.LastPaperInstallation is not { } installation) return;
        _announcedVersion = version;
        Dispatch(() => UpdateInstalled?.Invoke(this,
            (version, installation.ReceivedAt, installation.InstalledAt, ChangedQuestionsFor(version))));
        SetPhase(PaperUpdatePhase.Applied, $"试卷已修订为版本 {version}；原答案和标记保留，请复核修订试题。");
    }

    /// <summary>
    /// 修订定位：PaperUpdated 指令元数据携带本次发布的变更题目标识清单；
    /// 元数据缺失或损坏（旧版服务器/指令）时返回空清单，横幅退化为普通修订提示。
    /// </summary>
    private IReadOnlyList<string> ChangedQuestionsFor(long version)
    {
        var record = _manager.FindPaperUpdateCommand(version);
        if (record?.Delivery.Metadata is not { } metadata) return [];
        if (!metadata.TryGetValue("changedQuestionIds", out var json) || string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private void SetPhase(PaperUpdatePhase phase, string message)
    {
        _phase = phase;
        Volatile.Write(ref _statusText, message);
        var args = new PaperUpdateStatusChangedEventArgs(phase, message, PendingVersion, _manager.Descriptor?.PaperVersion ?? 0);
        Dispatch(() => StatusChanged?.Invoke(this, args));
    }

    private void Dispatch(Action action)
    {
        if (_disposed) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else if (!dispatcher.HasShutdownStarted)
            dispatcher.BeginInvoke(new Action(() => { if (!_disposed) action(); }));
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _lifetime.Cancel();
                _disposeTask = DisposeCoreAsync(_loop);
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task? loop)
    {
        try { if (loop is not null) await loop.ConfigureAwait(false); }
        finally { _wake.Dispose(); _lifetime.Dispose(); }
    }
}
