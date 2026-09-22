using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Domain.Submissions;

namespace Exam.Application.Submissions;

/// <summary>
/// Thread-safe reference implementation of the submission transaction boundary. It models the
/// unique constraints and atomic state changes expected from the production database adapter.
/// </summary>
public sealed class InMemorySubmissionStore : ISubmissionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SubmissionAuthorizationState> _authorizations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, PersistedProgress> _progressByParticipation =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, (RequestIdentity Identity, ProgressPersistenceResult Result)> _progressRequests =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, FinalSubmission> _submissionsByCandidate =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, FinalSubmission> _submissionsByParticipation =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, FinalSubmission> _submissionsById =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, (RequestIdentity Identity, SubmissionPersistenceResult Result)> _submissionRequests =
        new(StringComparer.Ordinal);

    public ValueTask SetAuthorizationStateAsync(
        SubmissionAuthorizationState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _authorizations[state.ParticipationId] = state;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<ProgressPersistenceResult> SaveProgressAsync(
        SyncProgressRequest request,
        DateTimeOffset persistedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var requestIdentity = IdentityOf(request);
            if (_progressRequests.TryGetValue(request.RequestId, out var storedRequest))
            {
                if (storedRequest.Identity != requestIdentity)
                {
                    return ValueTask.FromResult(new ProgressPersistenceResult(
                        ProgressPersistenceStatus.AuthorizationRevoked,
                        null));
                }

                var replay = storedRequest.Result;
                return ValueTask.FromResult(
                    replay.Status is ProgressPersistenceStatus.Saved or ProgressPersistenceStatus.RequestReplay
                        ? replay with { Status = ProgressPersistenceStatus.RequestReplay }
                        : replay);
            }

            ProgressPersistenceResult result;
            if (!_authorizations.TryGetValue(request.ParticipationId, out var authorization))
            {
                result = new(ProgressPersistenceStatus.ParticipationNotFound, null);
            }
            else if (!IsAuthorized(request, authorization))
            {
                result = new(ProgressPersistenceStatus.AuthorizationRevoked, null);
            }
            else if (authorization.BusinessState == ParticipationBusinessState.Submitted ||
                     _submissionsByParticipation.ContainsKey(request.ParticipationId))
            {
                result = new(
                    ProgressPersistenceStatus.AlreadySubmitted,
                    _progressByParticipation.GetValueOrDefault(request.ParticipationId));
            }
            else if (authorization.BusinessState != ParticipationBusinessState.Answering)
            {
                result = new(ProgressPersistenceStatus.InvalidState, null);
            }
            else if (request.PaperVersion > authorization.CurrentPaperVersion)
            {
                result = new(
                    ProgressPersistenceStatus.VersionConflict,
                    _progressByParticipation.GetValueOrDefault(request.ParticipationId));
            }
            else if (_progressByParticipation.TryGetValue(request.ParticipationId, out var current) &&
                     request.AnswerVersion <= current.AnswerVersion)
            {
                result = new(ProgressPersistenceStatus.VersionConflict, current);
            }
            else
            {
                var progress = new PersistedProgress(
                    request.ExamId,
                    request.CandidateId,
                    request.ParticipationId,
                    request.EntryVersion,
                    request.DeviceId,
                    request.AuthorizationVersion,
                    request.AnswerVersion,
                    request.PaperVersion,
                    PersistedProgress.FreezeAnswers(request.Answers),
                    request.CurrentQuestionId,
                    persistedAt);
                _progressByParticipation[request.ParticipationId] = progress;
                result = new(ProgressPersistenceStatus.Saved, progress);
            }

            _progressRequests.Add(request.RequestId, (requestIdentity, result));
            return ValueTask.FromResult(result);
        }
    }

    public ValueTask<SubmissionPersistenceResult> SaveSubmissionAsync(
        SubmitFinalAnswersRequest request,
        DateTimeOffset persistedAt,
        string receiptId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var requestIdentity = IdentityOf(request);
            if (_submissionRequests.TryGetValue(request.RequestId, out var storedRequest))
            {
                if (storedRequest.Identity != requestIdentity)
                {
                    return ValueTask.FromResult(new SubmissionPersistenceResult(
                        SubmissionPersistenceStatus.AuthorizationRevoked,
                        null));
                }

                var replay = storedRequest.Result;
                return ValueTask.FromResult(
                    replay.Status is SubmissionPersistenceStatus.Saved or
                        SubmissionPersistenceStatus.ExistingSubmission or
                        SubmissionPersistenceStatus.RequestReplay
                        ? replay with { Status = SubmissionPersistenceStatus.RequestReplay }
                        : replay);
            }

            SubmissionPersistenceResult result;
            var candidateKey = CandidateKey(request.ExamId, request.CandidateId);
            if (!_authorizations.TryGetValue(request.ParticipationId, out var authorization))
            {
                result = new(SubmissionPersistenceStatus.ParticipationNotFound, null);
            }
            else if (!IsAuthorized(request, authorization))
            {
                result = new(SubmissionPersistenceStatus.AuthorizationRevoked, null);
            }
            else if (_submissionsById.TryGetValue(request.SubmissionId, out var sameId) &&
                (!string.Equals(sameId.ExamId, request.ExamId, StringComparison.Ordinal) ||
                 !string.Equals(sameId.CandidateId, request.CandidateId, StringComparison.Ordinal)))
            {
                result = new(SubmissionPersistenceStatus.SubmissionIdConflict, null);
            }
            else if (_submissionsByCandidate.TryGetValue(candidateKey, out var existing))
            {
                result = new(SubmissionPersistenceStatus.ExistingSubmission, existing);
            }
            else if (authorization.BusinessState != ParticipationBusinessState.Answering)
            {
                result = new(SubmissionPersistenceStatus.InvalidState, null);
            }
            else if (request.PaperVersion > authorization.CurrentPaperVersion)
            {
                result = new(SubmissionPersistenceStatus.VersionConflict, null);
            }
            else
            {
                var receipt = new SubmissionReceipt(
                    receiptId,
                    request.SubmissionId,
                    request.ExamId,
                    request.CandidateId,
                    request.ParticipationId,
                    persistedAt,
                    request.PaperVersion,
                    authorization.CurrentGradingVersion);
                var submission = new FinalSubmission(
                    request.SubmissionId,
                    request.ExamId,
                    request.CandidateId,
                    request.ParticipationId,
                    request.DeviceId,
                    request.AuthorizationVersion,
                    request.PaperVersion,
                    PersistedProgress.FreezeAnswers(request.FrozenAnswers),
                    persistedAt,
                    receipt);

                // These changes form one critical section, mirroring one database transaction:
                // unique answer, receipt and terminal participation state become visible together.
                _submissionsById.Add(request.SubmissionId, submission);
                _submissionsByCandidate.Add(candidateKey, submission);
                _submissionsByParticipation.Add(request.ParticipationId, submission);
                _authorizations[request.ParticipationId] = authorization with
                {
                    BusinessState = ParticipationBusinessState.Submitted
                };
                result = new(SubmissionPersistenceStatus.Saved, submission);
            }

            _submissionRequests.Add(request.RequestId, (requestIdentity, result));
            return ValueTask.FromResult(result);
        }
    }

    public ValueTask<PersistedProgress?> FindProgressAsync(
        string participationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _progressByParticipation.TryGetValue(participationId, out var progress);
            return ValueTask.FromResult(progress);
        }
    }

    public ValueTask<FinalSubmission?> FindSubmissionAsync(
        string examId,
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _submissionsByCandidate.TryGetValue(CandidateKey(examId, candidateId), out var submission);
            return ValueTask.FromResult(submission);
        }
    }

    public ValueTask<FinalSubmission?> FindSubmissionByParticipationAsync(
        string participationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _submissionsByParticipation.TryGetValue(participationId, out var submission);
            return ValueTask.FromResult(submission);
        }
    }

    private static bool IsAuthorized(
        SyncProgressRequest request,
        SubmissionAuthorizationState authorization) =>
        string.Equals(request.ExamId, authorization.ExamId, StringComparison.Ordinal) &&
        string.Equals(request.CandidateId, authorization.CandidateId, StringComparison.Ordinal) &&
        request.EntryVersion == authorization.EntryVersion &&
        string.Equals(request.DeviceId, authorization.AuthorizedDeviceId, StringComparison.Ordinal) &&
        request.AuthorizationVersion == authorization.AuthorizationVersion;

    private static bool IsAuthorized(
        SubmitFinalAnswersRequest request,
        SubmissionAuthorizationState authorization) =>
        string.Equals(request.ExamId, authorization.ExamId, StringComparison.Ordinal) &&
        string.Equals(request.CandidateId, authorization.CandidateId, StringComparison.Ordinal) &&
        request.EntryVersion == authorization.EntryVersion &&
        string.Equals(request.DeviceId, authorization.AuthorizedDeviceId, StringComparison.Ordinal) &&
        request.AuthorizationVersion == authorization.AuthorizationVersion;

    private static string CandidateKey(string examId, string candidateId) =>
        string.Concat(examId, "\u001f", candidateId);

    private static RequestIdentity IdentityOf(SyncProgressRequest request) =>
        new(
            request.ExamId,
            request.CandidateId,
            request.ParticipationId,
            request.DeviceId);

    private static RequestIdentity IdentityOf(SubmitFinalAnswersRequest request) =>
        new(
            request.ExamId,
            request.CandidateId,
            request.ParticipationId,
            request.DeviceId);

    private readonly record struct RequestIdentity(
        string ExamId,
        string CandidateId,
        string ParticipationId,
        string DeviceId);
}

public sealed class InMemoryGradingTaskQueue : IGradingTaskQueue
{
    private readonly object _gate = new();
    private readonly HashSet<(string SubmissionId, long GradingVersion)> _tasks = [];

    public ValueTask<bool> EnqueueOnceAsync(
        string submissionId,
        long gradingVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_tasks.Add((submissionId, gradingVersion)));
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _tasks.Count;
            }
        }
    }
}

public sealed class InMemoryCollectionCompletionStore : ICollectionCompletionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<(string ExamId, long RosterVersion), CollectionCompletedNotification> _events = [];
    private readonly Func<string> _eventIdFactory;

    public InMemoryCollectionCompletionStore(Func<string>? eventIdFactory = null)
    {
        _eventIdFactory = eventIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    }

    public ValueTask<CollectionCompletedNotification?> TryRecordAsync(
        string examId,
        long rosterVersion,
        int participantCount,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = (examId, rosterVersion);
            if (_events.TryGetValue(key, out _))
            {
                return ValueTask.FromResult<CollectionCompletedNotification?>(null);
            }

            var notification = new CollectionCompletedNotification(
                _eventIdFactory(),
                examId,
                rosterVersion,
                participantCount,
                completedAt);
            _events.Add(key, notification);
            return ValueTask.FromResult<CollectionCompletedNotification?>(notification);
        }
    }
}
