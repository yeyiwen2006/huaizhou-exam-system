using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Resources;
using Exam.Contracts.Submissions;

namespace Exam.Student.Core;

public sealed partial class ExamPackageManager
{
    internal bool HasPendingPaperInstallation => _state.PendingPaperInstallation is not null;

    internal PaperUpdateInstallation? LastPaperInstallation => _state.LastPaperInstallation;

    internal IReadOnlyList<ProcessedPackageCommand> PendingPaperUpdates => _state.Commands.Where(item =>
        item.Delivery.EventType == CommandEventType.PaperUpdated && item.Delivery.ResourceVersion is > 0 &&
        MatchesCurrentAuthorization(item.Delivery) &&
        item.Acknowledgement.ProcessingResult != CommandProcessingResult.RejectedAsStale &&
        (!item.Acknowledged || item.Acknowledgement.ProcessingResult != CommandProcessingResult.Applied)).ToArray();

    internal bool IsRecordedPaperUpdate(CommandDelivery delivery) => delivery.EventType == CommandEventType.PaperUpdated &&
        delivery.ResourceVersion is > 0 && MatchesCurrentAuthorization(delivery) &&
        _state.Commands.Any(item => item.Delivery.EventId == delivery.EventId && SameCommand(item.Delivery, delivery));

    /// <summary>按版本查找已记录的 PaperUpdated 指令（含已确认），供复核横幅读取变更题目元数据。</summary>
    internal ProcessedPackageCommand? FindPaperUpdateCommand(long paperVersion) => _state.Commands
        .SingleOrDefault(item => item.Delivery.EventType == CommandEventType.PaperUpdated &&
            item.Delivery.ResourceVersion == paperVersion);

    private bool IsVerifiedDeviceDecision(CommandDelivery delivery) =>
        delivery.EventType == CommandEventType.DeviceDecided && _authorizedThisRun &&
        Participation is { } current && delivery.Server is { } server && delivery.Metadata is not null &&
        server.ServerId == _state.ServerId && !string.IsNullOrWhiteSpace(server.NodeId) &&
        server.ServerEpoch >= Math.Max(_device.ServerEpoch, Server?.ServerEpoch ?? 0) &&
        !string.IsNullOrWhiteSpace(delivery.EventId) && delivery.StateVersion > 0 &&
        delivery.ExamId == ExamId && delivery.ParticipationId == current.ParticipationId &&
        delivery.EntryVersion == current.EntryVersion && delivery.TargetDeviceId == _device.DeviceId &&
        delivery.TargetAuthorizationVersion >= current.AuthorizationVersion;

    /// <summary>只操作题包的边界；生产作答页面必须经运行时联合提交答案事务。</summary>
    public Task<PaperUpdateResult> ApplyPaperUpdateAsync(IProgress<PackageDownloadProgress>? progress = null,
        CancellationToken ct = default) => ApplyPaperUpdateCoreAsync(null, progress, ct);

    internal Task<PaperUpdateResult> ApplyPaperUpdateWithAnswersAsync(
        Func<PreparedPaperUpdate, CancellationToken, Task<AnswerChangeResult>> commit,
        IProgress<PackageDownloadProgress>? progress, CancellationToken ct) => ApplyPaperUpdateCoreAsync(commit, progress, ct);

    private Task<PaperUpdateResult> ApplyPaperUpdateCoreAsync(
        Func<PreparedPaperUpdate, CancellationToken, Task<AnswerChangeResult>>? commit,
        IProgress<PackageDownloadProgress>? progress, CancellationToken ct) => LockedAsync(async () =>
    {
        byte[]? candidatePayload = null;
        var published = false;
        try
        {
            if (CurrentPhase != Phase.Unlocked || UnlockedPayload is not { } previousPayload ||
                ExamEnded || _state.Revoked || !_authorizedThisRun || _state.PendingWithdraw is not null ||
                Descriptor is not { } previousDescriptor ||
                Participation is not { BusinessState: ParticipationBusinessState.Answering } previousParticipation)
                return PaperUpdateResult.Fail("当前答卷、考试或授权不允许更新，原记录保留。");
            if (_state.NewestPaperVersion <= previousDescriptor.PaperVersion)
                return PaperUpdateResult.Fail("当前没有待应用的试卷更新。");
            var generation = ControlGeneration;
            bool CanCommit() => generation == ControlGeneration && !ExamEnded && !_state.Revoked &&
                _authorizedThisRun && !_disposed && ReferenceEquals(UnlockedPayload, previousPayload);

            var transfer = _state.UpdateTransfer;
            if (transfer is null || transfer.SourcePaperVersion != previousDescriptor.PaperVersion ||
                transfer.ParticipationId != previousParticipation.ParticipationId ||
                transfer.EntryVersion != previousParticipation.EntryVersion ||
                transfer.AuthorizationVersion != previousParticipation.AuthorizationVersion)
            {
                transfer = new(new PrepareExamEntryRequest(RequestIds.New(), SessionId, ExamId),
                    previousParticipation.ParticipationId, previousParticipation.EntryVersion,
                    previousParticipation.AuthorizationVersion, previousDescriptor.PaperVersion, _state.NewestPaperVersion);
                Save(_state with { UpdateTransfer = transfer });
            }
            if (transfer.Preparation is null)
            {
                var prepared = await _api.PrepareEntryAsync(transfer.PrepareRequest, ct);
                ValidateServer(prepared);
                if (!prepared.IsSuccess || prepared.Result is not { } result)
                    return UpdateFailure(prepared.Error, "新版准备未完成。");
                if (result.Status == EntryPreparationStatus.DeviceDecisionPending)
                    return PaperUpdateResult.Fail("设备归属仍待裁决，未切换题包。");
                if (result.Status is not (EntryPreparationStatus.Ready or EntryPreparationStatus.Resume) ||
                    result.ExamId != ExamId || string.IsNullOrWhiteSpace(result.PreparationId) ||
                    result.Resources is not { } resource || resource.PaperVersion < transfer.RequestedVersion ||
                    resource.PaperVersion <= previousDescriptor.PaperVersion || resource.PackageLength <= 0 ||
                    resource.PackageLength > MaximumPackageBytes || resource.InstructionsVersion < 1 ||
                    string.IsNullOrWhiteSpace(resource.EncryptedPackageUri) ||
                    (result.AuthorizedDeviceId is not null && result.AuthorizedDeviceId != _device.DeviceId) ||
                    (result.ParticipationId is not null && result.ParticipationId != previousParticipation.ParticipationId) ||
                    (result.EntryVersion is not null && result.EntryVersion != previousParticipation.EntryVersion))
                    return PaperUpdateResult.Fail("新版准备回执的身份或资源版本不匹配，未切换题包。");
                transfer = transfer with { Preparation = result };
                Save(_state with { UpdateTransfer = transfer });
            }
            var preparation = transfer.Preparation;
            var descriptor = preparation.Resources!;
            if (!CanCommit()) return PaperUpdateResult.Fail("更新期间考试状态已变化，停止本次更新。");
            if (transfer.Instructions is null)
            {
                var instructions = _state.Instructions;
                if (instructions?.Version != descriptor.InstructionsVersion)
                {
                    var response = await _api.GetInstructionsAsync(preparation.PreparationId, transfer.PrepareRequest.SessionId, ct);
                    ValidateServer(response);
                    if (!response.IsSuccess || response.Result is not { } document)
                        return UpdateFailure(response.Error, "新版说明获取失败。");
                    instructions = document;
                }
                if (instructions is null || instructions.Version != descriptor.InstructionsVersion ||
                    instructions.Lines is null || instructions.Lines.Count == 0 || instructions.Lines.Any(string.IsNullOrWhiteSpace))
                    return PaperUpdateResult.Fail("新版说明内容不完整，未切换题包。");
                transfer = transfer with { Instructions = instructions };
                Save(_state with { UpdateTransfer = transfer });
            }
            if (!await DownloadUpdatePackageAsync(descriptor, preparation.PreparationId,
                    transfer.PrepareRequest.SessionId, progress, ct))
                return PaperUpdateResult.Fail(LastError ?? "新版下载失败，旧版题包保留。");
            if (!CanCommit()) return PaperUpdateResult.Fail("更新期间考试状态已变化，停止本次更新。");
            if (transfer.ConfirmRequest is null)
            {
                transfer = transfer with { ConfirmRequest = new(RequestIds.New(), transfer.PrepareRequest.SessionId,
                    ExamId, preparation.PreparationId, descriptor.PaperVersion, transfer.Instructions.Version) };
                Save(_state with { UpdateTransfer = transfer });
            }
            // 每次尝试均重放原确认请求，再用实时解锁请求核对当前授权，缓存回执不是新授权。
            var confirmed = await _api.ConfirmParticipationAsync(transfer.ConfirmRequest, ct);
            ValidateServer(confirmed);
            if (!confirmed.IsSuccess || confirmed.Result is not { } participation)
                return UpdateFailure(confirmed.Error, "新版确认未完成。");
            ValidateParticipation(participation);
            if (participation.Lifecycle == ExamLifecycle.Ended)
            {
                EndLocally();
                Save(_state with { Ended = true, ProtectedUnlockMaterial = null });
                return PaperUpdateResult.Fail("服务器确认考试已结束，已停止作答和题包更新。");
            }
            if (participation.ParticipationId != previousParticipation.ParticipationId ||
                participation.EntryVersion != previousParticipation.EntryVersion ||
                participation.AuthorizedDeviceId != _device.DeviceId ||
                participation.AuthorizationVersion != previousParticipation.AuthorizationVersion ||
                participation.BusinessState != ParticipationBusinessState.Answering)
                return UpdateFailure(new ApiError(ErrorCodes.AuthorizationRevoked,
                    "服务器返回的当前参加考试授权已变化。"), "已停止旧授权作答。");
            if (string.IsNullOrWhiteSpace(participation.UnlockReference))
                return PaperUpdateResult.Fail("新版缺少解锁引用，未切换题包。");
            if (transfer.UnlockRequest is null || transfer.UnlockRequest.ServerEpoch != Server!.ServerEpoch)
            {
                transfer = transfer with { Participation = participation, UnlockRequest = new(RequestIds.New(),
                    participation.ParticipationId, participation.EntryVersion, _device.DeviceId,
                    participation.AuthorizationVersion, descriptor.PaperVersion, participation.UnlockReference,
                    _state.ServerId, Server!.ServerEpoch) };
                Save(_state with { UpdateTransfer = transfer });
            }
            var unlock = await _api.ResolveUnlockAsync(transfer.UnlockRequest, ct);
            using var material = unlock.Result;
            ValidateServer(unlock);
            if (!unlock.IsSuccess || material is null) return UpdateFailure(unlock.Error, "新版解锁未完成。");
            if (material.PaperVersion != descriptor.PaperVersion ||
                participation.UnlockReference != $"resource-envelope:{material.PackageId}")
                return PaperUpdateResult.Fail("新版解锁材料与资源版本不匹配，旧包保留。");
            var protectedMaterial = _store.ProtectUnlock(material);
            await using (var encrypted = new FileStream(_store.PackagePath(descriptor.PaperVersion), FileMode.Open,
                FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var plaintext = new MemoryStream())
            {
                try
                {
                    await PackageCiphertext.DecryptAsync(encrypted, plaintext, material, ct);
                    candidatePayload = plaintext.ToArray();
                }
                finally
                {
                    if (plaintext.TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.AsSpan());
                }
            }
            var manifest = StudentPaperPackage.ReadManifest(candidatePayload, ExamId, descriptor.PaperVersion, ct);
            if (!CanCommit()) return PaperUpdateResult.Fail("更新期间考试状态已变化，未发布新版。");
            var previousManifest = StudentPaperPackage.ReadManifest(previousPayload, ExamId, previousDescriptor.PaperVersion, ct);
            if (!PaperUpdateCompatibility.SameManifestStructure(previousManifest, manifest))
                return PaperUpdateResult.Fail("新版改变了题目、选项、编号、题型或科目题组结构，不能在线替换。");
            var receivedAt = PendingPaperUpdates.Where(x => x.Delivery.ResourceVersion <= descriptor.PaperVersion)
                .Select(x => x.ReceivedAt ?? x.Delivery.CreatedAt).DefaultIfEmpty(DateTimeOffset.UtcNow).Min();
            var installation = new PaperUpdateInstallation(descriptor.PaperVersion, receivedAt, DateTimeOffset.UtcNow);
            var intent = new PendingPaperInstallation(previousDescriptor.PaperVersion, 0, "", preparation,
                transfer.PrepareRequest.SessionId, transfer.Instructions, participation, protectedMaterial, installation);
            void Publish()
            {
                // SQL 是答案版本的提交点。此后只发布内存，不再以第二次文件写入决定提交成败。
                _state = PromoteInstallation(_state, intent);
                lock (_plaintextGate)
                {
                    if (!CanCommit()) return; // END/撤权仍立即停止显示；已提交记录留给恢复和固定交卷。
                    _unlockedPayload = candidatePayload;
                    published = true;
                    CryptographicOperations.ZeroMemory(previousPayload);
                    CurrentPhase = Phase.Unlocked;
                }
            }
            if (commit is null)
            {
                Save(PromoteInstallation(_state, intent)); // 无答案的组件入口也必须先落盘，后销毁旧明文。
                Publish();
            }
            else
            {
                var result = await commit(new PreparedPaperUpdate(manifest, CanCommit,
                    (answer, snapshot) =>
                    {
                        if (answer.PaperVersion != descriptor.PaperVersion || answer.AnswerVersion <= 0 ||
                            answer.ParticipationId != participation.ParticipationId || answer.EntryVersion != participation.EntryVersion ||
                            answer.AuthorizationVersion != participation.AuthorizationVersion || answer.DeviceId != _device.DeviceId ||
                            string.IsNullOrWhiteSpace(snapshot.RequestId))
                            throw new InvalidDataException("更新恢复意图与答案身份不匹配。");
                        intent = intent with { MinimumAnswerVersion = answer.AnswerVersion, SnapshotRequestId = snapshot.RequestId };
                        Save(_state with { PendingPaperInstallation = intent });
                    }, Publish), ct);
                if (!result.Succeeded) return PaperUpdateResult.Fail(result.Error ?? "新版答案未保存，旧版未替换。");
            }
            if (!published) return PaperUpdateResult.Fail("更新提交期间考试结束或授权变化，已停止显示；持久记录保留。");
            LastError = null;
            return PaperUpdateResult.Ok(descriptor.PaperVersion);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or HttpRequestException or
            CryptographicException or JsonException or InvalidOperationException or ArgumentException or TimeoutException or
            OperationCanceledException)
        {
            LastError = "新版下载、验证或持久化未完成，旧版及已有恢复记录保留；请检查网络、授权和本地存储后重试。";
            return PaperUpdateResult.Fail(LastError);
        }
        finally { if (!published && candidatePayload is not null) CryptographicOperations.ZeroMemory(candidatePayload); }
    }, ct);

    private PaperUpdateResult UpdateFailure(ApiError? error, string message)
    {
        RecordFailure(error); // 撤权不能作为普通下载失败忽略。
        if (!Retryable(error)) Save(_state with { UpdateTransfer = null });
        return PaperUpdateResult.Fail(error?.Code == ErrorCodes.AuthorizationRevoked ?
            "服务器已撤销当前授权，已停止作答；本地原答案保留。" : message);
    }

    private static PackageControlState PromoteInstallation(PackageControlState state, PendingPaperInstallation intent) => state with
    {
        PendingPrepare = null, PendingConfirm = null, UpdateTransfer = null, PendingPaperInstallation = null,
        Preparation = intent.Preparation, PreparationSessionId = intent.SessionId, Instructions = intent.Instructions,
        Participation = intent.Participation, CompletedPaperVersion = intent.Installation.PaperVersion,
        CompletedPackageUri = intent.Preparation.Resources!.EncryptedPackageUri,
        ProtectedUnlockMaterial = state.Revoked || state.Ended ? null : intent.ProtectedUnlockMaterial,
        NewestPaperVersion = Math.Max(state.NewestPaperVersion, intent.Installation.PaperVersion),
        LastPaperInstallation = intent.Installation
    };

    internal Task<bool> RecoverPendingPaperInstallationAsync(LocalStateKey key, AnswerStateRecord? answer,
        PendingSnapshot? snapshot, CancellationToken ct) => LockedAsync(() =>
    {
        if (_state.PendingPaperInstallation is not { } intent) return Task.FromResult(true);
        if (key.ServerKey != _state.ServerId || key.ExamId != ExamId || key.CandidateId != CandidateId || answer is null ||
            intent.Preparation is null || intent.Participation is null || intent.Installation is null ||
            intent.Preparation.ExamId != ExamId || intent.Participation.CandidateId != CandidateId ||
            intent.Participation.ExamId != ExamId || intent.Participation.AuthorizedDeviceId != _device.DeviceId ||
            intent.MinimumAnswerVersion <= 0 || intent.PreviousPaperVersion <= 0 ||
            intent.Installation.PaperVersion <= intent.PreviousPaperVersion ||
            intent.Preparation.Resources?.PaperVersion != intent.Installation.PaperVersion ||
            intent.Instructions is null || intent.Instructions.Version != intent.Preparation.Resources.InstructionsVersion ||
            intent.ProtectedUnlockMaterial is not { Length: > 0 } || string.IsNullOrWhiteSpace(intent.SnapshotRequestId) ||
            answer.ParticipationId != intent.Participation.ParticipationId || answer.EntryVersion != intent.Participation.EntryVersion ||
            answer.AuthorizationVersion != intent.Participation.AuthorizationVersion || answer.DeviceId != _device.DeviceId)
            throw new InvalidDataException("题包恢复意图和本地答案的身份或版本不一致，未猜测或覆盖原记录。");
        if (answer.PaperVersion == intent.Installation.PaperVersion && answer.AnswerVersion >= intent.MinimumAnswerVersion &&
            (answer.AnswerVersion > intent.MinimumAnswerVersion || snapshot?.RequestId == intent.SnapshotRequestId))
            Save(PromoteInstallation(_state, intent));
        else if (answer.PaperVersion == intent.PreviousPaperVersion)
            Save(_state with { PendingPaperInstallation = null }); // SQL 未提交，旧包仍是当前版本。
        else throw new InvalidDataException("更新提交结果无法与答案事务核对，保留原记录并停止恢复。");
        // 本地提交记录不授予本次运行的访问权；仍须正常准备、确认并在线解锁。
        return Task.FromResult(true);
    }, ct);

    internal Task<PaperUpdateInstallation?> EnsurePaperInstallationAsync(long version, DateTimeOffset receivedAt,
        CancellationToken ct) => LockedAsync(() =>
    {
        if (CurrentPhase != Phase.Unlocked || Descriptor?.PaperVersion != version || UnlockedPayload is null || ExamEnded)
            return Task.FromResult<PaperUpdateInstallation?>(null);
        if (_state.LastPaperInstallation is not { } installation || installation.PaperVersion != version)
        {
            installation = new(version, receivedAt, DateTimeOffset.UtcNow);
            Save(_state with { LastPaperInstallation = installation });
        }
        return Task.FromResult<PaperUpdateInstallation?>(installation);
    }, ct);

    /// <summary>只有本机已验证并记录的更新事件才可推进结果；先记录稳定请求，再发送并核对完整回执。</summary>
    public Task<bool> ReportUpdateOutcomeAsync(string eventId, bool applied, long actualPaperVersion,
        string? failureReason, CancellationToken ct = default) => LockedAsync(async () =>
    {
        try
        {
            var record = _state.Commands.SingleOrDefault(item => item.Delivery.EventId == eventId);
            if (record is null || record.Delivery.EventType != CommandEventType.PaperUpdated ||
                record.Delivery.ResourceVersion is not > 0 || !MatchesCurrentAuthorization(record.Delivery)) return false;
            if (record.Acknowledgement.ProcessingResult == CommandProcessingResult.Applied)
                return record.Acknowledged || await SendAcknowledgementAsync(record, ct);
            if (applied && (CurrentPhase != Phase.Unlocked || ExamEnded || UnlockedPayload is null ||
                Descriptor?.PaperVersion != actualPaperVersion || _state.CompletedPaperVersion != actualPaperVersion ||
                actualPaperVersion < record.Delivery.ResourceVersion)) return false;
            var result = applied ? CommandProcessingResult.Applied : CommandProcessingResult.ResourceUpdateFailed;
            long? version = applied ? actualPaperVersion : null;
            var reason = applied ? null : (string.IsNullOrWhiteSpace(failureReason) ? "新版未完成，原记录保留。" : failureReason);
            if (record.Acknowledgement.ProcessingResult != result || record.Acknowledgement.ActualPaperVersion != version ||
                record.Acknowledgement.FailureReason != reason)
            {
                record = record with { Acknowledged = false, Acknowledgement = record.Acknowledgement with
                { RequestId = RequestIds.New(), ProcessingResult = result, ActualPaperVersion = version, FailureReason = reason } };
                Save(_state with { Commands = _state.Commands.Select(item => item.Delivery.EventId == eventId ? record : item).ToArray() });
            }
            return record.Acknowledged || await SendAcknowledgementAsync(record, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or InvalidOperationException or
            UnauthorizedAccessException or JsonException or TimeoutException or OperationCanceledException)
        {
            LastError = "更新结果尚未取得匹配回执，保留原请求标识并重试。";
            return false;
        }
    }, ct);
}
