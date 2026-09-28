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

/// <summary>B06 在线更新结果：成功时 InstalledPaperVersion 为切换后的版本；失败保留旧版可继续作答。</summary>
public sealed record PaperUpdateResult(bool Succeeded, long InstalledPaperVersion, string? Error)
{
    public static PaperUpdateResult Ok(long version) => new(true, version, null);
    public static PaperUpdateResult Fail(string error) => new(false, 0, error);
}

/// <summary>B02 control operations are serialized and ambiguous requests survive in a DPAPI journal.</summary>
public sealed class ExamPackageManager : IDisposable
{
    private const long MaximumPackageBytes = 512L * 1024 * 1024;
    private readonly IExamPackageApi _api;
    private readonly DeviceRecord _device;
    private readonly StudentSessionResult _session;
    private readonly string _candidateId;
    private readonly PackageControlStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _plaintextGate = new();
    private byte[]? _unlockedPayload;
    private long _controlGeneration;
    private PackageControlState _state;
    private bool _freshPreparation;
    private bool _authorizedThisRun;
    private bool _terminalInMemory;
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
                ExamId = examId
            });
        }
        catch { _store.Dispose(); throw; }
        // An earlier run already started this participation. A missing answer database
        // must not be mistaken for a first-time blank exam during B04 recovery.
        RequiresLocalAnswerRecovery = _state.Started && _state.Participation is not null;
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
    /// <summary>B06 设备裁决：准备记录处于待裁决状态（同账号另一设备登录触发），等待考务决定。</summary>
    public bool DeviceDecisionPending =>
        !_state.Revoked && !_state.Ended && CurrentPhase == Phase.Idle &&
        _state.Preparation is { Status: EntryPreparationStatus.DeviceDecisionPending };
    public byte[]? UnlockedPayload { get { lock (_plaintextGate) return _unlockedPayload; } }
    public bool ExamStarted => _state.Started;
    public bool ExamEnded => _state.Ended || _terminalInMemory;
    public bool HasPendingConfirmation => _state.PendingConfirm is not null;
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
                LastError = "试题和考试说明尚未全部就绪，不能确认参试。";
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

    /// <summary>
    /// B06 在线更新：重新准备→下载新版→重新确认拿新解锁引用→解密并解析新版清单，
    /// 全部通过后才一次性切换明文与控制状态；任一步失败时旧版明文、描述符与参试状态
    /// 全部保持原样，考试可以继续用旧包作答，稍后重试。
    /// </summary>
    public Task<PaperUpdateResult> ApplyPaperUpdateAsync(
        IProgress<PackageDownloadProgress>? progress = null, CancellationToken ct = default) =>
        LockedAsync(async () =>
        {
            if (CurrentPhase != Phase.Unlocked || UnlockedPayload is null)
                return PaperUpdateResult.Fail("试题尚未解锁，不能应用试卷更新。");
            if (ExamEnded || _state.Revoked)
                return PaperUpdateResult.Fail("考试已结束或授权已失效，不再应用试卷更新。");
            if (Descriptor is not { } currentDescriptor || Participation is not { BusinessState: ParticipationBusinessState.Answering } currentParticipation)
                return PaperUpdateResult.Fail("当前没有可更新的已确认参试状态。");
            if (_state.NewestPaperVersion <= currentDescriptor.PaperVersion)
                return PaperUpdateResult.Fail("当前没有待应用的试卷更新。");
            if (_state.PendingWithdraw is not null)
                return PaperUpdateResult.Fail("正在退出参试，不能应用试卷更新。");

            var generation = ControlGeneration;

            // 1) 重新准备，取得新版资源描述符。
            var prepareRequest = new PrepareExamEntryRequest(RequestIds.New(), SessionId, ExamId);
            var prepared = await _api.PrepareEntryAsync(prepareRequest, ct);
            ValidateServer(prepared);
            if (!prepared.IsSuccess || prepared.Result is not { } preparation)
                return PaperUpdateResult.Fail($"试卷更新准备失败：{prepared.Error?.Message ?? "服务器未确认"}");
            if (preparation.Status == EntryPreparationStatus.DeviceDecisionPending)
                return PaperUpdateResult.Fail("该账号存在设备冲突，请等待考务人员裁决。");
            if (preparation.ExamId != ExamId || string.IsNullOrWhiteSpace(preparation.PreparationId) ||
                preparation.Resources is not { } descriptor ||
                descriptor.PaperVersion <= currentDescriptor.PaperVersion ||
                descriptor.PaperVersion > _state.NewestPaperVersion ||
                descriptor.PackageLength <= 0 || descriptor.PackageLength > MaximumPackageBytes ||
                descriptor.InstructionsVersion < 1 ||
                string.IsNullOrWhiteSpace(descriptor.EncryptedPackageUri) ||
                (preparation.AuthorizedDeviceId is not null && preparation.AuthorizedDeviceId != _device.DeviceId))
                return PaperUpdateResult.Fail("服务器返回的新版资源描述不完整或版本异常。");
            if ((preparation.ParticipationId is not null && preparation.ParticipationId != currentParticipation.ParticipationId) ||
                (preparation.EntryVersion is not null && preparation.EntryVersion != currentParticipation.EntryVersion))
                return PaperUpdateResult.Fail("服务器返回的准备记录与当前参试不一致。");

            // 2) 考试说明版本变化时一并取新说明。
            InstructionsDocument? instructions = _state.Instructions;
            if (instructions is null || instructions.Version != descriptor.InstructionsVersion)
            {
                var document = await _api.GetInstructionsAsync(preparation.PreparationId, prepareRequest.SessionId, ct);
                ValidateServer(document);
                if (!document.IsSuccess || document.Result is not { } updated ||
                    updated.Version != descriptor.InstructionsVersion || updated.Lines is null ||
                    updated.Lines.Count == 0 || updated.Lines.Any(string.IsNullOrWhiteSpace))
                    return PaperUpdateResult.Fail("新版考试说明获取失败！");
                instructions = updated;
            }

            // 3) 原子下载新版密文（.part + 长度核对；旧版文件不受影响）。
            if (!await DownloadUpdatePackageAsync(descriptor, preparation.PreparationId,
                    prepareRequest.SessionId, progress, ct))
                return PaperUpdateResult.Fail(LastError ?? "新版试题下载失败！");

            // 4) 重新确认参试，取得新版解锁引用。
            var confirmRequest = new ConfirmParticipationRequest(RequestIds.New(), SessionId, ExamId,
                preparation.PreparationId, descriptor.PaperVersion, instructions.Version);
            var confirmed = await _api.ConfirmParticipationAsync(confirmRequest, ct);
            ValidateServer(confirmed);
            if (!confirmed.IsSuccess || confirmed.Result is not { } participation)
                return PaperUpdateResult.Fail($"试卷更新确认失败：{confirmed.Error?.Message ?? "服务器未确认"}");
            ValidateParticipation(participation);
            if (participation.ParticipationId != currentParticipation.ParticipationId ||
                participation.EntryVersion != currentParticipation.EntryVersion ||
                participation.AuthorizedDeviceId != _device.DeviceId ||
                participation.AuthorizationVersion < currentParticipation.AuthorizationVersion ||
                participation.BusinessState != ParticipationBusinessState.Answering ||
                string.IsNullOrWhiteSpace(participation.UnlockReference))
                return PaperUpdateResult.Fail("服务器返回的参试授权与当前作答状态不一致，不能切换试卷。");
            var ended = participation.Lifecycle == ExamLifecycle.Ended;
            if (ended)
                return PaperUpdateResult.Fail("考试已结束，不再应用试卷更新。");

            // 5) 解锁新版并解密、解析清单；解析失败不切换。
            var unlockRequest = new PackageUnlockRequest(RequestIds.New(),
                participation.ParticipationId, participation.EntryVersion, _device.DeviceId,
                participation.AuthorizationVersion, descriptor.PaperVersion, participation.UnlockReference,
                _state.ServerId, Server!.ServerEpoch);
            var unlock = await _api.ResolveUnlockAsync(unlockRequest, ct);
            using var material = unlock.Result;
            ValidateServer(unlock);
            if (!unlock.IsSuccess || material is null)
                return PaperUpdateResult.Fail($"新版试题解锁失败：{unlock.Error?.Message ?? "服务器未确认"}");
            if (material.PaperVersion != descriptor.PaperVersion ||
                !string.Equals(participation.UnlockReference, $"resource-envelope:{material.PackageId}", StringComparison.Ordinal))
                return PaperUpdateResult.Fail("新版解锁材料与试卷版本不一致，不能切换。");

            byte[]? newPayload = null;
            var protectedMaterial = _store.ProtectUnlock(material);
            try
            {
                await using var encrypted = new FileStream(_store.PackagePath(descriptor.PaperVersion), FileMode.Open,
                    FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var plaintext = new MemoryStream();
                await PackageCiphertext.DecryptAsync(encrypted, plaintext, material, ct);
                newPayload = plaintext.ToArray();
                // 先解析清单再切换：坏包在切换前被发现，旧版明文继续可用。
                _ = await Task.Run(() => StudentPaperPackage.ReadManifest(
                    newPayload, ExamId, descriptor.PaperVersion, ct), ct);
            }
            catch (CryptographicException)
            {
                ZeroIfNotNull(newPayload);
                return PaperUpdateResult.Fail("新版试题解密失败！密钥或题包认证不匹配，已保留当前版本。");
            }
            catch (EndOfStreamException)
            {
                ZeroIfNotNull(newPayload);
                return PaperUpdateResult.Fail("新版试题解密失败！题包不完整，已保留当前版本。");
            }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
            {
                ZeroIfNotNull(newPayload);
                return PaperUpdateResult.Fail($"新版试题清单校验失败：{error.Message} 已保留当前版本。");
            }

            // 6) 一次性切换：先提交明文（带代次核对），再保存控制状态。
            lock (_plaintextGate)
            {
                if (generation != _controlGeneration || ExamEnded || !_authorizedThisRun || _disposed)
                {
                    CryptographicOperations.ZeroMemory(newPayload);
                    return PaperUpdateResult.Fail("更新期间考试状态或授权发生变化，已保留当前版本，稍后重试。");
                }
                var previous = _unlockedPayload;
                _unlockedPayload = newPayload;
                if (previous is not null) CryptographicOperations.ZeroMemory(previous);
            }
            Save(_state with
            {
                PendingPrepare = null,
                Preparation = preparation,
                PreparationSessionId = prepareRequest.SessionId,
                Instructions = instructions,
                PendingConfirm = null,
                Participation = participation,
                CompletedPaperVersion = descriptor.PaperVersion,
                CompletedPackageUri = descriptor.EncryptedPackageUri,
                ProtectedUnlockMaterial = protectedMaterial,
                NewestPaperVersion = descriptor.PaperVersion
            });
            CurrentPhase = Phase.Unlocked;
            LastError = null;
            return PaperUpdateResult.Ok(descriptor.PaperVersion);
        }, ct);

    /// <summary>对已收到并确认（Received）的 PaperUpdated 事件补发更新结果：成功 Applied+实际版本，失败 ResourceUpdateFailed+原因。</summary>
    public Task<bool> ReportUpdateOutcomeAsync(
        string eventId, bool applied, long actualPaperVersion, string? failureReason, CancellationToken ct = default) =>
        LockedAsync(async () =>
        {
            if (Participation is not { } participation)
            {
                LastError = "参试登记尚未就绪，不能上报更新结果。";
                return false;
            }
            var acknowledgement = new AcknowledgeCommandRequest(
                RequestIds.New(), eventId, participation.ParticipationId, participation.EntryVersion,
                _device.DeviceId, participation.AuthorizationVersion,
                applied ? CommandProcessingResult.Applied : CommandProcessingResult.ResourceUpdateFailed,
                applied ? actualPaperVersion : null,
                applied ? null : failureReason);
            var response = await _api.AcknowledgeCommandAsync(acknowledgement, ct);
            ValidateServer(response);
            if (!response.IsSuccess || response.Result is not { } result)
            {
                RecordFailure(response.Error);
                return false;
            }
            if (result.EventId != eventId || result.RequestId != acknowledgement.RequestId ||
                result.ProcessingResult != acknowledgement.ProcessingResult)
                throw new InvalidDataException("服务器返回的指令确认与当前指令不一致。");
            return true;
        }, ct);

    private static void ZeroIfNotNull(byte[]? bytes)
    {
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
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
                await foreach (var chunk in _api.DownloadPackageChunksAsync(descriptor, preparationId, sessionId, ct))
                {
                    if (chunk.Length > descriptor.PackageLength - file.Length)
                        throw new InvalidDataException("试题下载数据超出声明的长度。");
                    await file.WriteAsync(chunk, ct);
                    progress?.Report(new PackageDownloadProgress(file.Length, descriptor.PackageLength));
                }
                if (file.Length != descriptor.PackageLength) throw new InvalidDataException("试题下载不完整。");
                file.Flush(flushToDisk: true);
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
        // A verified END stops visible content before waiting behind a long network operation.
        if (delivery.EventType == CommandEventType.End && MatchesCurrentAuthorization(delivery)) EndLocally();
        return LockedAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(delivery);
            if (delivery.EventType == CommandEventType.DeviceDecided && Participation is { } current &&
                delivery.Server.ServerId == _state.ServerId && delivery.Server.ServerEpoch >= (Server?.ServerEpoch ?? 1) &&
                delivery.ExamId == ExamId && delivery.ParticipationId == current.ParticipationId &&
                delivery.EntryVersion == current.EntryVersion && delivery.TargetDeviceId == _device.DeviceId &&
                delivery.TargetAuthorizationVersion > current.AuthorizationVersion)
            {
                _authorizedThisRun = false;
                ClearPlaintext();
                CurrentPhase = Phase.Revoked;
                Save(_state with { Revoked = true, ProtectedUnlockMaterial = null });
                LastError = "当前设备的授权版本已变化，请重新进入以核对服务器授权。";
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
                if (delivery.EventType == CommandEventType.Start && !ExamEnded &&
                    previous.Acknowledgement.ProcessingResult == CommandProcessingResult.Applied &&
                    UnlockedPayload is null && !await UnlockCoreAsync(ct)) return false;
                return await SendAcknowledgementAsync(previous, ct);
            }
            if (delivery.EventType == CommandEventType.Start)
            {
                if (ExamEnded || delivery.StateVersion < _state.LifecycleStateVersion)
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
                Save(_state with
                {
                    Started = true,
                    LifecycleStateVersion = delivery.StateVersion,
                    Participation = Participation! with
                    {
                        BusinessState = ParticipationBusinessState.Answering,
                        UnlockReference = delivery.ProtectedUnlockReference
                    }
                });
                if (!await UnlockCoreAsync(ct)) return false;
                return await RecordAndAcknowledgeAsync(delivery, CommandProcessingResult.Applied, ct);
            }
            if (delivery.EventType == CommandEventType.End)
            {
                EndLocally();
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
            if (delivery.EventType is CommandEventType.PaperUpdated or CommandEventType.ScheduleChanged)
            {
                Save(_state with
                {
                    NewestPaperVersion = delivery.EventType == CommandEventType.PaperUpdated
                        ? Math.Max(_state.NewestPaperVersion, delivery.ResourceVersion ?? delivery.StateVersion)
                        : _state.NewestPaperVersion,
                    LifecycleStateVersion = delivery.EventType == CommandEventType.ScheduleChanged
                        ? Math.Max(_state.LifecycleStateVersion, delivery.StateVersion) : _state.LifecycleStateVersion
                });
                LastError = delivery.EventType == CommandEventType.PaperUpdated
                    ? "已收到试卷更新，当前版本尚未完成更新，请重新同步。"
                    : "已收到考试时间更新，请重新同步考试信息。";
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
            throw new InvalidDataException("确认参试未返回可等待或恢复的参试记录。");
        var ended = _state.Ended || participation.Lifecycle == ExamLifecycle.Ended;
        Save(_state with
        {
            PendingConfirm = null,
            Participation = participation,
            Started = _state.Started || participation.BusinessState == ParticipationBusinessState.Answering,
            Ended = ended,
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
            if (unlock && participation.BusinessState == ParticipationBusinessState.Answering)
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
        if (_state.PendingWithdraw is null && (Participation is null || ExamStarted || ExamEnded ||
                Participation.BusinessState != ParticipationBusinessState.Waiting))
        {
            LastError = "当前状态不能退出参试，请等待服务器确认。";
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
            throw new InvalidDataException("退出响应未确认当前参试登记已经退出。");
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
                await foreach (var chunk in _api.DownloadPackageChunksAsync(descriptor, preparationId, sessionId, ct))
                {
                    if (chunk.Length > descriptor.PackageLength - file.Length)
                        throw new InvalidDataException("试题下载数据超出声明的长度。");
                    await file.WriteAsync(chunk, ct);
                    progress?.Report(new PackageDownloadProgress(file.Length, descriptor.PackageLength));
                }
                if (file.Length != descriptor.PackageLength) throw new InvalidDataException("试题下载不完整。");
                file.Flush(flushToDisk: true);
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
                Descriptor?.PaperVersion, result == CommandProcessingResult.Received ? LastError : null), false);
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
            acknowledgement.ProcessingResult != record.Acknowledgement.ProcessingResult)
            throw new InvalidDataException("服务器返回的指令确认与当前指令不一致。");
        if (!record.Acknowledged)
            Save(_state with
            {
                Commands = _state.Commands.Select(item => item.Delivery.EventId == record.Delivery.EventId
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
        first.ProtectedUnlockReference == next.ProtectedUnlockReference && first.Metadata.Count == next.Metadata.Count &&
        first.Metadata.All(pair => next.Metadata.TryGetValue(pair.Key, out var value) && pair.Value == value);
    private void ValidateParticipation(ParticipationResult participation)
    {
        if (participation.ExamId != ExamId || participation.CandidateId != _candidateId ||
            participation.AuthorizedDeviceId != _device.DeviceId || string.IsNullOrWhiteSpace(participation.ParticipationId) ||
            participation.EntryVersion < 1 || participation.AuthorizationVersion < 1 || participation.Version < 1)
            throw new InvalidDataException("服务器返回的参试身份或授权与当前考生不一致。");
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
            _authorizedThisRun = false;
            ClearPlaintext();
            CurrentPhase = Phase.Revoked;
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
    private void EndLocally() => StopImmediately(ended: true);
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
