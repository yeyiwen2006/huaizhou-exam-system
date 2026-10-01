using System.Collections.ObjectModel;
using Exam.Contracts.Common;

namespace Exam.Contracts.Submissions;

public sealed record AnswerValue(AnswerKind Kind, IReadOnlyList<string> OptionIds)
{
    public static AnswerValue Empty() => new(AnswerKind.Empty, Array.Empty<string>());

    public static AnswerValue Abstain() => new(AnswerKind.Abstain, Array.Empty<string>());

    public static AnswerValue Selected(params string[] optionIds) =>
        new(AnswerKind.Selected, Array.AsReadOnly(optionIds.ToArray()));
}

public enum SubmissionErrorCode
{
    None,
    InvalidRequest,
    AuthorizationRevoked,
    VersionConflict,
    AlreadySubmitted,
    SubmissionIdConflict,
    NotFound,
    ServiceRecovering,
    PersistenceFailed
}

public sealed record SyncProgressRequest(
    string RequestId,
    string ExamId,
    string CandidateId,
    string ParticipationId,
    long EntryVersion,
    string DeviceId,
    long AuthorizationVersion,
    long AnswerVersion,
    long PaperVersion,
    IReadOnlyDictionary<string, AnswerValue> Answers,
    string? CurrentQuestionId);

public sealed record PersistedProgress(
    string ExamId,
    string CandidateId,
    string ParticipationId,
    long EntryVersion,
    string DeviceId,
    long AuthorizationVersion,
    long AnswerVersion,
    long PaperVersion,
    IReadOnlyDictionary<string, AnswerValue> Answers,
    string? CurrentQuestionId,
    DateTimeOffset LastSyncedAt)
{
    public static IReadOnlyDictionary<string, AnswerValue> FreezeAnswers(
        IReadOnlyDictionary<string, AnswerValue> answers) =>
        new ReadOnlyDictionary<string, AnswerValue>(answers.ToDictionary(
            pair => pair.Key,
            pair => new AnswerValue(pair.Value.Kind, Array.AsReadOnly(pair.Value.OptionIds.ToArray())),
            StringComparer.Ordinal));
}

public sealed record SubmitFinalAnswersRequest(
    string RequestId,
    string SubmissionId,
    string ExamId,
    string CandidateId,
    string ParticipationId,
    long EntryVersion,
    string DeviceId,
    long AuthorizationVersion,
    long PaperVersion,
    IReadOnlyDictionary<string, AnswerValue> FrozenAnswers);

public sealed record SubmissionReceipt(
    string ReceiptId,
    string SubmissionId,
    string ExamId,
    string CandidateId,
    string ParticipationId,
    DateTimeOffset ReceivedAt,
    long PaperVersion,
    long GradingVersion);

public sealed record FinalSubmission(
    string SubmissionId,
    string ExamId,
    string CandidateId,
    string ParticipationId,
    string DeviceId,
    long AuthorizationVersion,
    long PaperVersion,
    IReadOnlyDictionary<string, AnswerValue> FrozenAnswers,
    DateTimeOffset ReceivedAt,
    SubmissionReceipt Receipt);

public sealed record CollectionCompletedNotification(
    string EventId,
    string ExamId,
    long RosterVersion,
    int ParticipantCount,
    DateTimeOffset CompletedAt);

/// <summary>一条已完成的失焦区间：EventId 由学生端在事件完成时生成，重试保持不变；发生与恢复时刻均为 UTC。</summary>
public sealed record ReportedFocusLossEvent(
    string EventId,
    DateTimeOffset LostAt,
    DateTimeOffset RegainedAt,
    long DurationMilliseconds);

/// <summary>B06 失焦补报：本地留痕事件的批量上报；按 (ParticipationId, EventId) 幂等去重，重试可用新 RequestId。</summary>
public sealed record ReportFocusEventsRequest(
    string RequestId,
    string ExamId,
    string CandidateId,
    string ParticipationId,
    long EntryVersion,
    string DeviceId,
    long AuthorizationVersion,
    long PaperVersion,
    IReadOnlyList<ReportedFocusLossEvent> Events);

/// <summary>服务端确认回执：本次受理的事件标识（含此前已存在而按幂等跳过的），学生端确认后删除本地留痕。</summary>
public sealed record FocusEventsReceipt(
    string ExamId,
    string CandidateId,
    string ParticipationId,
    long PaperVersion,
    IReadOnlyList<string> AcceptedEventIds,
    DateTimeOffset PersistedAt);

/// <summary>考务端按考生汇总的失焦监测视图。</summary>
public sealed record FocusLossEventSummary(
    string ExamId,
    string CandidateId,
    string ParticipationId,
    int EventCount,
    long TotalDurationMilliseconds,
    DateTimeOffset? LastLostAt);

public sealed record SubmissionOperationResult<T>(
    bool IsSuccess,
    T? Value,
    SubmissionErrorCode ErrorCode,
    string Message,
    bool IsReplay = false)
{
    public static SubmissionOperationResult<T> Success(T value, bool isReplay = false, string message = "操作成功。") =>
        new(true, value, SubmissionErrorCode.None, message, isReplay);

    public static SubmissionOperationResult<T> Failure(SubmissionErrorCode code, string message) =>
        new(false, default, code, message);
}
