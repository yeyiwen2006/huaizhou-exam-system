using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;
using Exam.Contracts.Resources;

namespace Exam.Student.Core;

/// <summary>一场考试的包与指令连接，在等待页转到答题页时继续保留。</summary>
public sealed partial class StudentExamRuntime(
    ExamPackageManager manager,
    DeviceRecord device,
    ILocalStateStore? localState = null,
    string? candidateId = null,
    Func<DeviceRecord, ServerNodeEnvelope, IExamCommandFeed>? commandFeedFactory = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _answerGate = new(1, 1);
    private readonly LocalStateKey? _localKey = localState is null ? null : new(
        device.ServerId ?? throw new ArgumentException("本地答题记录必须绑定可信逻辑服务器。", nameof(device)),
        manager.ExamId,
        !string.IsNullOrWhiteSpace(candidateId) && candidateId == manager.CandidateId ? candidateId :
            throw new ArgumentException("本地答题记录必须使用当前考试的考生标识。", nameof(candidateId)));
    private IExamCommandFeed? _feed;
    private readonly SemaphoreSlim _notificationGate = new(1, 1);
    private Task? _transportRecovery;
    private ParticipationResult? _recoveryParticipation;
    private volatile bool _authorizationLost;
    private ParticipationResult? _connectedParticipation;
    private ServerNodeEnvelope? _connectedServer;
    private bool _disposed;
    private string? _handlingError;
    private AnswerSessionContext? _answerContext;
    private FixedSubmissionRecord? _recoveredSubmission;
    private FixedSubmissionRecord? _endedRecoveryIntent;
    private bool _localRecoveryChecked;
    private string? _localStateError;
    private StudentPaperManifest? _paper;
    private PaperUpdateInstallation? _lastInstalledUpdate;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _confirmedReviewVersions = new();
    private readonly object _updateSync = new();
    private int _paperUpdateCommitting;
    private PaperUpdateCoordinator? _paperUpdate;

    public ExamPackageManager Manager { get; } = manager;
    public AnswerSession? Answers { get; private set; }
    /// <summary>在线更新编排器；懒创建，与本运行时同生命周期，随 DisposeAsync 一并释放。</summary>
    public PaperUpdateCoordinator PaperUpdate
    {
        get
        {
            lock (_updateSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _paperUpdate ??= new PaperUpdateCoordinator(Manager, this);
            }
        }
    }
    public StudentPaperManifest? Paper => !_disposed && !Manager.ExamEnded && !HasFixedSubmission &&
        Manager.CurrentPhase == ExamPackageManager.Phase.Unlocked &&
        _paper?.PaperVersion == Manager.Descriptor?.PaperVersion &&
        (Answers is null || Answers.PaperVersion == _paper?.PaperVersion) ? _paper : null;

    /// <summary>显示层读取 PDF 的入口。调用方负责释放显示副本；终止后禁止再次读取。</summary>
    public byte[] ReadQuestionPdf(string questionId)
    {
        var paper = Paper ?? throw new InvalidOperationException("试题尚未解锁或已经停止访问。");
        var payload = Manager.UnlockedPayload ?? throw new InvalidOperationException("试题已停止访问。");
        var bytes = StudentPaperPackage.ReadQuestionPdf(payload, paper, questionId);
        if (Paper != paper || !ReferenceEquals(payload, Manager.UnlockedPayload))
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidOperationException("读取期间试题已停止访问。");
        }
        return bytes;
    }
    /// <summary>仅供原答卷补传；不要求重新解锁或创建可编辑会话。</summary>
    public FixedSubmissionRecord? FixedSubmission => (_recoveredSubmission ?? Answers?.FixedSubmission) is { } record
        ? record with { FrozenAnswers = PersistedProgress.FreezeAnswers(record.FrozenAnswers) } : null;

    public Task<PendingSnapshot?> LoadPendingSnapshotAsync(CancellationToken ct = default) =>
        localState is not null && _localKey is not null
            ? localState.LoadPendingSnapshotAsync(_localKey, ct) : Task.FromResult<PendingSnapshot?>(null);

    public Task<StoreWriteResult> SaveSubmissionReceiptAsync(SubmissionReceipt receipt, CancellationToken ct = default) =>
        localState is ISubmissionReceiptStore receiptStore && _localKey is not null
            ? receiptStore.SaveSubmissionReceiptAsync(_localKey, receipt, ct)
            : Task.FromResult(StoreWriteResult.Fail("本地存储尚未提供可靠回执保存，原答卷继续保留。"));

    public Task RevokeSubmissionAuthorizationAsync()
    {
        _authorizationLost = true;
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.CheckAccess()
            ? InvalidateAuthorizationOnUiAsync()
            : dispatcher.InvokeAsync(InvalidateAuthorizationOnUiAsync, DispatcherPriority.Send).Task.Unwrap();
    }

    public bool LocalRecoveryChecked => _localRecoveryChecked;
    public bool HasFixedSubmission => _recoveredSubmission is not null || Answers?.Status == AnswerSessionStatus.Fixed ||
        Answers?.HasPendingSubmission == true;
    public string? LocalStateError => _localStateError ?? Answers?.LastError;
    public bool CanRetryLocalSave => localState is not null && Answers?.Status != AnswerSessionStatus.PermissionDenied &&
        (!_localRecoveryChecked || Answers is { HasUnsavedChanges: true } ||
         Answers is null && _localStateError is not null && !Manager.ExamEnded &&
            Manager.CurrentPhase == ExamPackageManager.Phase.Unlocked ||
         Answers?.Status is AnswerSessionStatus.WriteBlocked or AnswerSessionStatus.FixPending);
    public string LocalStateSummary => Answers?.HasPendingSubmission == true
        ? "答卷已经冻结，但固定内容尚未确认落盘；原答案和提交标识在内存中保留，请重试本地保存。"
        : HasFixedSubmission
        ? "本机已有固定答卷，原答案和提交标识已保留；尚不能据此视为服务器交卷成功。"
        : Answers?.Status switch
        {
            AnswerSessionStatus.Active => $"本地保存已就绪，可靠版本 {Answers.AnswerVersion}；已保存 {Answers.Answers.Count} 题记录、{Answers.Marks.Count} 个私人标记。",
            AnswerSessionStatus.WriteBlocked => "本地保存失败，已阻止新增修改。最后可靠状态和内存待保存内容均保留，请恢复写入后重试。",
            AnswerSessionStatus.Suspended => "答题已停止，原答案和私人标记保留；尚未取得服务器交卷成功回执。",
            AnswerSessionStatus.PermissionDenied => "本地记录或当前授权不允许继续作答，原文件已保留，请联系考务人员。",
            _ when _localRecoveryChecked => "本地恢复检查已完成，等待本次服务器授权和解锁后恢复答案。",
            _ => "本地答题恢复尚未完成，不能开始修改答案。"
        };
    public string? ConnectionError => (_feed as SignalRExamCommandFeed)?.LastError ?? _handlingError;
    public bool NotificationsStarted => _feed is not null;
    public event EventHandler? StateChanged;
    public event EventHandler? SubmissionRetryRequested;

    /// <summary>Read existing fixed work before any new entry/unlock; this never authorizes editing.</summary>
    public async Task<bool> InitializeLocalRecoveryAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _answerGate.WaitAsync(ct);
        try { return await InitializeLocalRecoveryCoreAsync(ct); }
        finally { _answerGate.Release(); NotifyStateChanged(); }
    }

    private async Task<bool> InitializeLocalRecoveryCoreAsync(CancellationToken ct)
    {
        if (_localRecoveryChecked) { return true; }
        if (localState is null || _localKey is null)
        {
            _localStateError = "本地答题存储尚未配置，不能开始作答。";
            return false;
        }
        try
        {
            if (Manager.HasPendingPaperInstallation)
            {
                var recoveredAnswer = await localState.LoadAnswerStateAsync(_localKey, ct);
                var recoveredSnapshot = await localState.LoadPendingSnapshotAsync(_localKey, ct);
                await Manager.RecoverPendingPaperInstallationAsync(_localKey, recoveredAnswer, recoveredSnapshot, ct);
            }
            _lastInstalledUpdate = Manager.LastPaperInstallation;
            foreach (var review in await localState.LoadPaperUpdateReviewsAsync(_localKey, ct))
            {
                _confirmedReviewVersions.TryAdd(review.PaperVersion, 0);
                // 服务端闭环：重启后把已确认但尚未上报的版本补回发件箱（按版本幂等）。
                var enqueued = await localState.EnqueueUploadAsync(_localKey, new PendingUploadRecord(
                    $"review:{review.PaperVersion}", "review",
                    System.Text.Json.JsonSerializer.Serialize(
                        new ReviewConfirmationPayload(review.PaperVersion, RequestIds.New()), UploadJson)), ct);
                if (!enqueued.Succeeded)
                    _localStateError = $"复核上报记录恢复失败：{enqueued.Error}";
            }
            _recoveredSubmission = await localState.LoadFixedSubmissionAsync(_localKey, ct);
            if (_recoveredSubmission is null && Manager.RequiresLocalAnswerRecovery &&
                await localState.LoadAnswerStateAsync(_localKey, ct) is null)
            {
                _localStateError = "本机保留了上次已经开考的参加考试记录，但原本地答案缺失。不能重建空答卷或重新开始，请联系考务人员恢复原记录。";
                return false;
            }
            if (_recoveredSubmission is null && Manager.ExamEnded &&
                await localState.LoadPendingSnapshotAsync(_localKey, ct) is { } snapshot)
            {
                // 已持久记录的 END 后崩溃：只冻结原可靠快照，绝不恢复编辑；服务端仍核对原授权。
                if (snapshot.ExamId != _localKey.ExamId || snapshot.CandidateId != _localKey.CandidateId || snapshot.DeviceId != device.DeviceId)
                    throw new InvalidDataException("结束恢复快照与当前考生、设备不一致。");
                _endedRecoveryIntent ??= new FixedSubmissionRecord($"subm-{Guid.NewGuid():N}", snapshot.PaperVersion,
                    snapshot.AnswerVersion, snapshot.Answers, snapshot.SavedAt, RequestIds.New(), snapshot.ParticipationId,
                    snapshot.EntryVersion, snapshot.DeviceId, snapshot.AuthorizationVersion);
                var fixedResult = await localState.TryFixSubmissionAsync(_localKey, _endedRecoveryIntent, ct);
                if (!fixedResult.Result.Succeeded || fixedResult.Record is null)
                {
                    _localStateError = fixedResult.Result.Error ?? "结束后的原答卷尚未确认固定，请重试本地保存。";
                    return false;
                }
                _recoveredSubmission = fixedResult.Record;
            }
            _localRecoveryChecked = true;
            _localStateError = null;
            return true;
        }
        catch (Exception error) when (IsLocalStorageError(error))
        {
            _localStateError = "本地恢复记录无法安全读取，原文件已保留。请修复本机存储后重试，不能清空记录重新开始。";
            return false;
        }
    }

    /// <summary>Live package unlock is the only runtime entry to an editable answer session.</summary>
    public async Task SynchronizeLocalAnswersAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _answerGate.WaitAsync(ct);
        try
        {
            if (!await InitializeLocalRecoveryCoreAsync(ct) || _disposed) { return; }
            if (Manager.CurrentPhase == ExamPackageManager.Phase.Revoked)
            {
                _paper = null;
                if (Answers is { } revoked) { await revoked.InvalidateAuthorizationAsync(ct); }
                return;
            }
            if (Manager.ExamEnded)
            {
                _paper = null;
                if (Answers is { } ended) { await FreezeEndedAnswersAsync(ended, ct); }
                else if (!HasFixedSubmission)
                {
                    // Recovery may have been checked before End arrived, without creating
                    // an AnswerSession yet. Freeze the original durable snapshot in that case.
                    _localRecoveryChecked = false;
                    await InitializeLocalRecoveryCoreAsync(ct);
                }
                return;
            }
            if (HasFixedSubmission) { return; }
            if (Manager.CurrentPhase != ExamPackageManager.Phase.Unlocked || Manager.UnlockedPayload is null ||
                Manager.Participation is not { BusinessState: ParticipationBusinessState.Answering } participation ||
                participation.Lifecycle == ExamLifecycle.Ended || Manager.Descriptor is not { } descriptor ||
                Manager.Server is not { } server || _localKey is null ||
                server.ServerId != _localKey.ServerKey || server.ServerEpoch < Math.Max(1, device.ServerEpoch) ||
                string.IsNullOrWhiteSpace(server.NodeId) || participation.CandidateId != _localKey.CandidateId ||
                participation.ExamId != _localKey.ExamId || participation.AuthorizedDeviceId != device.DeviceId)
            {
                return;
            }

            if (_paper is null || _paper.PaperVersion != descriptor.PaperVersion)
            {
                var payload = Manager.UnlockedPayload;
                if (payload is null) return;
                try
                {
                    var parsed = await Task.Run(() => StudentPaperPackage.ReadManifest(payload, Manager.ExamId, descriptor.PaperVersion, ct), ct);
                    // END/撤权/版本切换可能在工作线程读取时发生，旧解析结果不能授权新会话。
                    if (_disposed || Manager.ExamEnded || Manager.CurrentPhase != ExamPackageManager.Phase.Unlocked ||
                        !ReferenceEquals(payload, Manager.UnlockedPayload) || Manager.Descriptor != descriptor ||
                        Manager.Participation != participation || Manager.Server != server) return;
                    _paper = parsed;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
                {
                    _paper = null;
                    _localStateError = "试题包清单、PDF 或版本核对失败，原答案保留，不能开始作答。";
                    if (Answers is { } previous) await previous.SuspendAsync(_localStateError, ct);
                    return;
                }
            }
            var context = new AnswerSessionContext(participation.ParticipationId, participation.EntryVersion,
                participation.AuthorizationVersion, descriptor.PaperVersion, device.DeviceId, IsServerAuthorized: true);
            if (Answers is not null)
            {
                if (_answerContext != context)
                {
                    if (_answerContext is { } previous &&
                        previous.ParticipationId == context.ParticipationId &&
                        previous.EntryVersion == context.EntryVersion &&
                        previous.AuthorizationVersion == context.AuthorizationVersion &&
                        previous.DeviceId == context.DeviceId &&
                        context.PaperVersion > previous.PaperVersion &&
                        Answers.Status == AnswerSessionStatus.Active)
                    {
                        // 试卷修订：同一参试/设备/授权下仅版本提升时，
                        // 答案、标记与当前题结转为新版本继续作答，不清空不重建。
                        var switched = await Answers.SwitchPaperVersionAsync(
                            context.PaperVersion, _paper?.Structure, ct);
                        if (switched.Succeeded)
                        {
                            _answerContext = context;
                            _localStateError = null;
                        }
                        else
                        {
                            _localStateError = $"试卷版本切换未完成：{switched.Error} 原答案已保留，请联系考务人员。";
                        }
                    }
                    else
                    {
                        await Answers.InvalidateAuthorizationAsync(ct);
                        _localStateError = "参加考试、授权或试卷版本已变化，原答案未被覆盖；请重新核对恢复记录。";
                    }
                }
                return;
            }

            // Persist the initialization intent before exposing a session that can save
            // answers. Retry/cancellation and later key revocation must retain this evidence.
            await Manager.MarkLocalAnswersInitializedAsync(ct);
            if (_disposed || Manager.CurrentPhase == ExamPackageManager.Phase.Revoked) return;
            if (Manager.ExamEnded)
            {
                // End may arrive while the control journal is being written. Recover and
                // freeze an existing snapshot even though no AnswerSession was exposed yet.
                _localRecoveryChecked = false;
                await InitializeLocalRecoveryCoreAsync(ct);
                return;
            }
            if (Manager.CurrentPhase != ExamPackageManager.Phase.Unlocked) return;
            var answers = new AnswerSession(localState!, paper: _paper.Structure);
            Answers = answers;
            _answerContext = context;
            answers.WriteBlocked += OnAnswerWriteBlocked;
            answers.StateSaved += OnAnswerStateSaved;
            answers.SubmissionFixed += OnSubmissionFixed;
            var restored = await answers.StartAsync(_localKey, context, ct);
            _recoveredSubmission ??= answers.FixedSubmission;
            _localStateError = restored.IsUsable ? null : restored.Message;
            // END/revocation can arrive while SQLite recovery awaits its worker. Never let
            // a completed load reactivate a runtime that has already entered a terminal state.
            if (Manager.CurrentPhase == ExamPackageManager.Phase.Revoked)
                await answers.InvalidateAuthorizationAsync(ct);
            else if (_disposed)
                await answers.SuspendAsync("考试会话已关闭，原答案保留，不能新增修改。", ct);
            else if (Manager.ExamEnded)
                await FreezeEndedAnswersAsync(answers, ct);
        }
        catch (Exception error) when (IsLocalStorageError(error))
        {
            _localStateError = "本地答案恢复尚未完成，原记录已保留。请修复本机存储后重试。";
            if (Answers is { } answers) { await answers.SuspendAsync(_localStateError, CancellationToken.None); }
        }
        finally { _answerGate.Release(); NotifyStateChanged(); }
    }

    public async Task RetryLocalSaveAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_localRecoveryChecked)
        {
            await InitializeLocalRecoveryAsync(ct);
            return;
        }
        if (Answers is not { } answers)
        {
            await SynchronizeLocalAnswersAsync(ct);
            return;
        }
        if (answers.HasPendingPaperUpdate && !answers.HasPendingSubmission)
        {
            PaperUpdate.Retry();
            return;
        }
        var result = await answers.RetrySaveAsync(ct);
        _localStateError = result.Succeeded ? null : result.Error;
        NotifyStateChanged();
    }

    private void OnAnswerStateSaved() => NotifyStateChanged();

    private static async Task FreezeEndedAnswersAsync(AnswerSession answers, CancellationToken ct)
    {
        // 恢复读取期间先同步封锁编辑；完整状态读出后再固定，不补造空答卷。
        if (answers.Status == AnswerSessionStatus.NotStarted)
        {
            await answers.SuspendAsync("服务器已结束考试，原状态恢复后立即固定。", ct);
            return;
        }
        if (answers.Status == AnswerSessionStatus.PermissionDenied) return;
        await answers.FixSubmissionAsync(answers.FixedSubmissionId ?? $"subm-{Guid.NewGuid():N}", ct);
    }

    private void OnAnswerWriteBlocked(string message)
    {
        _localStateError = message;
        NotifyStateChanged();
    }

    private void OnSubmissionFixed(FixedSubmissionRecord record)
    {
        _recoveredSubmission = record;
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        if (_disposed) { return; }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) { StateChanged?.Invoke(this, EventArgs.Empty); }
        else if (!dispatcher.HasShutdownStarted)
            dispatcher.BeginInvoke(new Action(() => { if (!_disposed) StateChanged?.Invoke(this, EventArgs.Empty); }));
    }

    private static bool IsLocalStorageError(Exception error) => error is IOException or
        UnauthorizedAccessException or System.Data.Common.DbException or System.Text.Json.JsonException or
        CryptographicException or InvalidOperationException or ArgumentException;

    public async Task StartNotificationsAsync(ParticipationResult participation, ServerNodeEnvelope server)
    {
        await _notificationGate.WaitAsync(_lifetime.Token);
        try
        {
            if (_authorizationLost) throw new InvalidOperationException("当前设备授权已失效，不能重新建立答题连接。");
            if (Manager.Transport is { } transport && transport.CurrentTrustedEndpoint is { } endpoint)
                server = endpoint.Server;
            _recoveryParticipation = participation;
            if (Manager.Transport is { } http && device.Topology is not null)
                _transportRecovery ??= Task.Run(() => RecoverTransportAsync(http, _lifetime.Token));
            await StartNotificationsCoreAsync(participation, server);
        }
        finally { _notificationGate.Release(); }
    }

    private async Task RecoverTransportAsync(HttpStudentApi transport, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_authorizationLost)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                if (_authorizationLost || _disposed) return;
                if (_feed is SignalRExamCommandFeed { IsConnected: true } && transport.CurrentTrustedEndpoint?.Server == _connectedServer) continue;
                var response = await transport.GetServerTimeAsync(ct);
                var server = HttpStudentApi.RequireResult(response).Server;
                if (_recoveryParticipation is { } participation && !_authorizationLost && !_disposed)
                    await StartNotificationsAsync(participation, server);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or OperationCanceledException or TimeoutException or CryptographicException or UnauthorizedAccessException)
            { _handlingError = "正在核对可信服务器并恢复指令连接；原登记、答案与待传答卷保持不变。"; NotifyStateChanged(); }
        }
    }

    private async Task StartNotificationsCoreAsync(ParticipationResult participation, ServerNodeEnvelope server)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_feed is SignalRExamCommandFeed { IsConnected: true } &&
            _connectedServer == server && _connectedParticipation is { } previous &&
            previous.ExamId == participation.ExamId && previous.CandidateId == participation.CandidateId &&
            previous.ParticipationId == participation.ParticipationId && previous.EntryVersion == participation.EntryVersion &&
            previous.AuthorizedDeviceId == participation.AuthorizedDeviceId && previous.AuthorizationVersion == participation.AuthorizationVersion)
        {
            return;
        }

        if (_feed is not null)
        {
            await _feed.DisposeAsync();
            _feed = null;
        }

        var endpoint = Manager.Transport?.CurrentTrustedEndpoint;
        var feed = commandFeedFactory?.Invoke(endpoint?.Device ?? device, server)
            ?? new SignalRExamCommandFeed(endpoint?.Device ?? device, server);
        if (feed is SignalRExamCommandFeed signalR)
            signalR.AuthorizationLost = token => InvalidateAuthorizationAsync(token, endpoint?.Generation);
        try
        {
            await feed.StartAsync(participation, (delivery, token) =>
                _authorizationLost || _disposed || endpoint is { } captured && Manager.Transport?.IsCurrentEndpoint(captured.Generation) != true
                    ? Task.CompletedTask : ReceiveCommandAsync(delivery, token, endpoint?.Generation), _lifetime.Token);
            if (_authorizationLost || _disposed ||
                endpoint is { } current && Manager.Transport?.IsCurrentEndpoint(current.Generation) != true)
                throw new InvalidOperationException("授权或可信连接已变化，停止原通知连接。");
            _feed = feed;
            _connectedParticipation = participation;
            _connectedServer = server;
            _handlingError = null;
        }
        catch
        {
            await feed.DisposeAsync();
            _handlingError = "指令连接尚未建立，请重试连接；原参加考试登记保持不变。";
            throw;
        }
    }

    private Task ReceiveCommandAsync(CommandDelivery delivery, CancellationToken ct, long? generation = null)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("学生端界面尚未启动。");
        Task ApplyIfCurrent() => _authorizationLost || _disposed ||
            generation is { } expected && Manager.Transport?.IsCurrentEndpoint(expected) != true
                ? Task.CompletedTask : ApplyCommandAsync(delivery, ct);
        return dispatcher.CheckAccess() ? ApplyIfCurrent()
            : dispatcher.InvokeAsync(ApplyIfCurrent, DispatcherPriority.Normal, ct).Task.Unwrap();
    }

    private Task InvalidateAuthorizationAsync(CancellationToken ct, long? generation = null)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("学生端界面尚未启动。");
        // 连接自己的取消信号可能正是由撤销触发，不能因此跳过本地停用。
        Task InvalidateIfCurrent() => _disposed ||
            generation is { } expected && Manager.Transport?.IsCurrentEndpoint(expected) != true
                ? Task.CompletedTask : InvalidateAuthorizationOnUiAsync();
        return dispatcher.CheckAccess() ? InvalidateIfCurrent()
            : dispatcher.InvokeAsync(InvalidateIfCurrent, DispatcherPriority.Send).Task.Unwrap();
    }

    private async Task InvalidateAuthorizationOnUiAsync()
    {
        _authorizationLost = true;
        if (_disposed)
        {
            return;
        }

        try
        {
            // Both calls stop new work synchronously at their entry. Start both before
            // awaiting either persistence gate so a slow package operation cannot leave answers editable.
            var packageRevocation = Manager.InvalidateAuthorizationAsync(_lifetime.Token);
            var answerRevocation = Answers?.InvalidateAuthorizationAsync(CancellationToken.None) ?? Task.CompletedTask;
            NotifyStateChanged();
            await Task.WhenAll(packageRevocation, answerRevocation);
            _handlingError = "当前设备的答题授权已失效，已停止访问试题。请联系考务人员核对授权。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or CryptographicException or UnauthorizedAccessException)
        {
            _handlingError = "当前设备授权已失效，已停止访问试题；本机停用记录保存失败，请联系考务人员恢复。";
        }

        // 此回调仍属于指令连接，不在其中 Dispose/Stop 该连接，避免等待自身结束。
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ApplyCommandAsync(CommandDelivery delivery, CancellationToken ct)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var packageCommand = Manager.HandleCommandAsync(delivery, ct);
            // The manager validates END's full identity before synchronously marking Ended.
            // Merely receiving an END-shaped message is not sufficient to suspend this exam.
            var answerStop = Answers is { } answers
                ? Manager.CurrentPhase == ExamPackageManager.Phase.Revoked
                    ? answers.InvalidateAuthorizationAsync(CancellationToken.None)
                    : delivery.EventType == CommandEventType.End && Manager.ExamEnded
                        ? FreezeEndedAnswersAsync(answers, CancellationToken.None) : Task.CompletedTask
                : Task.CompletedTask;
            NotifyStateChanged();
            try { await Task.WhenAll(packageCommand, answerStop); }
            finally
            {
                // 依据本机已验证的持久指令，而不是原始网络消息；Received 回执丢失也不丢掉更新任务。
                if (Manager.IsRecordedPaperUpdate(delivery)) _paperUpdate?.Notify(delivery);
            }
            await SynchronizeLocalAnswersAsync(ct);
            if (delivery.EventType == CommandEventType.End && await packageCommand && Manager.ExamEnded && HasFixedSubmission)
                SubmissionRetryRequested?.Invoke(this, EventArgs.Empty);
            _handlingError = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || _lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or InvalidOperationException or TimeoutException
            or OperationCanceledException or CryptographicException or UnauthorizedAccessException or ArgumentException)
        {
            _handlingError = "服务器指令尚未处理完成，将继续使用原事件重试。请保持客户端运行。";
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _paper = null;
        var stopAnswers = Answers?.SuspendAsync("考试会话已关闭，原记录保留。", CancellationToken.None) ?? Task.CompletedTask;
        _lifetime.Cancel();
        if (_transportRecovery is { } recovery) await recovery;
        await _notificationGate.WaitAsync();
        try
        {
            // 先停更新编排器，避免其后台循环再触碰正在关闭的管理器与会话。
            var updateCoordinator = _paperUpdate;
            if (updateCoordinator is not null)
            {
                await updateCoordinator.DisposeAsync();
            }
            if (_feed is not null)
            {
                await _feed.DisposeAsync();
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException)
        {
            // 服务端已确认退出后的本地连接清理不应重新阻止退出；持久登记由管理器处理。
        }
        finally
        {
            try
            {
                await stopAnswers;
                await _answerGate.WaitAsync();
                try
                {
                    if (Answers is { } answers)
                    {
                        // A recovery may have been awaiting SQLite when disposal started.
                        await answers.SuspendAsync("考试会话已关闭，原记录保留。", CancellationToken.None);
                        answers.WriteBlocked -= OnAnswerWriteBlocked;
                        answers.StateSaved -= OnAnswerStateSaved;
                        answers.SubmissionFixed -= OnSubmissionFixed;
                    }
                }
                finally { _answerGate.Release(); }
            }
            finally
            {
                _feed = null;
                _connectedParticipation = null;
                _connectedServer = null;
                Manager.Dispose();
                _lifetime.Dispose();
                _notificationGate.Release();
            }
        }
    }
}
