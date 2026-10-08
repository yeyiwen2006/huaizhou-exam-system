using System.Collections.ObjectModel;
using Exam.Contracts.Common;

namespace Exam.Contracts.Commands;

public enum CommandProcessingResult
{
    Applied,
    Received,
    ResourceUpdateFailed,
    RejectedAsStale
}

public enum CommandErrorCode
{
    None,
    InvalidRequest,
    EventNotFound,
    AuthorizationRevoked,
    VersionConflict,
    KeyNotReleased,
    StaleServerEpoch,
    PersistenceFailed
}

public sealed record ServerNodeEnvelope(string ServerId, string NodeId, long ServerEpoch);

public sealed record ServerTimeSnapshot(
    DateTimeOffset ServerTime,
    ServerNodeEnvelope Server);

public sealed record ParticipationAccessSnapshot(
    string ExamId,
    string CandidateId,
    string ParticipationId,
    long EntryVersion,
    string AuthorizedDeviceId,
    long AuthorizationVersion,
    ExamLifecycle ExamLifecycle,
    ParticipationBusinessState BusinessState,
    long PaperVersion,
    long GradingVersion)
{
    public long ReopenedAtScheduleVersion { get; init; }
}

public sealed record CreateCommandEventRequest(
    string RequestId,
    string EventId,
    string ExamId,
    string? ParticipationId,
    long EntryVersion,
    CommandEventType EventType,
    long StateVersion,
    string? TargetDeviceId,
    long? TargetAuthorizationVersion,
    long? ResourceVersion,
    string? ProtectedUnlockReference,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public IReadOnlyDictionary<string, string> SafeMetadata =>
        Metadata is null
            ? ReadOnlyDictionary<string, string>.Empty
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(Metadata, StringComparer.Ordinal));
}

public sealed record CommandEventView(
    string EventId,
    string ExamId,
    string? ParticipationId,
    long EntryVersion,
    CommandEventType EventType,
    long StateVersion,
    string? TargetDeviceId,
    long? TargetAuthorizationVersion,
    long? ResourceVersion,
    DateTimeOffset CreatedAt,
    bool Acknowledged,
    int DeliveryAttempts);

public sealed record CommandDelivery(
    ServerNodeEnvelope Server,
    string EventId,
    string ExamId,
    string ParticipationId,
    long EntryVersion,
    CommandEventType EventType,
    long StateVersion,
    string TargetDeviceId,
    long TargetAuthorizationVersion,
    long? ResourceVersion,
    string? ProtectedUnlockReference,
    IReadOnlyDictionary<string, string> Metadata,
    DateTimeOffset CreatedAt,
    int DeliveryAttempt)
{
    /// <summary>ScheduleChanged 事件携带的新开考时间；服务端不填时为 null。</summary>
    public DateTimeOffset? NewScheduledStart { get; init; }
    /// <summary>ScheduleChanged 事件携带的新结束时间；服务端不填时为 null。</summary>
    public DateTimeOffset? NewScheduledEnd { get; init; }
}

public sealed record AcknowledgeCommandRequest(
    string RequestId,
    string EventId,
    string ParticipationId,
    long EntryVersion,
    string DeviceId,
    long AuthorizationVersion,
    CommandProcessingResult ProcessingResult,
    long? ActualPaperVersion,
    string? FailureReason);

public sealed record CommandAcknowledgement(
    string EventId,
    string RequestId,
    CommandProcessingResult ProcessingResult,
    long? ActualPaperVersion,
    string? FailureReason,
    DateTimeOffset AcknowledgedAt);

public sealed record CommandOperationResult<T>(
    bool IsSuccess,
    T? Value,
    CommandErrorCode ErrorCode,
    string Message)
{
    public static CommandOperationResult<T> Success(T value, string message = "操作成功。") =>
        new(true, value, CommandErrorCode.None, message);

    public static CommandOperationResult<T> Failure(CommandErrorCode code, string message) =>
        new(false, default, code, message);
}
