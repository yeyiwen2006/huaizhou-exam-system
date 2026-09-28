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

/// <summary>
/// B06 失焦检测补报：可选的现场状态补充字段。旧客户端不发送时保持 null，
/// 序列化向后兼容；服务端是否持久化及考务端展示由 A/C 侧接续实现。
/// </summary>
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
    string? CurrentQuestionId,
    int? FocusLostCount = null,
    DateTimeOffset? LastFocusRegainedAt = null);

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
