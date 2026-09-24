using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;

namespace Exam.Domain.Submissions;

public sealed record SubmissionAuthorizationState(
    string ExamId,
    string CandidateId,
    string ParticipationId,
    long EntryVersion,
    string AuthorizedDeviceId,
    long AuthorizationVersion,
    ParticipationBusinessState BusinessState,
    long CurrentPaperVersion,
    long CurrentGradingVersion);

public enum ProgressPersistenceStatus
{
    Saved,
    RequestReplay,
    VersionConflict,
    InvalidState,
    AuthorizationRevoked,
    AlreadySubmitted,
    ParticipationNotFound
}

public sealed record ProgressPersistenceResult(
    ProgressPersistenceStatus Status,
    PersistedProgress? Progress);

public enum SubmissionPersistenceStatus
{
    Saved,
    RequestReplay,
    ExistingSubmission,
    SubmissionIdConflict,
    VersionConflict,
    InvalidState,
    AuthorizationRevoked,
    ParticipationNotFound
}

public sealed record SubmissionPersistenceResult(
    SubmissionPersistenceStatus Status,
    FinalSubmission? Submission);

/// <summary>
/// Transactional boundary for authorization, progress and final submissions. A production
/// implementation must enforce all checks and writes in the same database transaction.
/// </summary>
public interface ISubmissionStore
{
    ValueTask SetAuthorizationStateAsync(
        SubmissionAuthorizationState state,
        CancellationToken cancellationToken = default);

    ValueTask<ProgressPersistenceResult> SaveProgressAsync(
        SyncProgressRequest request,
        DateTimeOffset persistedAt,
        CancellationToken cancellationToken = default);

    ValueTask<SubmissionPersistenceResult> SaveSubmissionAsync(
        SubmitFinalAnswersRequest request,
        DateTimeOffset persistedAt,
        string receiptId,
        CancellationToken cancellationToken = default);

    ValueTask<PersistedProgress?> FindProgressAsync(
        string participationId,
        CancellationToken cancellationToken = default);

    ValueTask<FinalSubmission?> FindSubmissionAsync(
        string examId,
        string candidateId,
        CancellationToken cancellationToken = default);

    ValueTask<FinalSubmission?> FindSubmissionByParticipationAsync(
        string participationId,
        CancellationToken cancellationToken = default);
}

public interface IGradingTaskQueue
{
    ValueTask<bool> EnqueueOnceAsync(
        string submissionId,
        long gradingVersion,
        CancellationToken cancellationToken = default);
}

public interface ICollectionCompletionStore
{
    ValueTask<CollectionCompletedNotification?> TryRecordAsync(
        string examId,
        long rosterVersion,
        int participantCount,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>Production collection evaluation uses only the transactional authoritative roster.</summary>
public interface IAuthoritativeCollectionCompletionStore
{
    ValueTask<CollectionCompletedNotification?> EvaluateAsync(string examId, CancellationToken cancellationToken = default);
    ValueTask<int> ProcessPendingAsync(int limit = 100, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<CollectionCompletedNotification>> ReadEventsAsync(
        string examId, long afterRosterVersion, int limit = 100, CancellationToken cancellationToken = default);
}

public static class SubmissionPolicy
{
    public static string? ValidateAnswers(IReadOnlyDictionary<string, AnswerValue>? answers)
    {
        if (answers is null)
        {
            return "答案集合不能为空。";
        }

        foreach (var (questionId, answer) in answers)
        {
            if (string.IsNullOrWhiteSpace(questionId) || answer is null)
            {
                return "题目标识和答案不能为空。";
            }

            if (!Enum.IsDefined(answer.Kind))
            {
                return "作答类型必须是 empty、selected 或 abstain。";
            }

            var options = answer.OptionIds;
            if (options is null)
            {
                return "选项集合不能为空。";
            }

            if (answer.Kind == AnswerKind.Selected && options.Count == 0)
            {
                return "selected 答案必须至少包含一个选项。";
            }

            if (answer.Kind != AnswerKind.Selected && options.Count != 0)
            {
                return "empty 与 abstain 答案不能携带选项。";
            }

            if (options.Any(string.IsNullOrWhiteSpace) ||
                options.Distinct(StringComparer.Ordinal).Count() != options.Count)
            {
                return "答案选项不能为空或重复。";
            }
        }

        return null;
    }

    public static string? ValidateProgress(SyncProgressRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.ExamId) ||
            string.IsNullOrWhiteSpace(request.CandidateId) ||
            string.IsNullOrWhiteSpace(request.ParticipationId) ||
            string.IsNullOrWhiteSpace(request.DeviceId) ||
            request.EntryVersion < 1 ||
            request.AuthorizationVersion < 1 ||
            request.AnswerVersion < 0 ||
            request.PaperVersion < 1)
        {
            return "进度请求的标识或版本无效。";
        }

        return ValidateAnswers(request.Answers);
    }

    public static string? ValidateSubmission(SubmitFinalAnswersRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) ||
            string.IsNullOrWhiteSpace(request.SubmissionId) ||
            string.IsNullOrWhiteSpace(request.ExamId) ||
            string.IsNullOrWhiteSpace(request.CandidateId) ||
            string.IsNullOrWhiteSpace(request.ParticipationId) ||
            string.IsNullOrWhiteSpace(request.DeviceId) ||
            request.EntryVersion < 1 ||
            request.AuthorizationVersion < 1 ||
            request.PaperVersion < 1)
        {
            return "交卷请求的标识或版本无效。";
        }

        return ValidateAnswers(request.FrozenAnswers);
    }
}
