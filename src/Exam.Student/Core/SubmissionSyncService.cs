using System.IO;
using System.Net.Http;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;

namespace Exam.Student.Core;

public enum SubmissionSyncPhase
{
    Idle, Progressing, Uploading, WaitingRetry, Confirmed, Blocked,
}

public sealed record SubmissionSyncStatusChangedEventArgs(SubmissionSyncPhase Phase);

/// <summary>
/// 上传已在本地落盘的完整快照，重试及进程恢复始终使用其原请求标识。
/// 网络写入串行；Confirmed/Blocked 为终态。只有匹配且已在本机保留的服务器回执才能确认成功。
/// </summary>
public sealed class SubmissionSyncService(
    IStudentApi api, StudentExamRuntime runtime, DeviceRecord device, TimeProvider? timeProvider = null,
    ServerClock? clock = null, FocusTracker? focus = null) : IAsyncDisposable
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(20);
    private static readonly System.Text.Json.JsonSerializerOptions UploadJson = JsonDefaults.Create();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _roundGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _sync = new();
    private TaskCompletionSource _idle = CompletedSource();
    private Task? _disposeTask;
    private int _operations;
    private bool _runStarted;
    private bool _disposed;
    private SubmissionSyncPhase _phase;
    private string _statusText = "同步服务尚未启动。";
    private SubmissionReceipt? _confirmedReceipt;
    private SubmissionReceipt? _receiptToSave;
    private string? _syncedRequestId;
    private long _syncedAt;
    private int _uploadRetryCount;
    private long _observedFocusRevision = -1;
    private (long Version, AnswerSessionStatus Status, bool Fixed, bool Ended, bool Revoked) _observedState;

    public event EventHandler<SubmissionSyncStatusChangedEventArgs>? StatusChanged;
    public SubmissionSyncPhase Phase => _phase;
    public string StatusText => _statusText;
    public SubmissionReceipt? ConfirmedReceipt => _confirmedReceipt;

    public Task RunAsync(CancellationToken ct)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runStarted) throw new InvalidOperationException("同步循环不能重复启动。");
            _runStarted = true;
            EnterOperation();
            return RunCoreAsync(ct);
        }
    }

    private async Task RunCoreAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var clockTask = SynchronizeClockAsync(linked.Token);
        runtime.SynchronizationStateChanged += OnRuntimeStateChanged;
        runtime.SubmissionRetryRequested += OnSubmissionRetryRequested;
        try
        {
            while (!linked.IsCancellationRequested)
            {
                var delay = await RoundAsync(linked.Token).ConfigureAwait(false);
                await _wake.WaitAsync(delay, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        finally
        {
            runtime.SynchronizationStateChanged -= OnRuntimeStateChanged;
            runtime.SubmissionRetryRequested -= OnSubmissionRetryRequested;
            linked.Cancel();
            try { await clockTask.ConfigureAwait(false); }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
            finally { ExitOperation(); }
        }
    }

    private async Task SynchronizeClockAsync(CancellationToken ct)
    {
        if (clock is null) return;
        using var timer = new PeriodicTimer(ProgressInterval);
        do
        {
            try { await clock.SynchronizeAsync(api, ct).ConfigureAwait(false); }
            catch (Exception error) when (!ct.IsCancellationRequested && error is
                HttpRequestException or InvalidOperationException or TimeoutException or OperationCanceledException)
            {
                // 校时失败不产生开考/结束指令，也不能阻塞独立的交卷写入回合。
            }
        } while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    /// <summary>显式请求一个立即回合；自动循环仍按返回间隔退避。</summary>
    public Task KickAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnterOperation();
            return KickCoreAsync(ct);
        }
    }

    private async Task KickCoreAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try { await RoundAsync(linked.Token).ConfigureAwait(false); }
        finally { ExitOperation(); }
    }

    private async Task<TimeSpan> RoundAsync(CancellationToken ct)
    {
        await _roundGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            // 补报：失焦事件与复核确认先于进度/交卷处理；已确认回执后仍继续清空发件箱。
            var uploadsPending = await FlushUploadsAsync(ct).ConfigureAwait(false);
            if (_phase is SubmissionSyncPhase.Confirmed or SubmissionSyncPhase.Blocked)
                return uploadsPending ? RetryInterval : Timeout.InfiniteTimeSpan;
            // 固定答卷不恢复编辑。保留原载荷，由服务器在同一事务中核对连续保留本设备的裁决。
            if (runtime.FixedSubmission is { } fixedRecord)
                return await SyncFixedAsync(fixedRecord, ct).ConfigureAwait(false);
            if (runtime.Manager.CurrentPhase == ExamPackageManager.Phase.Revoked ||
                runtime.Answers?.Status == AnswerSessionStatus.PermissionDenied)
                return await BlockAsync("当前设备或本地记录没有有效授权，已停止自动上传，请联系考务人员核对。");

            if (runtime.Answers?.HasPendingSubmission == true)
            {
                await runtime.RetryLocalSaveAsync(ct).ConfigureAwait(false);
                if (runtime.Answers.HasPendingSubmission)
                {
                    SetPhase(SubmissionSyncPhase.WaitingRetry, "答卷已经冻结，固定内容尚未落盘；保留原标识并重试本地保存。");
                    return TimeSpan.FromSeconds(5);
                }
            }
            if (runtime.FixedSubmission is { } record)
                return await SyncFixedAsync(record, ct).ConfigureAwait(false);
            return await SyncProgressAsync(ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or
            TimeoutException or OperationCanceledException or UnauthorizedAccessException or System.Data.Common.DbException or
            System.Text.Json.JsonException)
        {
            ct.ThrowIfCancellationRequested();
            return ScheduleRetry("网络或本地存储尚未恢复，原记录已保留");
        }
        finally { _roundGate.Release(); }
    }

    /// <summary>
    /// 发件箱补报：失焦事件批量上报（按事件幂等），复核确认逐版本上报（按版本幂等）。
    /// 明确的不可重试拒绝视为服务端终态：删除记录不再重发；临时失败保留待下轮。
    /// 返回是否仍有未确认记录。
    /// </summary>
    private async Task<bool> FlushUploadsAsync(CancellationToken ct)
    {
        var uploads = await runtime.LoadPendingUploadsAsync(200, ct).ConfigureAwait(false);
        if (uploads.Count == 0) return false;

        var examId = runtime.Manager.ExamId;
        var candidateId = runtime.Manager.CandidateId;
        var participation = runtime.Manager.Participation;
        var paperVersion = runtime.Manager.Descriptor?.PaperVersion ?? 0;

        var focusPayloads = new List<StudentExamRuntime.PendingFocusEventPayload>();
        var focusIds = new List<string>();
        foreach (var item in uploads)
        {
            if (!string.Equals(item.Kind, "focus", StringComparison.Ordinal)) continue;
            try
            {
                var payload = System.Text.Json.JsonSerializer.Deserialize<StudentExamRuntime.PendingFocusEventPayload>(
                    item.Payload, UploadJson);
                if (payload is null || string.IsNullOrWhiteSpace(payload.EventId) ||
                    (payload.EventId != item.ItemId && item.ItemId != $"{payload.EventId}:{payload.DurationMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}") ||
                    payload.DurationMilliseconds < 0 ||
                    payload.RegainedAt < payload.LostAt)
                    throw new System.Text.Json.JsonException("载荷不完整。");
                focusPayloads.Add(payload);
                focusIds.Add(item.ItemId);
            }
            catch (System.Text.Json.JsonException)
            {
                // 损坏载荷无法被服务器接受，删除以免永久阻塞其余补报。
                await DeleteUploadQuietlyAsync(item.ItemId, ct).ConfigureAwait(false);
            }
        }

        if (focusPayloads.Count > 0 && participation is { } current &&
            !string.IsNullOrWhiteSpace(examId) && !string.IsNullOrWhiteSpace(candidateId) && paperVersion > 0)
        {
            var request = new ReportFocusEventsRequest(RequestIds.New(), examId, candidateId,
                current.ParticipationId, current.EntryVersion, device.DeviceId, current.AuthorizationVersion,
                paperVersion, focusPayloads.GroupBy(payload => payload.EventId, StringComparer.Ordinal)
                    .Select(group => group.MaxBy(payload => payload.DurationMilliseconds)!)
                    .Select(payload => new ReportedFocusLossEvent(
                    payload.EventId, payload.LostAt, payload.RegainedAt, payload.DurationMilliseconds)).ToArray());
            var response = await api.ReportFocusEventsAsync(request, ct).ConfigureAwait(false);
            var receipt = response?.Result;
            var accepted = response is { IsSuccess: true } && receipt is not null &&
                receipt.ExamId == examId && receipt.CandidateId == candidateId &&
                receipt.ParticipationId == current.ParticipationId && receipt.PaperVersion == paperVersion &&
                receipt.AcceptedEventIds is not null &&
                new HashSet<string>(receipt.AcceptedEventIds, StringComparer.Ordinal)
                    .SetEquals(request.Events.Select(item => item.EventId));
            if (accepted || IsDefinitiveRejection(response?.Error))
            {
                foreach (var id in focusIds)
                    await DeleteUploadQuietlyAsync(id, ct).ConfigureAwait(false);
            }
        }
        else if (focusPayloads.Count > 0)
        {
            return true; // 授权元组尚未就绪，保留待报。
        }

        foreach (var item in uploads)
        {
            if (!string.Equals(item.Kind, "review", StringComparison.Ordinal)) continue;
            StudentExamRuntime.ReviewConfirmationPayload? payload;
            try
            {
                payload = System.Text.Json.JsonSerializer.Deserialize<StudentExamRuntime.ReviewConfirmationPayload>(
                    item.Payload, UploadJson);
                if (payload is null || payload.PaperVersion < 1 || string.IsNullOrWhiteSpace(payload.RequestId))
                    throw new System.Text.Json.JsonException("载荷不完整。");
            }
            catch (System.Text.Json.JsonException)
            {
                await DeleteUploadQuietlyAsync(item.ItemId, ct).ConfigureAwait(false);
                continue;
            }
            if (participation is not { } reviewParticipation || string.IsNullOrWhiteSpace(examId)) return true;
            var request = new Exam.Contracts.Versioning.ConfirmPaperReviewRequest(
                examId, reviewParticipation.ParticipationId, payload.PaperVersion, payload.RequestId,
                device.DeviceId, reviewParticipation.EntryVersion, reviewParticipation.AuthorizationVersion);
            var response = await api.ConfirmPaperReviewAsync(
                examId, reviewParticipation.ParticipationId, request, ct).ConfigureAwait(false);
            if (response is { IsSuccess: true } || IsDefinitiveRejection(response?.Error))
                await DeleteUploadQuietlyAsync(item.ItemId, ct).ConfigureAwait(false);
            else
                return true;
        }

        return await runtime.LoadPendingUploadsAsync(1, ct).ConfigureAwait(false) is { Count: > 0 };
    }

    private static bool IsDefinitiveRejection(ApiError? error) =>
        error is { Retryable: false } && error.Code != ErrorCodes.ServiceRecovering;

    private async Task DeleteUploadQuietlyAsync(string itemId, CancellationToken ct)
    {
        try { await runtime.DeletePendingUploadAsync(itemId, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { /* 删除失败时下轮幂等重试；服务器按标识去重 */ }
    }

    private async Task<TimeSpan> SyncProgressAsync(CancellationToken ct)
    {
        var answers = runtime.Answers;
        if (runtime.IsPaperUpdateInProgress) return TimeSpan.FromSeconds(1);
        if (answers?.Status != AnswerSessionStatus.Active || runtime.Manager.ExamEnded ||
            runtime.Manager.CurrentPhase != ExamPackageManager.Phase.Unlocked)
        {
            SetPhase(SubmissionSyncPhase.Idle, "当前没有可上报的答题进度。");
            return RetryInterval;
        }
        // 同一 SQLite 事务验证身份、版本、答案和导航；不用多个可能跨版本的属性读拼装载荷。
        var snapshot = await runtime.LoadPendingSnapshotAsync(ct).ConfigureAwait(false);
        if (answers.Status != AnswerSessionStatus.Active || runtime.HasFixedSubmission || runtime.Manager.ExamEnded)
            return TimeSpan.Zero;
        if (runtime.IsPaperUpdateInProgress ||
            (snapshot is not null && snapshot.PaperVersion != answers.PaperVersion))
            return TimeSpan.FromSeconds(1); // 读取快照期间恰好提交新版，重读而不是永久封锁同步。
        var participation = runtime.Manager.Participation;
        if (snapshot is null || participation is null || string.IsNullOrWhiteSpace(snapshot.RequestId) ||
            snapshot.ExamId != runtime.Manager.ExamId || snapshot.CandidateId != runtime.Manager.CandidateId ||
            snapshot.ParticipationId != participation.ParticipationId || snapshot.EntryVersion != participation.EntryVersion ||
            snapshot.DeviceId != device.DeviceId || snapshot.AuthorizationVersion != participation.AuthorizationVersion ||
            snapshot.PaperVersion != runtime.Manager.Descriptor?.PaperVersion)
            return await BlockAsync("可靠快照与当前参加考试、设备或版本不一致，原记录已保留，请核对后恢复。");

        // 复焦只触发原答案快照的幂等核对，不伪造同版本新写入，也不宣称已保存失焦事件。
        var focusRevision = focus?.Revision ?? 0;
        var focusChanged = focus is not null && focusRevision != _observedFocusRevision;
        if (_syncedRequestId == snapshot.RequestId && !focusChanged)
        {
            var remaining = ProgressInterval - _time.GetElapsedTime(_syncedAt);
            if (remaining > TimeSpan.Zero) return remaining;
        }
        var request = new SyncProgressRequest(snapshot.RequestId, snapshot.ExamId, snapshot.CandidateId,
            snapshot.ParticipationId!, snapshot.EntryVersion, snapshot.DeviceId!, snapshot.AuthorizationVersion,
            snapshot.AnswerVersion, snapshot.PaperVersion, snapshot.Answers, snapshot.CurrentQuestionId);
        var response = await api.SyncProgressAsync(request, ct).ConfigureAwait(false);
        if (response is { IsSuccess: true, Result: { } saved })
        {
            if (saved.ExamId != request.ExamId || saved.CandidateId != request.CandidateId ||
                saved.ParticipationId != request.ParticipationId || saved.EntryVersion != request.EntryVersion ||
                saved.DeviceId != request.DeviceId || saved.AuthorizationVersion != request.AuthorizationVersion ||
                saved.PaperVersion != request.PaperVersion || saved.AnswerVersion != request.AnswerVersion ||
                saved.CurrentQuestionId != request.CurrentQuestionId || saved.LastSyncedAt == default ||
                saved.Answers is null || !SameAnswers(request.Answers, saved.Answers))
                return await BlockAsync("服务器返回的进度与本次可靠快照不一致，已停止自动写入并保留原记录。");
            _syncedRequestId = snapshot.RequestId;
            _syncedAt = _time.GetTimestamp();
            _observedFocusRevision = focusRevision; // 不把等待回执期间的新失焦算作已处理。
            SetPhase(SubmissionSyncPhase.Progressing,
                $"答题进度已由服务器确认（版本 {saved.AnswerVersion}）；每 60 秒重报原快照，新修改及时上报。");
            return ProgressInterval;
        }
        return await HandleErrorAsync(response?.Error, "进度尚未同步").ConfigureAwait(false);
    }

    private async Task<TimeSpan> SyncFixedAsync(FixedSubmissionRecord record, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(record.RequestId) || string.IsNullOrWhiteSpace(record.SubmissionId) ||
            string.IsNullOrWhiteSpace(record.ParticipationId) || record.EntryVersion < 1 || record.AuthorizationVersion < 1 ||
            record.PaperVersion < 1 || record.DeviceId != device.DeviceId)
            return await BlockAsync("原固定答卷的身份或请求标识不完整，不能生成替代身份上传，请联系考务人员。");
        if (_receiptToSave is { } pendingReceipt)
            return await AcceptReceiptAsync(pendingReceipt, record, ct).ConfigureAwait(false);

        SetPhase(SubmissionSyncPhase.Uploading, "正在上传原固定答卷并等待服务器持久化回执……");
        var request = new SubmitFinalAnswersRequest(record.RequestId, record.SubmissionId,
            runtime.Manager.ExamId, runtime.Manager.CandidateId, record.ParticipationId, record.EntryVersion,
            record.DeviceId, record.AuthorizationVersion, record.PaperVersion, record.FrozenAnswers);
        var response = await api.SubmitFinalAnswersAsync(request, ct).ConfigureAwait(false);
        if (response is { IsSuccess: true, Result: { } receipt })
            return await AcceptReceiptAsync(receipt, record, ct).ConfigureAwait(false);
        if (response?.Error?.Code is ErrorCodes.AlreadySubmitted or ErrorCodes.VersionConflict or ErrorCodes.NotFound)
        {
            SetPhase(SubmissionSyncPhase.Uploading, "正在查询并核对服务器原回执……");
            var found = await api.GetSubmissionReceiptAsync(record.ParticipationId, record.EntryVersion,
                record.DeviceId, record.AuthorizationVersion, ct).ConfigureAwait(false);
            if (found is { IsSuccess: true, Result: { } original })
                return await AcceptReceiptAsync(original, record, ct).ConfigureAwait(false);
            // 临时查询失败不等于发现不同答卷，必须保留原请求继续退避。
            return await HandleErrorAsync(found?.Error, "尚未取得可核对的原回执").ConfigureAwait(false);
        }
        return await HandleErrorAsync(response?.Error, "尚未取得交卷回执").ConfigureAwait(false);
    }

    private async Task<TimeSpan> AcceptReceiptAsync(SubmissionReceipt receipt, FixedSubmissionRecord record, CancellationToken ct)
    {
        if (!SubmissionReceiptValidation.Matches(receipt, record, runtime.Manager.ExamId, runtime.Manager.CandidateId))
            return await BlockAsync("服务器回执与原考试、考生、参加考试、提交或试卷版本不一致，已停止自动重试；原答卷保留，请人工核对。");
        _receiptToSave = receipt;
        var saved = await runtime.SaveSubmissionReceiptAsync(receipt, ct).ConfigureAwait(false);
        if (!saved.Succeeded) return ScheduleRetry("服务器已收卷，但原回执尚未可靠保存在本机；正在重试保存");
        _confirmedReceipt = receipt;
        _receiptToSave = null;
        SetPhase(SubmissionSyncPhase.Confirmed, $"交卷成功：服务器回执 {receipt.ReceiptId} 已确认并保留。");
        return Timeout.InfiniteTimeSpan;
    }

    private async Task<TimeSpan> HandleErrorAsync(ApiError? error, string context)
    {
        if (error?.Code == ErrorCodes.AuthorizationRevoked)
        {
            await runtime.RevokeSubmissionAuthorizationAsync().ConfigureAwait(false);
            return await BlockAsync("服务器已撤销当前设备授权，已停止作答和自动上传；原答卷保留，请联系考务人员。");
        }
        if (error is not null && !error.Retryable && error.Code != ErrorCodes.ServiceRecovering)
            return await BlockAsync($"{context}（{error.Message}）；自动写入已停止，原记录保留，请联系考务人员。");
        return ScheduleRetry($"{context}（{error?.Message ?? "服务器暂不可用"}）");
    }

    private async Task<TimeSpan> BlockAsync(string message)
    {
        if (runtime.Answers is { } answers) await answers.SuspendAsync(message).ConfigureAwait(false);
        SetPhase(SubmissionSyncPhase.Blocked, message);
        return Timeout.InfiniteTimeSpan;
    }

    private TimeSpan ScheduleRetry(string message)
    {
        var delay = runtime.HasFixedSubmission
            ? TimeSpan.FromSeconds(Math.Min(3 * Math.Pow(2, _uploadRetryCount), 60)) : RetryInterval;
        if (runtime.HasFixedSubmission) _uploadRetryCount = Math.Min(_uploadRetryCount + 1, 5);
        SetPhase(SubmissionSyncPhase.WaitingRetry, $"{message}；约 {Math.Ceiling(delay.TotalSeconds)} 秒后重试。");
        return delay;
    }

    private void OnRuntimeStateChanged(object? sender, EventArgs e)
    {
        lock (_sync)
        {
            var state = (runtime.Answers?.AnswerVersion ?? 0, runtime.Answers?.Status ?? AnswerSessionStatus.NotStarted,
                runtime.HasFixedSubmission, runtime.Manager.ExamEnded,
                runtime.Manager.CurrentPhase == ExamPackageManager.Phase.Revoked);
            if (_disposed || state == _observedState) return;
            _observedState = state;
            if (_wake.CurrentCount == 0) _wake.Release();
        }
    }

    private void OnSubmissionRetryRequested(object? sender, EventArgs e)
    {
        lock (_sync)
        {
            if (!_disposed && _wake.CurrentCount == 0) _wake.Release();
        }
    }

    private void SetPhase(SubmissionSyncPhase phase, string text)
    {
        _phase = phase;
        _statusText = text;
        // This is a worker notification. UI subscribers dispatch through their own view,
        // rather than an unrelated or stopped Application.Current dispatcher.
        if (!_disposed) StatusChanged?.Invoke(this, new(phase));
    }

    private static bool SameAnswers(IReadOnlyDictionary<string, AnswerValue> expected, IReadOnlyDictionary<string, AnswerValue> actual) =>
        expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var value) &&
            value is not null && value.Kind == pair.Value.Kind && value.OptionIds is not null &&
            pair.Value.OptionIds.SequenceEqual(value.OptionIds, StringComparer.Ordinal));

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private void EnterOperation()
    {
        if (_operations++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void ExitOperation()
    {
        lock (_sync) { if (--_operations == 0) _idle.TrySetResult(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _lifetime.Cancel();
                _disposeTask = DisposeCoreAsync(_idle.Task);
            }
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task idle)
    {
        // 等所有循环和已接纳的 Kick（包括正在排队者）退出后才释放同步原语。
        await idle.ConfigureAwait(false);
        _roundGate.Dispose();
        _wake.Dispose();
        _lifetime.Dispose();
    }
}
