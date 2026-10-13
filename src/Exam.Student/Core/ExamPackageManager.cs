using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Resources;
using Exam.Contracts.Student;

namespace Exam.Student.Core;

public sealed record PackageDownloadProgress(long ReceivedBytes, long TotalBytes);

/// <summary>在线更新结果：成功时 InstalledPaperVersion 为切换后的版本；失败保留旧版可继续作答。</summary>
public sealed record PaperUpdateResult(bool Succeeded, long InstalledPaperVersion, string? Error)
{
    public static PaperUpdateResult Ok(long version) => new(true, version, null);
    public static PaperUpdateResult Fail(string error) => new(false, 0, error);
}

/// <summary>Package control operations are serialized and ambiguous requests survive in a DPAPI journal.</summary>
public sealed partial class ExamPackageManager : IDisposable
{
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private readonly IExamPackageApi _api;
    internal HttpStudentApi? Transport => _api as HttpStudentApi;
    private readonly DeviceRecord _device;
    private readonly StudentSessionResult _session;
    private readonly string _candidateId;
    private readonly PackageControlStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _plaintextGate = new();
    private byte[]? _unlockedPayload;
    private long _controlGeneration;
    private volatile PackageControlState _state;
    private bool _freshPreparation;
    private volatile bool _authorizedThisRun;
    private volatile bool _terminalInMemory;
    private long _observedScheduleVersion;
    private long _observedEndVersion;
    private long _observedPauseVersion;
    private bool _disposed;

    public ExamPackageManager(IExamPackageApi api, string storageRoot, DeviceRecord device,
        StudentSessionResult session, string examId)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(session);
        if (device.Status != ActivationStatus.Active || string.IsNullOrWhiteSpace(device.ServerId) ||
            session.DeviceId != device.DeviceId || !session.ExamIds.Contains(examId, StringComparer.Ordinal))
            throw new InvalidOperationException("当前设备和登录考生无权准备该考试。");
        _api = api;
        _device = device;
        _session = session;
        ExamId = examId;
        _candidateId = session.CandidateIdsByExam?.GetValueOrDefault(examId) ?? session.Candidate.CandidateId;
        _store = new PackageControlStore(storageRoot, device.ServerId, device.DeviceId, _candidateId, examId);
        try
        {
            _state = _store.Load(new PackageControlState
            {
                ServerId = device.ServerId,
                DeviceId = device.DeviceId,
                CandidateId = _candidateId,
                ExamId = examId,
                AnswersInitialized = false
            });
        }
        catch { _store.Dispose(); throw; }
        _observedScheduleVersion = _state.ScheduleStateVersion;
        _observedEndVersion = _state.Ended ? _state.LifecycleStateVersion : 0;
        // An earlier run already started this participation. A missing answer database
        // must not be mistaken for a first-time blank exam during local persistence recovery.
        // Legacy journals omit AnswersInitialized; revocation may already have erased
        // their unlock material, so absence of the new field cannot prove no answers exist.
        RequiresLocalAnswerRecovery = (_state.Started || _state.AnswersInitialized == true) && _state.Participation is not null &&
            (_state.AnswersInitialized != false || _state.ProtectedUnlockMaterial is not null);
        CurrentPhase = _state.Ended ? Phase.Ended : _state.Revoked ? Phase.Revoked :
            _state.Participation?.BusinessState == ParticipationBusinessState.NotParticipating ? Phase.Withdrawn :
            _state.Participation is not null ? Phase.ConfirmedWaiting : Phase.Idle;
    }

    public enum Phase { Idle, Downloading, Downloaded, ConfirmedWaiting, Unlocked, Ended, Revoked, Withdrawn }
    public Phase CurrentPhase { get; private set; }
    public string ExamId { get; }
    public string SessionId => _session.SessionId;
    public string CandidateId => _candidateId;
    public bool RequiresLocalAnswerRecovery { get; }
    public ServerNodeEnvelope? Server => _state.Server;
    public ExamResourceDescriptor? Descriptor => _state.Preparation?.Resources;
    public InstructionsDocument? Instructions => _state.Instructions;
    public ParticipationResult? Participation => _state.Participation;
    public string? ParticipationId => Participation?.ParticipationId;
    public string? UnlockReference => Participation?.UnlockReference;
    public string? LastError { get; private set; }
    /// <summary>已收到的最新试卷版本（PaperUpdated 事件推进）；高于当前描述符版本表示有待应用的更新。</summary>
    public long NewestPaperVersion => _state.NewestPaperVersion;
    /// <summary>ScheduleChanged 事件携带的最新开考时间；null 表示未收到过时间更新。</summary>
    public DateTimeOffset? OverriddenScheduledStart => _state.OverriddenScheduledStart;
    /// <summary>ScheduleChanged 事件携带的最新结束时间；null 表示未收到过时间更新。</summary>
    public DateTimeOffset? OverriddenScheduledEnd => _state.OverriddenScheduledEnd;
    /// <summary>设备裁决：准备记录处于待裁决状态（同账号另一设备登录触发），等待考务决定。</summary>
    public bool DeviceDecisionPending =>
        !_state.Revoked && !_state.Ended && CurrentPhase == Phase.Idle &&
        _state.Preparation is { Status: EntryPreparationStatus.DeviceDecisionPending };
    public byte[]? UnlockedPayload { get { lock (_plaintextGate) return _unlockedPayload; } }
    public bool ExamStarted => _state.Started;
    public bool ExamEnded => _state.Ended || _terminalInMemory;
    public bool HasPendingConfirmation => _state.PendingConfirm is not null;
    // Only an authenticated terminal state allows an answering participation to leave.
    // Passing a cached schedule deadline is not an End acknowledgement.
    public bool CanLeaveWaitingPage => ExamEnded || Participation is null ||
        Participation.BusinessState is ParticipationBusinessState.Waiting or ParticipationBusinessState.NotParticipating;
    public Task<bool> MarkLocalAnswersInitializedAsync(CancellationToken ct = default) => LockedAsync(() =>
    {
        if (_state.AnswersInitialized != true) Save(_state with { AnswersInitialized = true });
        return Task.FromResult(true);
    }, ct);
    public bool HasPackage => IsPackageComplete();
    public bool HasInstructions => Descriptor is { } descriptor && Instructions is { } instructions &&
        instructions.Version == descriptor.InstructionsVersion && instructions.Lines.Count > 0;
    public string PackageFilePath => Descriptor is { } descriptor
        ? _store.PackagePath(descriptor.PaperVersion) : Path.Combine(_store.Root, "unprepared.epkg");
    public bool CanConfirmParticipation => _freshPreparation && !_authorizedThisRun && HasPackage && HasInstructions &&
        (!ExamEnded || Participation is not null) &&
        !_state.Revoked && _state.PendingWithdraw is null;

    public Task<bool> PrepareAndDownloadAsync(IProgress<PackageDownloadProgress>? progress = null,
        CancellationToken ct = default) => LockedAsync(async () =>
    {
        var generation = ControlGeneration;
        if (_state.PendingPaperInstallation is not null)
        {
            LastError = "本地更新提交结果尚未核对，请先执行答案恢复，不能覆盖原准备记录。";
            return false;
        }
        if (ExamEnded && Participation is null) { LastError = "当前考试已经结束。"; return false; }
        _authorizedThisRun = false;
        _freshPreparation = false;
        ClearPlaintext();
        if (_state.PendingWithdraw is not null)
        {
            await WithdrawCoreAsync(ct);
            LastError ??= "退出结果已确认，请返回选择考试页面。";
            return false;
        }
        if (_state.PendingConfirm is not null)
        {
            var replay = await ConfirmCoreAsync(_state.PendingConfirm, unlock: false, ct);
            if (replay is null || !replay.IsSuccess) return false;
        }
        var request = _state.PendingPrepare ?? new PrepareExamEntryRequest(RequestIds.New(), SessionId, ExamId);
        Save(_state with { PendingPrepare = request });
        var envelope = await _api.PrepareEntryAsync(request, ct);
        ValidateServer(envelope);
        if (!envelope.IsSuccess || envelope.Result is not { } preparation)
        {
            RecordFailure(envelope.Error);
            if (!Retryable(envelope.Error)) Save(_state with { PendingPrepare = null });
            return false;
        }
        if (preparation.ExamId != ExamId || string.IsNullOrWhiteSpace(preparation.PreparationId))
            throw new InvalidDataException("服务器返回的准备记录与当前考试不一致。");
        Save(_state with { PendingPrepare = null, Preparation = preparation, PreparationSessionId = request.SessionId });
        if (preparation.Status == EntryPreparationStatus.DeviceDecisionPending)
        {
            LastError = "该账号存在设备冲突，请等待考务人员裁决。";
            CurrentPhase = Phase.Idle;
            return false;
        }
        if (!Enum.IsDefined(preparation.Status) || preparation.Resources is not { } descriptor ||
            descriptor.PackageLength <= 0 || descriptor.PackageLength > MaximumPackageBytes ||
            descriptor.PaperVersion < 1 || descriptor.InstructionsVersion < 1 ||
            string.IsNullOrWhiteSpace(descriptor.EncryptedPackageUri) ||
            (preparation.AuthorizedDeviceId is not null && preparation.AuthorizedDeviceId != _device.DeviceId))
            throw new InvalidDataException("服务器返回的资源或设备授权不完整。");
        if (generation != ControlGeneration) return false;
        _freshPreparation = true;
        Save(_state with { Revoked = false });
        CurrentPhase = Phase.Downloading;
        if (!IsPackageComplete() && !await DownloadPackageAsync(descriptor, preparation.PreparationId,
                request.SessionId, progress, ct)) return false;
        if (!HasInstructions)
        {
            var instructions = await _api.GetInstructionsAsync(preparation.PreparationId, request.SessionId, ct);
            ValidateServer(instructions);
            if (!instructions.IsSuccess || instructions.Result is not { } document ||
                document.Version != descriptor.InstructionsVersion || document.Lines is null ||
                document.Lines.Count == 0 || document.Lines.Any(string.IsNullOrWhiteSpace))
            {
                LastError = "考试说明获取失败！说明缺失或版本不一致。";
                return false;
            }
            Save(_state with { Instructions = document });
        }
        if (generation != ControlGeneration) return false;
        CurrentPhase = ExamEnded ? Phase.Ended : Phase.Downloaded;
        LastError = null;
        return true;
    }, ct);

    public Task<ApiEnvelope<ParticipationResult>?> ConfirmParticipationAsync(CancellationToken ct = default) =>
        LockedAsync(async () =>
        {
            if (!CanConfirmParticipation || Descriptor is null || Instructions is null || _state.Preparation is null)
            {
                LastError = "试题和考试说明尚未全部就绪，不能确认参加考试。";
                return null;
            }
            var request = _state.PendingConfirm ?? new ConfirmParticipationRequest(RequestIds.New(),
                _state.PreparationSessionId!, ExamId, _state.Preparation.PreparationId,
                Descriptor.PaperVersion, Instructions.Version);
            return await ConfirmCoreAsync(request, unlock: true, ct);
        }, ct);

    public Task<ApiEnvelope<ParticipationResult>?> WithdrawAsync(CancellationToken ct = default) =>
        LockedAsync(() => WithdrawCoreAsync(ct), ct);
    public Task<bool> UnlockAsync(CancellationToken ct = default) => LockedAsync(() => UnlockCoreAsync(ct), ct);

    private async Task DownloadSameVersionIntoAsync(FileStream file, ExamResourceDescriptor descriptor,
        string preparationId, string sessionId, IProgress<PackageDownloadProgress>? progress, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await foreach (var chunk in _api.DownloadPackageChunksAsync(descriptor, preparationId, sessionId, ct))
                {
                    if (chunk.Length > descriptor.PackageLength - file.Length)
                        throw new InvalidDataException("试题下载数据超出声明的长度。");
                    await file.WriteAsync(chunk, ct);
                    progress?.Report(new PackageDownloadProgress(file.Length, descriptor.PackageLength));
                }
                if (file.Length != descriptor.PackageLength) throw new InvalidDataException("试题下载不完整。");
                file.Flush(flushToDisk: true);
                return;
            }
            catch (HttpStudentApi.DownloadEndpointChangedException) when (attempt == 0)
            {
                // Restart the same protected version; never concatenate bytes from two nodes.
                file.SetLength(0); file.Position = 0;
                progress?.Report(new PackageDownloadProgress(0, descriptor.PackageLength));
            }
        }
    }

    private async Task<bool> DownloadUpdatePackageAsync(ExamResourceDescriptor descriptor, string preparationId,
        string sessionId, IProgress<PackageDownloadProgress>? progress, CancellationToken ct)
    {
        var final = _store.PackagePath(descriptor.PaperVersion);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        var temporary = final + $".{Guid.NewGuid():N}.part";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await DownloadSameVersionIntoAsync(file, descriptor, preparationId, sessionId, progress, ct);
            }
            if (File.Exists(final)) File.Replace(temporary, final, final + ".bak");
            else File.Move(temporary, final);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
        {
            LastError = "新版试题获取失败！请检查网络或磁盘后重试，当前版本可继续作答。";
            return false;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task InvalidateAuthorizationAsync(CancellationToken ct = default)
    {
        StopImmediately(ended: false);
        LastError = "当前设备的答题授权已失效，请重新连接并由服务器核对。";
        return LockedAsync(() =>
        {
            Save(_state with { Revoked = true, ProtectedUnlockMaterial = null });
            return Task.FromResult(true);
        }, ct);
    }

    public Task<bool> HandleCommandAsync(CommandDelivery delivery, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        // END 和设备裁决先撤掉可见内容，不排在长下载后面；只接受已核对的当前上下文。
        if (MatchesCurrentAuthorization(delivery))
        {
            var recorded = _state.Commands.SingleOrDefault(item => item.Delivery.EventId == delivery.EventId);
            if (recorded is null || SameCommand(recorded.Delivery, delivery))
            {
                ObserveSchedule(delivery);
                if (delivery.EventType == CommandEventType.End) TryEndLocally(delivery.StateVersion);
                if (CanPauseForSchedule(delivery)) PauseLocally(delivery.StateVersion);
            }
        }
        var decision = IsVerifiedDeviceDecision(delivery);
        var decisionParticipation = decision ? Participation : null;
        if (decision) StopImmediately(ended: false);
        return LockedAsync(async () =>
        {
            if (decision && Participation is { } current && decisionParticipation is { } prior &&
                current.ParticipationId == prior.ParticipationId && current.EntryVersion == prior.EntryVersion &&
                current.AuthorizationVersion == prior.AuthorizationVersion)
            {
                Save(_state with { Revoked = true, ProtectedUnlockMaterial = null });
                LastError = "当前设备的授权已变化，已停止作答，请重新进入核对服务器授权。";
                if (delivery.TargetAuthorizationVersion != current.AuthorizationVersion) return false;
                return await RecordAndAcknowledgeAsync(delivery, CommandProcessingResult.Received, ct);
            }
            if (delivery.EventType == CommandEventType.PaperUpdated && delivery.ResourceVersion is not > 0)
            {
                LastError = "试卷更新指令缺少有效资源版本，未使用其他版本字段猜测。";
                return false;
            }
            if (!MatchesCurrentAuthorization(delivery))
            {
                LastError = "收到过期或不属于当前设备的指令，已拒绝处理。";
                return false;
            }
            AcceptServer(delivery.Server);
            var previous = _state.Commands.SingleOrDefault(item => item.Delivery.EventId == delivery.EventId);
            if (previous is not null)
            {
                if (!SameCommand(previous.Delivery, delivery))
                    throw new InvalidDataException("相同指令标识对应了不同内容。");
                if (delivery.EventType == CommandEventType.Start && !ExamEnded && CanApplyStart(delivery) &&
                    previous.Acknowledgement.ProcessingResult == CommandProcessingResult.Applied &&
                    UnlockedPayload is null && !await UnlockCoreAsync(ct)) return false;
                return await SendAcknowledgementAsync(previous, ct);
            }
            if (delivery.EventType == CommandEventType.Start)
            {
                if (!CanApplyStart(delivery))
                    return await RecordAndAcknowledgeAsync(delivery, CommandProcessingResult.RejectedAsStale, ct);
                if (_state.PendingWithdraw is not null)
                {
                    await WithdrawCoreAsync(ct);
                    if (_state.PendingWithdraw is not null || CurrentPhase == Phase.Withdrawn) return false;
                }
                if (Descriptor is null || (delivery.ResourceVersion is { } version && version != Descriptor.PaperVersion) ||
                    _state.NewestPaperVersion > Descriptor.PaperVersion ||
                    string.IsNullOrWhiteSpace(delivery.ProtectedUnlockReference))
                {
                    LastError = "试卷版本或解锁信息已变化，请重新获取考试资料。";
                    return false;
                }
                if (!TryApplyStart(delivery))
                    return await RecordAndAcknowledgeAsync(delivery, CommandProcessingResult.RejectedAsStale, ct);
                if (!await UnlockCoreAsync(ct)) return false;
                return await RecordAndAcknowledgeAsync(delivery, CommandProcessingResult.Applied, ct);
            }
            if (delivery.EventType == CommandEventType.End)
            {
                if (!TryEndLocally(delivery.StateVersion))
                    return await RecordAndAcknowledgeAsync(delivery, CommandProcessingResult.RejectedAsStale, ct);
                Save(_state with
                {
                    Ended = true,
                    ProtectedUnlockMaterial = null,
                    LifecycleStateVersion = Math.Max(_state.LifecycleStateVersion, delivery.StateVersion)
                });
                return await RecordAndAcknowledgeAsync(delivery, CommandProcessingResult.Applied, ct);
            }
            if (delivery.EventType == CommandEventType.DeviceDecided)
            {
                _authorizedThisRun = false;
                ClearPlaintext();
                CurrentPhase = Phase.Revoked;
                Save(_state with { Revoked = true, ProtectedUnlockMaterial = null });
                LastError = "设备授权已调整，请重新进入以核对当前授权。";
                return await RecordAndAcknowledgeAsync(delivery, CommandProcessingResult.Received, ct);
            }
            if (delivery.EventType == CommandEventType.ScheduleChanged)
            {
                // 计划版本与试卷版本分开；旧消息晚到、重连和重启均不能把新时间改回去。
                var previousVersion = Math.Max(_state.ScheduleStateVersion, _state.Commands
                    .Where(item => item.Delivery.EventType == CommandEventType.ScheduleChanged &&
                                   item.Acknowledgement.ProcessingResult != CommandProcessingResult.RejectedAsStale)
                    .Select(item => item.Delivery.StateVersion).DefaultIfEmpty(0).Max());
                if (delivery.StateVersion <= previousVersion)
                    return await RecordAndAcknowledgeAsync(delivery, CommandProcessingResult.RejectedAsStale, ct);
                if (delivery.NewScheduledStart is null && delivery.NewScheduledEnd is null)
                {
                    LastError = "时间调整指令缺少起止时间，请重新同步考试信息。";
                    return await RecordAndAcknowledgeAsync(delivery, CommandProcessingResult.Received, ct);
                }
                var startsAt = delivery.NewScheduledStart ?? _state.OverriddenScheduledStart;
                var endsAt = delivery.NewScheduledEnd ?? _state.OverriddenScheduledEnd;
                if (startsAt is { } start && endsAt is { } end && start >= end)
                    throw new InvalidDataException("时间调整指令的结束时间必须晚于开始时间。");
                if (CanPauseForSchedule(delivery))
                {
                    PauseLocally(delivery.StateVersion);
                    Save(_state with
                    {
                        Started = false,
                        Ended = false,
                        ProtectedUnlockMaterial = null,
                        LifecycleStateVersion = delivery.StateVersion,
                        ReopenedAtScheduleVersion = Math.Max(_state.ReopenedAtScheduleVersion, ReopenedVersion(delivery)),
                        Participation = Participation! with { Lifecycle = ExamLifecycle.Scheduled }
                    });
                }
                Save(_state with
                {
                    ScheduleStateVersion = delivery.StateVersion,
                    OverriddenScheduledStart = startsAt,
                    OverriddenScheduledEnd = endsAt
                });
                LastError = null;
                return await RecordAndAcknowledgeAsync(delivery, CommandProcessingResult.Applied, ct);
            }
            if (delivery.EventType == CommandEventType.PaperUpdated)
            {
                Save(_state with { NewestPaperVersion = Math.Max(_state.NewestPaperVersion, delivery.ResourceVersion!.Value) });
                LastError = "已收到试卷更新，当前版本尚未完成更新，请重新同步。";
                return await RecordAndAcknowledgeAsync(delivery, CommandProcessingResult.Received, ct);
            }
            LastError = "当前页面不能处理该指令。";
            return false;
        }, ct);
    }

    private async Task<ApiEnvelope<ParticipationResult>?> ConfirmCoreAsync(
        ConfirmParticipationRequest request, bool unlock, CancellationToken ct)
    {
        var generation = ControlGeneration;
        Save(_state with { PendingConfirm = request });
        var response = await _api.ConfirmParticipationAsync(request, ct);
        ValidateServer(response);
        if (!response.IsSuccess || response.Result is not { } participation)
        {
            RecordFailure(response.Error);
            if (!Retryable(response.Error)) Save(_state with { PendingConfirm = null });
            return response;
        }
        ValidateParticipation(participation);
        if (participation.BusinessState is not (ParticipationBusinessState.Waiting or ParticipationBusinessState.Answering))
            throw new InvalidDataException("确认参加考试未返回可等待或恢复的参加考试记录。");
        var reopened = participation.Lifecycle != ExamLifecycle.Ended &&
            participation.ReopenedAtScheduleVersion > Math.Max(_observedEndVersion, _state.LifecycleStateVersion) &&
            participation.ReopenedAtScheduleVersion <= participation.ScheduleVersion;
        var ended = (!reopened && ExamEnded) || participation.Lifecycle == ExamLifecycle.Ended;
        Save(_state with
        {
            PendingConfirm = null,
            Participation = participation,
            Started = participation.Lifecycle == ExamLifecycle.Running &&
                (_state.Started || participation.BusinessState == ParticipationBusinessState.Answering),
            Ended = ended,
            LifecycleStateVersion = Math.Max(_state.LifecycleStateVersion, participation.ScheduleVersion),
            ReopenedAtScheduleVersion = Math.Max(_state.ReopenedAtScheduleVersion, participation.ReopenedAtScheduleVersion),
            ScheduleStateVersion = Math.Max(_state.ScheduleStateVersion, participation.ScheduleVersion),
            OverriddenScheduledStart = participation.ScheduleVersion >= _state.ScheduleStateVersion
                ? participation.ScheduledStart ?? _state.OverriddenScheduledStart : _state.OverriddenScheduledStart,
            OverriddenScheduledEnd = participation.ScheduleVersion >= _state.ScheduleStateVersion
                ? participation.ScheduledEnd ?? _state.OverriddenScheduledEnd : _state.OverriddenScheduledEnd,
            Revoked = false,
            ProtectedUnlockMaterial = ended ? null : _state.ProtectedUnlockMaterial
        });
        lock (_plaintextGate)
        {
            if (generation != _controlGeneration)
            {
                LastError = "确认期间考试状态或设备授权已经变化，请重新核对。";
                return response;
            }
            _authorizedThisRun = true;
            if (reopened) _terminalInMemory = false;
            if (participation.Lifecycle == ExamLifecycle.Ended)
                _observedEndVersion = Math.Max(_observedEndVersion, participation.ScheduleVersion);
            if (participation.Lifecycle == ExamLifecycle.Scheduled)
            {
                // This response belongs to the current serialized operation. Applying it must
                // not invalidate the outer preparation that is replaying a lost confirmation.
                _observedPauseVersion = Math.Max(_observedPauseVersion, participation.ScheduleVersion);
                ClearPlaintext();
            }
        }
        LastError = null;
        if (ended)
        {
            EndLocally();
            LastError = "考试已经结束，仅可恢复未完成的交卷任务。";
        }
        else
        {
            CurrentPhase = Phase.ConfirmedWaiting;
            if (unlock && participation.Lifecycle == ExamLifecycle.Running &&
                participation.BusinessState == ParticipationBusinessState.Answering)
                await UnlockCoreAsync(ct);
        }
        return response;
    }

    private async Task<ApiEnvelope<ParticipationResult>?> WithdrawCoreAsync(CancellationToken ct)
    {
        if (_state.PendingConfirm is not null)
        {
            var confirmation = await ConfirmCoreAsync(_state.PendingConfirm, unlock: false, ct);
            if (confirmation is null || !confirmation.IsSuccess) return confirmation;
        }
        if (_state.PendingWithdraw is null && (Participation is null ||
                Participation.BusinessState != ParticipationBusinessState.Waiting))
        {
            LastError = "当前状态不能直接退出考试，请先核对服务器状态。";
            return null;
        }
        var request = _state.PendingWithdraw ?? new WithdrawParticipationRequest(RequestIds.New(),
            SessionId, Participation!.ParticipationId, Participation.Version);
        Save(_state with { PendingWithdraw = request });
        var response = await _api.WithdrawParticipationAsync(request, ct);
        ValidateServer(response);
        if (!response.IsSuccess || response.Result is not { } withdrawn)
        {
            RecordFailure(response.Error);
            if (!Retryable(response.Error)) Save(_state with { PendingWithdraw = null });
            return response;
        }
        ValidateParticipation(withdrawn);
        if (withdrawn.ParticipationId != request.ParticipationId || withdrawn.BusinessState != ParticipationBusinessState.NotParticipating ||
            withdrawn.EntryVersion != Participation?.EntryVersion || withdrawn.Version < request.ExpectedVersion)
            throw new InvalidDataException("退出响应未确认当前参加考试登记已经退出。");
        Save(_state with
        {
            PendingWithdraw = null,
            Participation = withdrawn,
            Started = false,
            ProtectedUnlockMaterial = null,
            Preparation = null,
            PreparationSessionId = null
        });
        _freshPreparation = false;
        _authorizedThisRun = false;
        ClearPlaintext();
        CurrentPhase = Phase.Withdrawn;
        LastError = null;
        return response;
    }

    private async Task<bool> UnlockCoreAsync(CancellationToken ct)
    {
        var generation = ControlGeneration;
        if (!_authorizedThisRun || !ExamStarted || ExamEnded || _state.Revoked || !HasPackage ||
            !HasInstructions || Participation is null || Descriptor is null || Server is null ||
            string.IsNullOrWhiteSpace(UnlockReference) || _state.NewestPaperVersion > Descriptor.PaperVersion)
        {
            LastError = "当前身份、题包或考试状态尚不允许解锁。";
            return false;
        }
        if (UnlockedPayload is not null) return true;
        var response = await _api.ResolveUnlockAsync(new PackageUnlockRequest(RequestIds.New(),
            Participation.ParticipationId, Participation.EntryVersion, _device.DeviceId,
            Participation.AuthorizationVersion, Descriptor.PaperVersion, UnlockReference,
            _state.ServerId, Server.ServerEpoch), ct);
        using var material = response.Result;
        ValidateServer(response);
        if (!response.IsSuccess || material is null) { RecordFailure(response.Error); return false; }
        if (material.PaperVersion != Descriptor.PaperVersion ||
            !string.Equals(UnlockReference, $"resource-envelope:{material.PackageId}", StringComparison.Ordinal))
            throw new CryptographicException("解锁材料与当前试卷版本不一致。");
        var protectedMaterial = _store.ProtectUnlock(material);
        await using var plaintext = new MemoryStream();
        byte[]? completed = null;
        try
        {
            await using var encrypted = new FileStream(PackageFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await PackageCiphertext.DecryptAsync(encrypted, plaintext, material, ct);
            completed = plaintext.ToArray();
            if (generation != ControlGeneration) return false;
            Save(_state with { ProtectedUnlockMaterial = protectedMaterial });
            lock (_plaintextGate)
            {
                if (generation != _controlGeneration || ExamEnded || !_authorizedThisRun) return false;
                _unlockedPayload = completed;
                completed = null;
                CurrentPhase = Phase.Unlocked;
                LastError = null;
            }
            return true;
        }
        catch (CryptographicException)
        {
            Save(_state with { CompletedPaperVersion = null, CompletedPackageUri = null, ProtectedUnlockMaterial = null });
            LastError = "试题解密失败！密钥或题包认证不匹配，未显示题目。";
            return false;
        }
        catch (EndOfStreamException)
        {
            Save(_state with { CompletedPaperVersion = null, CompletedPackageUri = null, ProtectedUnlockMaterial = null });
            LastError = "试题解密失败！题包不完整，未显示题目。";
            return false;
        }
        finally
        {
            if (completed is not null) CryptographicOperations.ZeroMemory(completed);
            if (plaintext.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan());
        }
    }

    private async Task<bool> DownloadPackageAsync(ExamResourceDescriptor descriptor, string preparationId,
        string sessionId, IProgress<PackageDownloadProgress>? progress, CancellationToken ct)
    {
        var final = _store.PackagePath(descriptor.PaperVersion);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        var temporary = final + $".{Guid.NewGuid():N}.part";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await DownloadSameVersionIntoAsync(file, descriptor, preparationId, sessionId, progress, ct);
            }
            if (File.Exists(final)) File.Replace(temporary, final, final + ".bak");
            else File.Move(temporary, final);
            Save(_state with
            {
                CompletedPaperVersion = descriptor.PaperVersion,
                CompletedPackageUri = descriptor.EncryptedPackageUri
            });
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
        {
            LastError = "试题获取失败！请检查网络或磁盘后重试。";
            return false;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<bool> RecordAndAcknowledgeAsync(CommandDelivery delivery,
        CommandProcessingResult result, CancellationToken ct)
    {
        if (_state.Commands.Count >= 4096) throw new IOException("本机指令恢复记录已达上限，请联系考务人员。");
        var record = new ProcessedPackageCommand(delivery,
            new AcknowledgeCommandRequest(RequestIds.New(), delivery.EventId, delivery.ParticipationId,
                delivery.EntryVersion, _device.DeviceId, delivery.TargetAuthorizationVersion, result,
                Descriptor?.PaperVersion, result == CommandProcessingResult.Received ? LastError : null), false, DateTimeOffset.UtcNow);
        Save(_state with { Commands = _state.Commands.Append(record).ToArray() });
        return await SendAcknowledgementAsync(record, ct);
    }

    private async Task<bool> SendAcknowledgementAsync(ProcessedPackageCommand record, CancellationToken ct)
    {
        var response = await _api.AcknowledgeCommandAsync(record.Acknowledgement, ct);
        ValidateServer(response);
        if (!response.IsSuccess || response.Result is not { } acknowledgement)
        { RecordFailure(response.Error); return false; }
        if (acknowledgement.EventId != record.Delivery.EventId ||
            acknowledgement.RequestId != record.Acknowledgement.RequestId ||
            acknowledgement.ProcessingResult != record.Acknowledgement.ProcessingResult ||
            acknowledgement.ActualPaperVersion != record.Acknowledgement.ActualPaperVersion ||
            acknowledgement.FailureReason != record.Acknowledgement.FailureReason)
            throw new InvalidDataException("服务器返回的指令确认与当前指令不一致。");
        if (!record.Acknowledged)
            Save(_state with
            {
                Commands = _state.Commands.Select(item => item.Delivery.EventId == record.Delivery.EventId &&
                    item.Acknowledgement.RequestId == record.Acknowledgement.RequestId
                ? item with { Acknowledged = true } : item).ToArray()
            });
        return true;
    }

    private bool IsPackageComplete() => Descriptor is { } descriptor &&
        _state.CompletedPaperVersion == descriptor.PaperVersion && _state.CompletedPackageUri == descriptor.EncryptedPackageUri &&
        File.Exists(_store.PackagePath(descriptor.PaperVersion)) &&
        new FileInfo(_store.PackagePath(descriptor.PaperVersion)).Length == descriptor.PackageLength;
    private bool MatchesCurrentAuthorization(CommandDelivery command) => Participation is { } participation &&
        _authorizedThisRun && command.Server is not null && command.Metadata is not null && command.Server.ServerId == _state.ServerId &&
        command.Server.ServerEpoch >= Math.Max(_device.ServerEpoch, Server?.ServerEpoch ?? 0) &&
        !string.IsNullOrWhiteSpace(command.Server.NodeId) && !string.IsNullOrWhiteSpace(command.EventId) &&
        command.ExamId == ExamId && command.ParticipationId == participation.ParticipationId &&
        command.EntryVersion == participation.EntryVersion && command.TargetDeviceId == _device.DeviceId &&
        command.TargetAuthorizationVersion == participation.AuthorizationVersion && command.StateVersion > 0;
    private static bool SameCommand(CommandDelivery first, CommandDelivery next) =>
        first.ExamId == next.ExamId && first.ParticipationId == next.ParticipationId && first.EntryVersion == next.EntryVersion &&
        first.EventType == next.EventType && first.StateVersion == next.StateVersion && first.TargetDeviceId == next.TargetDeviceId &&
        first.TargetAuthorizationVersion == next.TargetAuthorizationVersion && first.ResourceVersion == next.ResourceVersion &&
        first.ProtectedUnlockReference == next.ProtectedUnlockReference &&
        first.NewScheduledStart == next.NewScheduledStart && first.NewScheduledEnd == next.NewScheduledEnd &&
        first.Metadata.Count == next.Metadata.Count &&
        first.Metadata.All(pair => next.Metadata.TryGetValue(pair.Key, out var value) && pair.Value == value);
    private void ValidateParticipation(ParticipationResult participation)
    {
        if (participation.ExamId != ExamId || participation.CandidateId != _candidateId ||
            participation.AuthorizedDeviceId != _device.DeviceId || string.IsNullOrWhiteSpace(participation.ParticipationId) ||
            participation.EntryVersion < 1 || participation.AuthorizationVersion < 1 || participation.Version < 1)
            throw new InvalidDataException("服务器返回的参加考试身份或授权与当前考生不一致。");
    }
    private void ValidateServer<T>(ApiEnvelope<T> response)
    {
        if (response.IsSuccess || !string.IsNullOrWhiteSpace(response.ServerId))
            AcceptServer(new ServerNodeEnvelope(response.ServerId, response.NodeId, response.ServerEpoch));
    }
    private void AcceptServer(ServerNodeEnvelope server)
    {
        if (server.ServerId != _state.ServerId || string.IsNullOrWhiteSpace(server.NodeId) ||
            server.ServerEpoch < Math.Max(1, Math.Max(_device.ServerEpoch, Server?.ServerEpoch ?? 0)))
            throw new InvalidDataException("服务器身份或服务代次与当前考试不一致。");
        if (Server != server) Save(_state with { Server = server });
    }
    private void Save(PackageControlState state) { _store.Save(state); _state = state; }
    private void RecordFailure(ApiError? error)
    {
        LastError = error?.Message ?? "服务器未确认操作结果，请重试。";
        if (error?.Code is ErrorCodes.AuthorizationRevoked or ErrorCodes.NotActivated or "UNAUTHORIZED")
        {
            StopImmediately(ended: false);
            Save(_state with { Revoked = true, ProtectedUnlockMaterial = null });
        }
    }
    private static bool Retryable(ApiError? error) => error is null || error.Retryable ||
        error.Code is ErrorCodes.ServiceRecovering or ErrorCodes.PersistenceFailed or ErrorCodes.NotActiveServer or
            ErrorCodes.StaleServerEpoch or ErrorCodes.ReplicaUnavailable;
    private async Task<T> LockedAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { ObjectDisposedException.ThrowIf(_disposed, this); return await Task.Run(action, ct).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        { LastError ??= "操作尚未确认，请检查网络和本机恢复存储后重试。"; throw; }
        finally { _gate.Release(); }
    }
    private long ControlGeneration { get { lock (_plaintextGate) return _controlGeneration; } }
    private void ObserveSchedule(CommandDelivery delivery)
    {
        if (delivery.EventType is not (CommandEventType.ScheduleChanged or CommandEventType.Start) ||
            delivery.NewScheduledStart is not { } start || delivery.NewScheduledEnd is not { } end || start >= end)
            return;
        lock (_plaintextGate)
            _observedScheduleVersion = Math.Max(_observedScheduleVersion, delivery.StateVersion);
    }

    private bool TryEndLocally(long version)
    {
        lock (_plaintextGate)
        {
            if (version < Math.Max(_state.ScheduleStateVersion,
                    Math.Max(_observedScheduleVersion, _state.LifecycleStateVersion))) return false;
            _observedEndVersion = Math.Max(_observedEndVersion, version);
            EndLocally();
            return true;
        }
    }

    private static long ReopenedVersion(CommandDelivery delivery) =>
        delivery.Metadata.TryGetValue("reopenedAtScheduleVersion", out var text) &&
        long.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture,
            out var version) && version > 0 && version <= delivery.StateVersion ? version : 0;

    private bool CanApplyStart(CommandDelivery delivery)
    {
        lock (_plaintextGate)
        {
            if (delivery.StateVersion < Math.Max(_state.LifecycleStateVersion, _observedPauseVersion) ||
                delivery.StateVersion < _state.ReopenedAtScheduleVersion) return false;
            if (!ExamEnded) return true;
            // Only a newer, explicit reopening issued by this exam's authenticated server can
            // supersede END. Ordinary START retries and an END in the same version stay terminal.
            return ReopenedVersion(delivery) > _observedEndVersion &&
                delivery.NewScheduledStart is { } start && delivery.NewScheduledEnd is { } end && start < end;
        }
    }

    private bool TryApplyStart(CommandDelivery delivery)
    {
        lock (_plaintextGate)
        {
            if (!CanApplyStart(delivery)) return false;
            var hasWindow = delivery.NewScheduledStart is { } start && delivery.NewScheduledEnd is { } end && start < end;
            var newerWindow = hasWindow && delivery.StateVersion >= _state.ScheduleStateVersion;
            Save(_state with
            {
                Started = true,
                Ended = false,
                LifecycleStateVersion = delivery.StateVersion,
                ReopenedAtScheduleVersion = Math.Max(_state.ReopenedAtScheduleVersion, ReopenedVersion(delivery)),
                ScheduleStateVersion = newerWindow ? delivery.StateVersion : _state.ScheduleStateVersion,
                OverriddenScheduledStart = newerWindow ? delivery.NewScheduledStart : _state.OverriddenScheduledStart,
                OverriddenScheduledEnd = newerWindow ? delivery.NewScheduledEnd : _state.OverriddenScheduledEnd,
                Participation = Participation! with
                {
                    BusinessState = ParticipationBusinessState.Answering,
                    Lifecycle = ExamLifecycle.Running,
                    UnlockReference = delivery.ProtectedUnlockReference
                }
            });
            _terminalInMemory = false;
            return true;
        }
    }

    private void EndLocally() => StopImmediately(ended: true);
    private bool CanPauseForSchedule(CommandDelivery delivery)
    {
        lock (_plaintextGate)
            return delivery.EventType == CommandEventType.ScheduleChanged &&
                delivery.Metadata.TryGetValue("lifecycleState", out var lifecycle) && lifecycle == "scheduled" &&
                delivery.NewScheduledStart is { } start && delivery.NewScheduledEnd is { } end && start < end &&
                delivery.StateVersion > Math.Max(_state.LifecycleStateVersion, _observedEndVersion) &&
                delivery.StateVersion >= _observedScheduleVersion;
    }

    private void PauseLocally(long version)
    {
        lock (_plaintextGate)
        {
            _controlGeneration++;
            _observedPauseVersion = Math.Max(_observedPauseVersion, version);
            _terminalInMemory = false;
            ClearPlaintext();
            CurrentPhase = Phase.ConfirmedWaiting;
        }
    }

    private void StopImmediately(bool ended)
    {
        byte[]? plaintext;
        lock (_plaintextGate)
        {
            _controlGeneration++;
            if (ended) _terminalInMemory = true;
            else { _authorizedThisRun = false; _freshPreparation = false; }
            plaintext = _unlockedPayload;
            _unlockedPayload = null;
            CurrentPhase = ended ? Phase.Ended : Phase.Revoked;
        }
        if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
    }
    private void ClearPlaintext()
    {
        byte[]? plaintext;
        lock (_plaintextGate) { plaintext = _unlockedPayload; _unlockedPayload = null; }
        if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
    }
    public void Dispose()
    {
        _gate.Wait();
        try { if (_disposed) return; _disposed = true; ClearPlaintext(); _store.Dispose(); }
        finally { _gate.Release(); }
    }
}
