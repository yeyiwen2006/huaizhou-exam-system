using System.Security.Cryptography;
using System.Text.Json;
using Exam.Application.Activation;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Domain.Participation;

namespace Exam.Application.Participation;

public sealed class ParticipationService(
    IParticipationPersistence persistence,
    IDeviceCredentialValidator deviceCredentialValidator,
    ICandidateRosterDirectory rosterDirectory,
    IExamEntryDirectory examDirectory,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan EntryWindowBeforeStart = TimeSpan.FromMinutes(20);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async ValueTask<StudentSessionResult> LoginAsync(
        LoginStudentRequest request,
        CancellationToken cancellationToken = default)
        => (await LoginCoreAsync(request, null, cancellationToken).ConfigureAwait(false)).Session;

    public async ValueTask<ConnectedStudentSessionResult> ConnectAndLoginAsync(
        LoginStudentRequest request, ActivationService connections,
        CancellationToken cancellationToken = default)
    {
        var result = await LoginCoreAsync(request, connections, cancellationToken).ConfigureAwait(false);
        return new(result.Session, result.Device!);
    }

    private async ValueTask<(StudentSessionResult Session, Exam.Contracts.Activation.ActivationResult? Device)> LoginCoreAsync(
        LoginStudentRequest request, ActivationService? connections, CancellationToken cancellationToken)
    {
        ValidateRequestId(request.RequestId);
        Require(request.DeviceId, nameof(request.DeviceId));
        Require(request.Credential, nameof(request.Credential));
        Require(request.Name, nameof(request.Name));
        Require(request.ClassText, nameof(request.ClassText));
        Require(request.AdmissionNo, nameof(request.AdmissionNo));
        RequireAsciiDigits(request.ClassText, nameof(request.ClassText));
        RequireAsciiDigits(request.AdmissionNo, nameof(request.AdmissionNo));

        if (connections is null && !await deviceCredentialValidator.ValidateActiveDeviceAsync(
                request.DeviceId,
                request.Credential,
                cancellationToken).ConfigureAwait(false))
        {
            throw Error(ParticipationErrorCodes.NotActivated, "设备未激活或设备凭据无效。");
        }

        var matches = await rosterDirectory.FindMatchesAsync(
            request.Name,
            request.ClassText,
            request.AdmissionNo,
            cancellationToken).ConfigureAwait(false);
        if (matches.Count == 0)
        {
            throw Error(ParticipationErrorCodes.RosterMismatch, "姓名、班级和准考证号未同时匹配任何考试名册。");
        }

        var now = _timeProvider.GetUtcNow();
        var candidate = matches[0].Candidate;
        var examIds = matches.Select(match => match.ExamId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var matchesByExam = matches
            .GroupBy(match => match.ExamId, StringComparer.Ordinal)
            .ToArray();
        if (matchesByExam.Any(group =>
                group.Select(match => match.Candidate.CandidateId).Distinct(StringComparer.Ordinal).Skip(1).Any()))
        {
            throw Error(ParticipationErrorCodes.RosterMismatch, "同一考试中匹配到多个名册身份。");
        }

        var candidateIdsByExam = matchesByExam
            .ToDictionary(
                group => group.Key,
                group => group.First().Candidate.CandidateId,
                StringComparer.Ordinal);
        // Invalid roster input never creates a device or consumes a daily number.
        var device = connections is null ? null : await connections.ConnectAsync(
            request.DeviceId, request.Credential, cancellationToken).ConfigureAwait(false);
        var result = await persistence.ExecuteTransactionAsync(transaction =>
        {
            EnsureActiveDevice(transaction, request.DeviceId);
            return RunIdempotent(transaction, request.RequestId, "participation.login", request, () =>
            {
                var session = new StudentSession(
                    Guid.NewGuid().ToString("N"),
                    request.DeviceId,
                    candidate,
                    examIds,
                    now,
                    candidateIdsByExam);
                transaction.SaveSession(session);
                return ToSessionResult(session);
            });
        }, cancellationToken).ConfigureAwait(false);
        return (result, device);
    }

    public async ValueTask<EntryPreparationResult> PrepareEntryAsync(
        PrepareExamEntryRequest request,
        CancellationToken cancellationToken = default)
        => await PrepareEntryCoreAsync(request, null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<EntryPreparationResult> PrepareEntryAsync(
        PrepareExamEntryRequest request,
        string authenticatedDeviceId,
        CancellationToken cancellationToken = default)
        => await PrepareEntryCoreAsync(request, authenticatedDeviceId, cancellationToken).ConfigureAwait(false);

    private async ValueTask<EntryPreparationResult> PrepareEntryCoreAsync(
        PrepareExamEntryRequest request,
        string? authenticatedDeviceId,
        CancellationToken cancellationToken)
    {
        ValidateRequestId(request.RequestId);
        Require(request.SessionId, nameof(request.SessionId));
        Require(request.ExamId, nameof(request.ExamId));

        var session = await GetAuthorizedSessionAsync(request.SessionId, request.ExamId, cancellationToken)
            .ConfigureAwait(false);
        EnsureAuthenticatedDeviceOwnsSession(authenticatedDeviceId, session);
        var exam = await GetExamAsync(request.ExamId, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();

        return await persistence.ExecuteTransactionAsync(transaction =>
        {
            EnsureCurrentSession(transaction, session);
            EnsureCurrentExam(transaction, exam);
            // Acquire the request lock before the participation lock, as the other
            // mutation paths do, even though replay must recheck current authority.
            transaction.FindIdempotencyRecord(request.RequestId);
            var candidateId = CandidateIdForExam(session, request.ExamId);
            var participation = transaction.FindParticipation(request.ExamId, candidateId);
            if (participation is { BusinessState: ParticipationBusinessState.Submitted })
            {
                throw Error(ParticipationErrorCodes.AlreadySubmitted, "该考生已经交卷，不能再次进入考试。");
            }

            EnsureExamAllowedNow(exam, _timeProvider.GetUtcNow(), IsActualParticipation(participation));
            var result = RunIdempotent(transaction, request.RequestId, "participation.prepare", request, () =>
            {
                if (participation is not null &&
                    !StringComparer.Ordinal.Equals(participation.AuthorizedDeviceId, session.DeviceId))
                {
                    var conflict = transaction.FindConflict(participation.ParticipationId);
                    if (conflict is not null && !StringComparer.Ordinal.Equals(conflict.PendingDeviceId, session.DeviceId))
                    {
                        throw Error(ParticipationErrorCodes.DeviceDecisionPending, "已有另一台设备等待裁决。");
                    }

                    conflict ??= new DeviceConflict(
                        participation.ParticipationId,
                        participation.AuthorizedDeviceId,
                        session.DeviceId,
                        now);
                    transaction.SaveConflict(conflict);
                    return new EntryPreparationResult(
                        $"conflict:{participation.ParticipationId}:{session.DeviceId}",
                        request.ExamId,
                        EntryPreparationStatus.DeviceDecisionPending,
                        null,
                        participation.ParticipationId,
                        participation.AuthorizedDeviceId,
                        session.DeviceId,
                        participation.EntryVersion,
                        participation.AuthorizationVersion,
                        now);
                }

                var preparation = new EntryPreparation(
                    Guid.NewGuid().ToString("N"),
                    session.SessionId,
                    request.ExamId,
                    candidateId,
                    session.DeviceId,
                    exam.PaperVersion,
                    exam.InstructionsVersion,
                    now);
                transaction.SavePreparation(preparation);
                return new EntryPreparationResult(
                    preparation.PreparationId,
                    request.ExamId,
                    participation is null || participation.BusinessState == ParticipationBusinessState.NotParticipating
                        ? EntryPreparationStatus.Ready
                        : EntryPreparationStatus.Resume,
                    ToResources(exam),
                    participation?.ParticipationId,
                    participation?.AuthorizedDeviceId,
                    null,
                    participation?.EntryVersion,
                    participation?.AuthorizationVersion,
                    now);
            });

            // A saved preparation receipt is not an authorization token. A device that
            // lost authorization after that request must start the device-decision flow.
            if (participation is not null &&
                !StringComparer.Ordinal.Equals(participation.AuthorizedDeviceId, session.DeviceId) &&
                result.Status != EntryPreparationStatus.DeviceDecisionPending)
            {
                throw Error(ParticipationErrorCodes.AuthorizationRevoked, "设备授权已变化，请重新申请进入并等待裁决。");
            }

            return result;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ParticipationResult> ConfirmParticipationAsync(
        ConfirmParticipationRequest request,
        CancellationToken cancellationToken = default)
        => await ConfirmParticipationCoreAsync(request, null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ParticipationResult> ConfirmParticipationAsync(
        ConfirmParticipationRequest request,
        string authenticatedDeviceId,
        CancellationToken cancellationToken = default)
        => await ConfirmParticipationCoreAsync(request, authenticatedDeviceId, cancellationToken).ConfigureAwait(false);

    private async ValueTask<ParticipationResult> ConfirmParticipationCoreAsync(
        ConfirmParticipationRequest request,
        string? authenticatedDeviceId,
        CancellationToken cancellationToken)
    {
        ValidateRequestId(request.RequestId);
        Require(request.SessionId, nameof(request.SessionId));
        Require(request.ExamId, nameof(request.ExamId));
        Require(request.PreparationId, nameof(request.PreparationId));

        var session = await GetAuthorizedSessionAsync(request.SessionId, request.ExamId, cancellationToken)
            .ConfigureAwait(false);
        EnsureAuthenticatedDeviceOwnsSession(authenticatedDeviceId, session);
        var exam = await GetExamAsync(request.ExamId, cancellationToken).ConfigureAwait(false);

        return await persistence.ExecuteTransactionAsync(transaction =>
        {
            EnsureCurrentSession(transaction, session);
            EnsureCurrentExam(transaction, exam);
            transaction.FindIdempotencyRecord(request.RequestId);
            var candidateId = CandidateIdForExam(session, request.ExamId);
            var participation = transaction.FindParticipation(request.ExamId, candidateId);
            if (participation is { BusinessState: ParticipationBusinessState.Submitted })
            {
                throw Error(ParticipationErrorCodes.AlreadySubmitted, "该考生已经交卷，不能再次参加考试。");
            }

            if (participation is not null &&
                !StringComparer.Ordinal.Equals(participation.AuthorizedDeviceId, session.DeviceId))
            {
                throw Error(ParticipationErrorCodes.DeviceDecisionPending, "当前设备须等待考务人员裁决。");
            }

            EnsureExamAllowedNow(exam, _timeProvider.GetUtcNow(), IsActualParticipation(participation));
            var result = RunIdempotent(transaction, request.RequestId, "participation.confirm", request, () =>
            {
                var preparation = transaction.FindPreparation(request.PreparationId)
                    ?? throw Error(ParticipationErrorCodes.PreparationNotFound, "找不到有效的进入准备记录。");
                if (!StringComparer.Ordinal.Equals(preparation.SessionId, session.SessionId) ||
                    !StringComparer.Ordinal.Equals(preparation.ExamId, request.ExamId) ||
                    !StringComparer.Ordinal.Equals(preparation.CandidateId, candidateId) ||
                    !StringComparer.Ordinal.Equals(preparation.DeviceId, session.DeviceId))
                {
                    throw Error(ParticipationErrorCodes.PreparationNotFound, "进入准备记录不属于当前会话。");
                }

                if (request.DownloadedPaperVersion != preparation.PaperVersion ||
                    request.DownloadedInstructionsVersion != preparation.InstructionsVersion ||
                    exam.PaperVersion != preparation.PaperVersion ||
                    exam.InstructionsVersion != preparation.InstructionsVersion)
                {
                    throw Error(ParticipationErrorCodes.PreparationVersionMismatch, "下载的试题包或考试说明已不是当前版本。");
                }

                var state = CurrentLifecycle(exam, _timeProvider.GetUtcNow()) == ExamLifecycle.Running
                    ? ParticipationBusinessState.Answering
                    : ParticipationBusinessState.Waiting;
                if (participation is null)
                {
                    participation = new ParticipationRecord(
                        Guid.NewGuid().ToString("N"),
                        request.ExamId,
                        candidateId,
                        1,
                        state,
                        session.DeviceId,
                        1,
                        1);
                    transaction.SaveParticipation(participation);
                }
                else if (participation.BusinessState == ParticipationBusinessState.NotParticipating)
                {
                    participation = participation.Reenter(session.DeviceId, state);
                    transaction.SaveParticipation(participation);
                }
                else if (state == ParticipationBusinessState.Answering &&
                         participation.BusinessState == ParticipationBusinessState.Waiting)
                {
                    participation = participation with
                    {
                        BusinessState = ParticipationBusinessState.Answering,
                        Version = checked(participation.Version + 1)
                    };
                    transaction.SaveParticipation(participation);
                }

                return ToResult(participation, null);
            });

            // Idempotency prevents repeating the business mutation, not observing new
            // lifecycle/authorization facts. Never replay a stale unlock after the end.
            var current = transaction.FindParticipation(result.ParticipationId)
                ?? throw Error(ParticipationErrorCodes.AuthorizationRevoked, "参加考试登记已不存在。");
            var lifecycle = CurrentLifecycle(exam, _timeProvider.GetUtcNow());
            return ToResult(current,
                lifecycle == ExamLifecycle.Running && current.BusinessState == ParticipationBusinessState.Answering
                    ? exam.UnlockReference : null) with
            {
                Lifecycle = lifecycle,
                ScheduleVersion = exam.ScheduleVersion,
                ReopenedAtScheduleVersion = exam.ReopenedAtScheduleVersion,
                ScheduledStart = exam.ScheduledStart,
                ScheduledEnd = exam.ScheduledEnd
            };
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ParticipationResult> WithdrawAsync(
        WithdrawParticipationRequest request,
        CancellationToken cancellationToken = default)
        => await WithdrawCoreAsync(request, null, cancellationToken).ConfigureAwait(false);

    public async ValueTask<ParticipationResult> WithdrawAsync(
        WithdrawParticipationRequest request,
        string authenticatedDeviceId,
        CancellationToken cancellationToken = default)
        => await WithdrawCoreAsync(request, authenticatedDeviceId, cancellationToken).ConfigureAwait(false);

    private async ValueTask<ParticipationResult> WithdrawCoreAsync(
        WithdrawParticipationRequest request,
        string? authenticatedDeviceId,
        CancellationToken cancellationToken)
    {
        ValidateRequestId(request.RequestId);
        Require(request.SessionId, nameof(request.SessionId));
        Require(request.ParticipationId, nameof(request.ParticipationId));
        var session = await GetActiveSessionAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        EnsureAuthenticatedDeviceOwnsSession(authenticatedDeviceId, session);

        return await persistence.ExecuteTransactionAsync(transaction =>
        {
            EnsureCurrentSession(transaction, session);
            return RunIdempotent(transaction, request.RequestId, "participation.withdraw", request, () =>
            {
                var participation = transaction.FindParticipation(request.ParticipationId)
                    ?? throw Error(ParticipationErrorCodes.InvalidBusinessState, "找不到参加考试记录。");
                EnsureSessionOwnsParticipation(session, participation);
                if (participation.BusinessState == ParticipationBusinessState.NotParticipating)
                {
                    return ToResult(participation, null);
                }

                EnsureVersion(participation, request.ExpectedVersion);
                if (participation.BusinessState != ParticipationBusinessState.Waiting)
                {
                    throw Error(ParticipationErrorCodes.InvalidBusinessState, "只有等待考试状态可以退出考试。");
                }

                var withdrawn = participation.Withdraw();
                transaction.SaveParticipation(withdrawn);
                transaction.RemoveConflict(participation.ParticipationId);
                return ToResult(withdrawn, null);
            });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DeviceDecisionResult> DecideDeviceAsync(
        DeviceDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestId(request.RequestId);
        Require(request.ParticipationId, nameof(request.ParticipationId));
        Require(request.SelectedDeviceId, nameof(request.SelectedDeviceId));
        Require(request.OperatorId, nameof(request.OperatorId));
        Require(request.Reason, nameof(request.Reason));
        var now = _timeProvider.GetUtcNow();
        if (!await deviceCredentialValidator.IsActiveDeviceAsync(request.SelectedDeviceId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw Error(ParticipationErrorCodes.NotActivated, "被选择的设备未处于有效激活状态。");
        }

        return await persistence.ExecuteTransactionAsync(transaction =>
        {
            EnsureActiveDevice(transaction, request.SelectedDeviceId);
            return RunIdempotent(transaction, request.RequestId, "participation.device-decision", request, () =>
            {
                var participation = transaction.FindParticipation(request.ParticipationId)
                    ?? throw Error(ParticipationErrorCodes.InvalidBusinessState, "找不到参加考试记录。");
                if (participation.BusinessState == ParticipationBusinessState.Submitted)
                {
                    throw Error(ParticipationErrorCodes.AlreadySubmitted, "已交卷记录不能更换授权设备。");
                }

                EnsureVersion(participation, request.ExpectedVersion);
                var conflict = transaction.FindConflict(participation.ParticipationId)
                    ?? throw Error(ParticipationErrorCodes.InvalidBusinessState, "当前没有待裁决的设备冲突。");
                if (!StringComparer.Ordinal.Equals(request.SelectedDeviceId, conflict.OriginalDeviceId) &&
                    !StringComparer.Ordinal.Equals(request.SelectedDeviceId, conflict.PendingDeviceId))
                {
                    throw Error(ParticipationErrorCodes.InvalidRequest, "选择的设备不属于当前冲突。");
                }

                var revokedDeviceId = StringComparer.Ordinal.Equals(request.SelectedDeviceId, conflict.OriginalDeviceId)
                    ? conflict.PendingDeviceId
                    : conflict.OriginalDeviceId;
                var decided = participation.DecideDevice(request.SelectedDeviceId);
                var audit = new DeviceDecisionAudit(
                    Guid.NewGuid().ToString("N"),
                    participation.ParticipationId,
                    request.SelectedDeviceId,
                    revokedDeviceId,
                    decided.AuthorizationVersion,
                    request.OperatorId,
                    request.Reason,
                    now);
                transaction.SaveParticipation(decided);
                transaction.RemoveConflict(participation.ParticipationId);
                transaction.SaveDecisionAudit(audit);
                return new DeviceDecisionResult(
                    decided.ParticipationId,
                    decided.AuthorizedDeviceId,
                    revokedDeviceId,
                    decided.AuthorizationVersion,
                    decided.Version,
                    audit.OperatorId,
                    audit.Reason,
                    audit.DecidedAt);
            });
        }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ParticipationResult> EnsureAuthorizedForWriteAsync(
        AuthorizationCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await deviceCredentialValidator.IsActiveDeviceAsync(request.DeviceId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw Error(ParticipationErrorCodes.NotActivated, "设备激活已经失效。");
        }

        return await persistence.ExecuteTransactionAsync(transaction =>
        {
            EnsureActiveDevice(transaction, request.DeviceId);
            var participation = transaction.FindParticipation(request.ParticipationId)
                ?? throw Error(ParticipationErrorCodes.AuthorizationRevoked, "参加考试授权不存在。");
            EnsureAuthorization(participation, request.DeviceId, request.EntryVersion, request.AuthorizationVersion);
            if (participation.BusinessState == ParticipationBusinessState.Submitted)
            {
                throw Error(ParticipationErrorCodes.AlreadySubmitted, "该参加考试记录已经交卷。");
            }

            if (participation.BusinessState is ParticipationBusinessState.NotParticipating)
            {
                throw Error(ParticipationErrorCodes.AuthorizationRevoked, "当前登记已退出考试。");
            }

            return ToResult(participation, null);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// State-machine reference operation for isolated participation tests only.
    /// Do not expose this method through an API or CLI: production submission must
    /// persist the final answers, receipt and submitted state in the single submission
    /// submission transaction reached through /api/v1/submissions/final.
    /// </summary>
    public async ValueTask<ParticipationResult> FinalizeAsync(
        FinalizeParticipationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestId(request.RequestId);
        Require(request.ParticipationId, nameof(request.ParticipationId));
        Require(request.DeviceId, nameof(request.DeviceId));
        if (!await deviceCredentialValidator.IsActiveDeviceAsync(request.DeviceId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw Error(ParticipationErrorCodes.NotActivated, "设备激活已经失效。");
        }

        return await persistence.ExecuteTransactionAsync(transaction =>
        {
            EnsureActiveDevice(transaction, request.DeviceId);
            return RunIdempotent(transaction, request.RequestId, "participation.finalize", request, () =>
            {
                var participation = transaction.FindParticipation(request.ParticipationId)
                    ?? throw Error(ParticipationErrorCodes.AuthorizationRevoked, "参加考试授权不存在。");
                EnsureAuthorization(
                    participation,
                    request.DeviceId,
                    request.EntryVersion,
                    request.AuthorizationVersion);
                if (participation.BusinessState == ParticipationBusinessState.Submitted)
                {
                    return ToResult(participation, null);
                }

                if (participation.BusinessState is ParticipationBusinessState.NotParticipating)
                {
                    throw Error(ParticipationErrorCodes.AuthorizationRevoked, "已退出登记不能交卷。");
                }

                if (participation.BusinessState != ParticipationBusinessState.Answering)
                {
                    throw Error(ParticipationErrorCodes.InvalidBusinessState, "只有正在答题的参加考试记录可以交卷。");
                }

                var submitted = participation.MarkSubmitted();
                transaction.SaveParticipation(submitted);
                transaction.RemoveConflict(participation.ParticipationId);
                return ToResult(submitted, null);
            });
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// reliable command calls this when the persisted START transition wins. Waiting-state
    /// advancement and waiting-page withdrawal use the same participation transaction,
    /// so their race has one deterministic serialized winner.
    /// </summary>
    public ValueTask<ExamStartTransitionResult> AdvanceExamToAnsweringAsync(
        AdvanceExamToAnsweringRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequestId(request.RequestId);
        Require(request.ExamId, nameof(request.ExamId));
        var now = _timeProvider.GetUtcNow();
        return persistence.ExecuteTransactionAsync(transaction =>
            RunIdempotent(transaction, request.RequestId, "participation.start-exam", request, () =>
            {
                var changed = 0;
                foreach (var participation in transaction.ListParticipations(request.ExamId))
                {
                    if (participation.BusinessState != ParticipationBusinessState.Waiting)
                    {
                        continue;
                    }

                    transaction.SaveParticipation(participation with
                    {
                        BusinessState = ParticipationBusinessState.Answering,
                        Version = checked(participation.Version + 1)
                    });
                    changed++;
                }

                return new ExamStartTransitionResult(request.ExamId, changed, now);
            }), cancellationToken);
    }

    public async ValueTask<ParticipationResult?> FindParticipationAsync(
        string participationId,
        CancellationToken cancellationToken = default)
    {
        var result = await persistence.ExecuteTransactionAsync(
            transaction => transaction.FindParticipation(participationId) is { } participation
                ? ToResult(participation, null)
                : null,
            cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        var exam = await examDirectory.FindAsync(result.ExamId, cancellationToken).ConfigureAwait(false);
        return result with
        {
            Lifecycle = exam is null ? null : CurrentLifecycle(exam, _timeProvider.GetUtcNow()),
            ScheduleVersion = exam?.ScheduleVersion ?? 0,
            ReopenedAtScheduleVersion = exam?.ReopenedAtScheduleVersion ?? 0,
            ScheduledStart = exam?.ScheduledStart,
            ScheduledEnd = exam?.ScheduledEnd
        };
    }

    public ValueTask<IReadOnlyList<ParticipationResult>> ListActualParticipationsAsync(
        string examId,
        CancellationToken cancellationToken = default)
    {
        Require(examId, nameof(examId));
        return persistence.ExecuteTransactionAsync<IReadOnlyList<ParticipationResult>>(
            transaction => transaction.ListParticipations(examId)
                .Where(participation => participation.BusinessState != ParticipationBusinessState.NotParticipating)
                .Select(participation => ToResult(participation, null))
                .ToArray(),
            cancellationToken);
    }

    private async ValueTask<StudentSession> GetAuthorizedSessionAsync(
        string sessionId,
        string examId,
        CancellationToken cancellationToken)
    {
        var session = await GetActiveSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        var candidateId = CandidateIdForExam(session, examId);

        if (!session.ExamIds.Contains(examId, StringComparer.Ordinal) ||
            !await rosterDirectory.IsRegisteredAsync(examId, candidateId, cancellationToken)
                .ConfigureAwait(false))
        {
            throw Error(ParticipationErrorCodes.RosterMismatch, "当前登录身份不在该考试名册中。");
        }

        return session;
    }

    private async ValueTask<StudentSession> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken) =>
        await persistence.ExecuteTransactionAsync(
            transaction => transaction.FindSession(sessionId)
                ?? throw Error(ParticipationErrorCodes.SessionNotFound, "登录会话不存在。"),
            cancellationToken).ConfigureAwait(false);

    private async ValueTask<StudentSession> GetActiveSessionAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (!await deviceCredentialValidator.IsActiveDeviceAsync(session.DeviceId, cancellationToken).ConfigureAwait(false))
        {
            throw Error(ParticipationErrorCodes.NotActivated, "设备激活已经失效。");
        }

        return session;
    }

    private async ValueTask<ExamEntrySnapshot> GetExamAsync(
        string examId,
        CancellationToken cancellationToken)
    {
        var exam = await examDirectory.FindAsync(examId, cancellationToken).ConfigureAwait(false)
            ?? throw Error(ParticipationErrorCodes.ExamNotFound, "找不到考试。");
        return exam;
    }

    private static void EnsureCurrentSession(
        IParticipationTransaction transaction,
        StudentSession expected)
    {
        EnsureActiveDevice(transaction, expected.DeviceId);
        var current = transaction.FindSession(expected.SessionId);
        if (current is null ||
            !string.Equals(current.DeviceId, expected.DeviceId, StringComparison.Ordinal))
        {
            throw Error(ParticipationErrorCodes.AuthorizationRevoked, "登录会话已失效或不再属于当前设备。");
        }
    }

    private static void EnsureActiveDevice(IParticipationTransaction transaction, string deviceId)
    {
        if (!transaction.IsActiveDevice(deviceId))
        {
            throw Error(ParticipationErrorCodes.NotActivated, "设备激活已经失效。");
        }
    }

    private static void EnsureCurrentExam(
        IParticipationTransaction transaction,
        ExamEntrySnapshot exam)
    {
        if (!transaction.IsExamEntryCurrent(exam))
        {
            throw Error(
                ParticipationErrorCodes.PreparationVersionMismatch,
                "考试时间、状态或资源版本已变化，请重新获取进入资料。");
        }
    }

    private static bool IsActualParticipation(ParticipationRecord? participation) =>
        participation?.BusinessState is ParticipationBusinessState.Waiting or ParticipationBusinessState.Answering;

    private static ExamLifecycle CurrentLifecycle(ExamEntrySnapshot exam, DateTimeOffset now) =>
        exam.Lifecycle == ExamLifecycle.Ended || now >= exam.ScheduledEnd ? ExamLifecycle.Ended : exam.Lifecycle;

    private static void EnsureExamAllowedNow(
        ExamEntrySnapshot exam,
        DateTimeOffset now,
        bool actualRecovery = false)
    {
        if (!actualRecovery && (exam.Lifecycle == ExamLifecycle.Ended ||
            now < exam.ScheduledStart - EntryWindowBeforeStart ||
            now >= exam.ScheduledEnd))
        {
            throw Error(ParticipationErrorCodes.EntryNotAllowed, "当前不在允许进入考试的时间或状态内。");
        }

        if (!exam.PackageReady || !exam.InstructionsReady)
        {
            throw Error(ParticipationErrorCodes.ResourcesNotReady, "试题包或考试说明尚未准备完成。");
        }

        if (exam.PackageLength <= 0 || exam.PaperVersion <= 0 || exam.InstructionsVersion <= 0 ||
            string.IsNullOrWhiteSpace(exam.EncryptedPackageUri))
        {
            throw Error(ParticipationErrorCodes.ResourcesNotReady, "考试资源元数据无效。");
        }

        if (CurrentLifecycle(exam, now) == ExamLifecycle.Running && string.IsNullOrWhiteSpace(exam.UnlockReference))
        {
            throw Error(ParticipationErrorCodes.ResourcesNotReady, "考试已开始，但解锁信息尚不可用。");
        }
    }

    private static T RunIdempotent<T>(
        IParticipationTransaction transaction,
        string requestId,
        string operation,
        object payload,
        Func<T> execute)
    {
        var payloadHash = HashPayload(payload);
        var previous = transaction.FindIdempotencyRecord(requestId);
        if (previous is not null)
        {
            if (previous.Operation != operation || previous.PayloadHash != payloadHash)
            {
                throw Error(ParticipationErrorCodes.IdempotencyConflict, "requestId 已用于另一项写请求或不同参数。");
            }

            return JsonSerializer.Deserialize<T>(previous.ResponseJson)
                ?? throw new InvalidOperationException("无法读取已保存的幂等响应。");
        }

        var result = execute();
        transaction.SaveIdempotencyRecord(new ParticipationIdempotencyRecord(
            requestId,
            operation,
            payloadHash,
            typeof(T).FullName ?? typeof(T).Name,
            JsonSerializer.Serialize(result)));
        return result;
    }

    private static string HashPayload(object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType());
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static void EnsureAuthorization(
        ParticipationRecord participation,
        string deviceId,
        long entryVersion,
        long authorizationVersion)
    {
        if (!StringComparer.Ordinal.Equals(participation.AuthorizedDeviceId, deviceId) ||
            participation.EntryVersion != entryVersion ||
            participation.AuthorizationVersion != authorizationVersion)
        {
            throw Error(ParticipationErrorCodes.AuthorizationRevoked, "设备或授权版本已经失效。");
        }
    }

    private static void EnsureSessionOwnsParticipation(StudentSession session, ParticipationRecord participation)
    {
        if (!StringComparer.Ordinal.Equals(CandidateIdForExam(session, participation.ExamId), participation.CandidateId) ||
            !StringComparer.Ordinal.Equals(session.DeviceId, participation.AuthorizedDeviceId))
        {
            throw Error(ParticipationErrorCodes.AuthorizationRevoked, "当前会话无权操作该参加考试记录。");
        }
    }

    private static void EnsureAuthenticatedDeviceOwnsSession(
        string? authenticatedDeviceId,
        StudentSession session)
    {
        if (authenticatedDeviceId is not null &&
            !string.Equals(authenticatedDeviceId, session.DeviceId, StringComparison.Ordinal))
        {
            throw Error(ParticipationErrorCodes.AuthorizationRevoked, "已认证设备与学生会话不一致。");
        }
    }

    private static void EnsureVersion(ParticipationRecord participation, long expectedVersion)
    {
        if (participation.Version != expectedVersion)
        {
            throw Error(ParticipationErrorCodes.VersionConflict, "参加考试记录版本冲突。");
        }
    }

    private static StudentSessionResult ToSessionResult(StudentSession session) => new(
        session.SessionId,
        session.DeviceId,
        session.Candidate,
        session.ExamIds,
        session.AuthenticatedAt,
        session.CandidateIdsByExam);

    private static string CandidateIdForExam(StudentSession session, string examId) =>
        session.CandidateIdsByExam.TryGetValue(examId, out var candidateId)
            ? candidateId
            : throw Error(ParticipationErrorCodes.RosterMismatch, "当前登录身份不在该考试名册中。");

    private static ExamResourceDescriptor ToResources(ExamEntrySnapshot exam) => new(
        exam.EncryptedPackageUri,
        exam.PackageLength,
        exam.PaperVersion,
        exam.InstructionsVersion);

    private static ParticipationResult ToResult(ParticipationRecord participation, string? unlockReference) => new(
        participation.ParticipationId,
        participation.ExamId,
        participation.CandidateId,
        participation.BusinessState,
        participation.AuthorizedDeviceId,
        participation.EntryVersion,
        participation.AuthorizationVersion,
        participation.Version,
        unlockReference);

    private static void ValidateRequestId(string requestId) => Require(requestId, nameof(requestId));

    private static void Require(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Error(ParticipationErrorCodes.InvalidRequest, $"{field} 不能为空。");
        }
    }

    private static void RequireAsciiDigits(string value, string field)
    {
        if (value.Any(character => character is < '0' or > '9'))
        {
            throw Error(ParticipationErrorCodes.InvalidRequest, $"{field} 只能包含数字。");
        }
    }

    private static ParticipationRuleException Error(string code, string message) => new(code, message);
}
