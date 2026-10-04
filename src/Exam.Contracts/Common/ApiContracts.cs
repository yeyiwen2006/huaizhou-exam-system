using System.Text.Json.Serialization;

namespace Exam.Contracts.Common;

public enum ExamLifecycle
{
    Scheduled,
    Running,
    Ended
}

public enum ParticipationBusinessState
{
    NotParticipating,
    Waiting,
    Answering,
    Submitted
}

public enum AnswerKind
{
    Empty,
    Selected,
    Abstain
}

public enum CommandEventType
{
    Start,
    End,
    ScheduleChanged,
    PaperUpdated,
    DeviceDecided,
    CollectionCompleted,
    UploadRequested
}

public static class ErrorCodes
{
    public const string NotActivated = "NOT_ACTIVATED";
    public const string RosterMismatch = "ROSTER_MISMATCH";
    public const string EntryNotAllowed = "ENTRY_NOT_ALLOWED";
    public const string AlreadySubmitted = "ALREADY_SUBMITTED";
    public const string DeviceDecisionPending = "DEVICE_DECISION_PENDING";
    public const string AuthorizationRevoked = "AUTHORIZATION_REVOKED";
    public const string VersionConflict = "VERSION_CONFLICT";
    public const string ImportInvalid = "IMPORT_INVALID";
    public const string KeyNotReleased = "KEY_NOT_RELEASED";
    public const string ReportNotReady = "REPORT_NOT_READY";
    public const string PersistenceFailed = "PERSISTENCE_FAILED";
    public const string ServiceRecovering = "SERVICE_RECOVERING";
    public const string NotActiveServer = "NOT_ACTIVE_SERVER";
    public const string StaleServerEpoch = "STALE_SERVER_EPOCH";
    public const string ReplicaUnavailable = "REPLICA_UNAVAILABLE";
    public const string InvalidInput = "INVALID_INPUT";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
}

public sealed record ApiError(
    string Code,
    string Message,
    string? Field = null,
    string? Location = null,
    bool Retryable = false);

public sealed record ApiEnvelope<T>(
    string ServerId,
    string NodeId,
    long ServerEpoch,
    string CorrelationId,
    T? Result,
    ApiError? Error)
{
    [JsonIgnore]
    public bool IsSuccess => Error is null;
}

public sealed record MutationContext(
    string RequestId,
    long? ExpectedVersion = null,
    string? ActorId = null,
    DateTimeOffset? RequestedAt = null);

public sealed record VersionedResult<T>(T Value, long Version, bool Replayed = false);
