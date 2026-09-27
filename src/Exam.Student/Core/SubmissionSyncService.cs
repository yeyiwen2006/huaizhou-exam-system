using System.IO;
using System.Net.Http;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;

namespace Exam.Student.Core;

public enum SubmissionSyncPhase
{
    /// <summary>当前没有可同步的内容（未开始作答或无可上报进度）。</summary>
    Idle,

    /// <summary>答题进度已成功同步，等待下一轮或新的修改。</summary>
    Progressing,

    /// <summary>正在上传固定答卷并等待服务器持久化回执。</summary>
    Uploading,

    /// <summary>上一轮同步失败，正按退避间隔自动重试；本地记录保留原请求标识。</summary>
    WaitingRetry,

    /// <summary>已取得可信持久化回执，交卷成功；此后不再产生新的网络写入。</summary>
    Confirmed,

    /// <summary>服务器已持有与本地不一致的提交记录，自动重试已停止，需考务人员核对。</summary>
    Blocked,
}

public sealed record SubmissionSyncStatusChangedEventArgs(SubmissionSyncPhase Phase);

/// <summary>
/// B05 同步与交卷服务：把 B04 的可靠本地状态（待发送快照、固定提交记录）上传到服务器，
/// 只有取得可信持久化回执才进入 Confirmed。所有重试沿用首次请求标识，服务器幂等重放
/// 保证同一提交不会被重复计次；回执丢失后通过重发原请求或查询已持有提交恢复原结果。
/// 进度上报与固定提交共用一个串行回合门，避免并发请求造成乱序覆盖。
/// </summary>
public sealed class SubmissionSyncService(
    IStudentApi api,
    StudentExamRuntime runtime,
    DeviceRecord device) : IAsyncDisposable
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProgressRetryInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UploadRetryBase = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan UploadRetryMax = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConfirmedInterval = TimeSpan.FromMinutes(5);

    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _roundGate = new(1, 1);
    private readonly object _sync = new();
    private TaskCompletionSource<bool> _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _wakeRequested;
    private bool _disposed;

    private SubmissionSyncPhase _phase = SubmissionSyncPhase.Idle;
    private string _statusText = "同步服务尚未启动。";
    private SubmissionReceipt? _confirmedReceipt;
    private long _syncedProgressVersion;
    private long _attemptedProgressVersion;
    private string? _progressRequestId;
    private int _uploadRetryCount;
    private string? _lastUploadError;

    /// <summary>触发 UI 刷新；事件封送到 UI 线程触发，订阅方无需自行处理线程。</summary>
    public event EventHandler<SubmissionSyncStatusChangedEventArgs>? StatusChanged;

    public SubmissionSyncPhase Phase => _phase;
    public string StatusText => _statusText;

    /// <summary>只有取得可信持久化回执才非空；该回执是显示交卷成功的唯一依据。</summary>
    public SubmissionReceipt? ConfirmedReceipt => _confirmedReceipt;

    /// <summary>常驻同步循环：每回合先处理固定提交上传，再处理进度上报；回合间可被唤醒提前执行。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        runtime.StateChanged += OnRuntimeStateChanged;
        try
        {
            var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            try
            {
                var token = linked.Token;
                while (!token.IsCancellationRequested)
                {
                    TimeSpan nextDelay;
                    await _roundGate.WaitAsync(token);
                    try { nextDelay = await ExecuteRoundAsync(token); }
                    finally { _roundGate.Release(); }

                    if (token.IsCancellationRequested) { break; }
                    await WaitAsync(nextDelay, token);
                }
            }
            finally { linked.Dispose(); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            runtime.StateChanged -= OnRuntimeStateChanged;
        }
    }

    /// <summary>立即执行一个同步回合（与循环共用串行门）；交卷后调用方可据此检查回执。</summary>
    public async Task KickAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            await _roundGate.WaitAsync(linked.Token);
            try { await ExecuteRoundAsync(linked.Token); }
            finally { _roundGate.Release(); }
        }
        finally { linked.Dispose(); }
    }

    private async Task<TimeSpan> ExecuteRoundAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (runtime.Answers?.FixedSubmission is { } record)
        {
            return await SyncFixedSubmissionAsync(record, ct);
        }

        if (runtime.HasFixedSubmission && runtime.Answers is { HasPendingSubmission: true })
        {
            // 固定意图尚未落盘：等待 B04 重试完成，本服务不代为固定。
            SetPhase(SubmissionSyncPhase.WaitingRetry,
                "本地固定提交尚未落盘，等待本地保存恢复后再上传。");
            return TimeSpan.FromSeconds(5);
        }

        return await SyncProgressAsync(ct);
    }

    private async Task<TimeSpan> SyncProgressAsync(CancellationToken ct)
    {
        var answers = runtime.Answers;
        if (answers is not { Status: AnswerSessionStatus.Active } ||
            runtime.Manager.Participation is not { } participation ||
            runtime.Manager.Descriptor is not { } descriptor)
        {
            SetPhase(SubmissionSyncPhase.Idle, "当前没有可上报的答题进度。");
            return IdleInterval;
        }

        var version = answers.AnswerVersion;
        if (version <= 0 || version == _syncedProgressVersion)
        {
            SetPhase(SubmissionSyncPhase.Progressing,
                $"答题进度已同步（可靠版本 {version}），每分钟自动补报。");
            return ProgressInterval;
        }

        if (version != _attemptedProgressVersion)
        {
            // 同一版本的重试沿用同一请求标识，服务器幂等重放不会产生重复进度。
            _progressRequestId = RequestIds.New();
            _attemptedProgressVersion = version;
        }

        var request = new SyncProgressRequest(
            _progressRequestId!,
            runtime.Manager.ExamId,
            runtime.Manager.CandidateId,
            participation.ParticipationId,
            participation.EntryVersion,
            device.DeviceId,
            participation.AuthorizationVersion,
            version,
            descriptor.PaperVersion,
            answers.Answers,
            answers.CurrentQuestionId);

        try
        {
            var response = await api.SyncProgressAsync(request, ct).ConfigureAwait(false);
            if (response is { IsSuccess: true, Result: { } persisted })
            {
                _syncedProgressVersion = Math.Max(_syncedProgressVersion, persisted.AnswerVersion);
                SetPhase(SubmissionSyncPhase.Progressing,
                    $"答题进度已同步至服务器（版本 {persisted.AnswerVersion}，{persisted.LastSyncedAt:HH:mm:ss}）。");
                return ProgressInterval;
            }

            var error = response?.Error;
            if (error is not null && error.Code is ErrorCodes.AuthorizationRevoked)
            {
                SetPhase(SubmissionSyncPhase.Blocked, "答题授权已被服务器撤销，进度上报已停止。");
                return UploadRetryMax;
            }

            SetPhase(SubmissionSyncPhase.WaitingRetry,
                $"进度暂未同步（{error?.Message ?? "服务器暂不可用"}），稍后自动重试。");
            return ProgressRetryInterval;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException
            or TimeoutException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested || _lifetime.IsCancellationRequested)
            {
                throw;
            }

            SetPhase(SubmissionSyncPhase.WaitingRetry, "进度暂未同步（网络尚未恢复），稍后自动重试。");
            return ProgressRetryInterval;
        }
    }

    private async Task<TimeSpan> SyncFixedSubmissionAsync(FixedSubmissionRecord record, CancellationToken ct)
    {
        if (_confirmedReceipt is { } existing)
        {
            SetPhase(SubmissionSyncPhase.Confirmed,
                $"交卷成功：服务器回执 {existing.ReceiptId} 已确认。");
            return ConfirmedInterval;
        }

        if (runtime.Manager.Participation is not { } participation)
        {
            SetPhase(SubmissionSyncPhase.WaitingRetry, "参试登记尚未就绪，答卷已冻结，等待恢复后上传。");
            return TimeSpan.FromSeconds(10);
        }

        var participationId = record.ParticipationId ?? participation.ParticipationId;
        var entryVersion = record.EntryVersion > 0 ? record.EntryVersion : participation.EntryVersion;
        var authorizationVersion = record.AuthorizationVersion > 0
            ? record.AuthorizationVersion
            : participation.AuthorizationVersion;
        SetPhase(SubmissionSyncPhase.Uploading, "正在上传固定答卷并等待服务器持久化回执……");

        var request = new SubmitFinalAnswersRequest(
            record.RequestId ?? RequestIds.New(),
            record.SubmissionId,
            runtime.Manager.ExamId,
            runtime.Manager.CandidateId,
            participationId,
            entryVersion,
            device.DeviceId,
            authorizationVersion,
            // 试卷版本以固定记录自身为准，避免上传后版本切换造成不一致。
            record.PaperVersion,
            record.FrozenAnswers);

        try
        {
            var response = await api.SubmitFinalAnswersAsync(request, ct).ConfigureAwait(false);
            if (response is { IsSuccess: true, Result: { } final } && final.Receipt is { } receipt)
            {
                return AcceptReceipt(receipt, record);
            }

            var error = response?.Error;
            if (error is not null &&
                error.Code is ErrorCodes.AlreadySubmitted or ErrorCodes.VersionConflict or ErrorCodes.NotFound)
            {
                // 服务器已持有该参试的提交（可能回执在网络中丢失）：查询原结果恢复回执。
                return await RecoverReceiptAsync(record, participationId, entryVersion, authorizationVersion, ct);
            }

            if (error is not null && error.Code is ErrorCodes.AuthorizationRevoked)
            {
                SetPhase(SubmissionSyncPhase.Blocked,
                    "答题授权已被服务器撤销，答卷保留在本地，请联系考务人员核对。");
                return UploadRetryMax;
            }

            _lastUploadError = error?.Message ?? "服务器暂不可用";
            return ScheduleUploadRetry();
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException
            or TimeoutException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested || _lifetime.IsCancellationRequested)
            {
                throw;
            }

            // 响应可能在传输中丢失，但服务器可能已持久化该提交；下一轮沿用原请求标识重发即可恢复。
            _lastUploadError = "网络尚未恢复";
            return ScheduleUploadRetry();
        }
    }

    private async Task<TimeSpan> RecoverReceiptAsync(
        FixedSubmissionRecord record, string participationId, long entryVersion, long authorizationVersion,
        CancellationToken ct)
    {
        SetPhase(SubmissionSyncPhase.Uploading, "正在核对服务器已持有的提交记录……");
        try
        {
            var response = await api.GetFinalSubmissionAsync(
                participationId, entryVersion, device.DeviceId, authorizationVersion, ct).ConfigureAwait(false);

            if (response is { IsSuccess: true, Result: { } found } && found.Receipt is { } receipt)
            {
                return AcceptReceipt(receipt, record);
            }

            SetPhase(SubmissionSyncPhase.Blocked,
                "服务器已持有不同的提交记录，自动重试已停止；本地答卷已保留，请联系考务人员核对。");
            return UploadRetryMax;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException
            or TimeoutException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested || _lifetime.IsCancellationRequested)
            {
                throw;
            }

            return ScheduleUploadRetry();
        }
    }

    private TimeSpan AcceptReceipt(SubmissionReceipt receipt, FixedSubmissionRecord record)
    {
        var participationId = record.ParticipationId;
        if (!string.Equals(receipt.SubmissionId, record.SubmissionId, StringComparison.Ordinal) ||
            receipt.PaperVersion != record.PaperVersion ||
            (participationId is not null &&
             !string.Equals(receipt.ParticipationId, participationId, StringComparison.Ordinal)))
        {
            SetPhase(SubmissionSyncPhase.Blocked,
                $"服务器回执与本地固定提交不一致（提交 {receipt.SubmissionId} / 试卷版本 {receipt.PaperVersion}），" +
                "已停止自动重试，请联系考务人员核对。");
            return UploadRetryMax;
        }

        _confirmedReceipt = receipt;
        _uploadRetryCount = 0;
        SetPhase(SubmissionSyncPhase.Confirmed, $"交卷成功：服务器回执 {receipt.ReceiptId} 已确认。");
        return ConfirmedInterval;
    }

    private TimeSpan ScheduleUploadRetry()
    {
        var delay = TimeSpan.FromSeconds(Math.Min(
            UploadRetryBase.TotalSeconds * Math.Pow(2, _uploadRetryCount),
            UploadRetryMax.TotalSeconds));
        _uploadRetryCount++;
        SetPhase(SubmissionSyncPhase.WaitingRetry,
            $"答卷已冻结并保留，尚未取得服务器回执（{_lastUploadError ?? "重试中"}）；" +
            $"约 {Math.Ceiling(delay.TotalSeconds)} 秒后自动重试。");
        return delay;
    }

    private void OnRuntimeStateChanged(object? sender, EventArgs e)
    {
        // 本地新修改或固定提交完成时尽快唤醒同步循环（即时状态上报）。
        RequestWake();
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

    private void SetPhase(SubmissionSyncPhase phase, string text)
    {
        _phase = phase;
        _statusText = text;
        RaiseStatusChanged(phase);
    }

    private void RaiseStatusChanged(SubmissionSyncPhase phase)
    {
        var handler = StatusChanged;
        if (handler is null)
        {
            return;
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            handler(this, new SubmissionSyncStatusChangedEventArgs(phase));
        }
        else if (!dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_disposed)
                {
                    handler(this, new SubmissionSyncStatusChangedEventArgs(phase));
                }
            }));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        runtime.StateChanged -= OnRuntimeStateChanged;
        _lifetime.Cancel();
        lock (_sync) { _wake.TrySetResult(false); }

        // 等待进行中的回合退出后再释放同步原语；任何竞态都不应阻断释放。
        try
        {
            await _roundGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try { _roundGate.Release(); }
            catch (Exception releaseError) when (releaseError is ObjectDisposedException or SemaphoreFullException) { }
        }
        catch (Exception waitError) when (waitError is ObjectDisposedException or OperationCanceledException)
        {
        }

        _roundGate.Dispose();
        _lifetime.Dispose();
    }
}
