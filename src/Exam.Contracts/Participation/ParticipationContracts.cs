using Exam.Contracts.Common;

namespace Exam.Contracts.Participation;

public enum EntryPreparationStatus
{
    Ready,
    Resume,
    DeviceDecisionPending
}

public sealed record CandidateIdentity(
    string CandidateId,
    string Name,
    string ClassText,
    string AdmissionNo);

public sealed record LoginStudentRequest(
    string RequestId,
    string DeviceId,
    string Credential,
    string Name,
    string ClassText,
    string AdmissionNo);

public sealed record StudentSessionResult(
    string SessionId,
    string DeviceId,
    CandidateIdentity Candidate,
    IReadOnlyList<string> ExamIds,
    DateTimeOffset AuthenticatedAt,
    IReadOnlyDictionary<string, string>? CandidateIdsByExam = null);

public sealed record ConnectedStudentSessionResult(
    StudentSessionResult Session,
    Exam.Contracts.Activation.ActivationResult Device);

public sealed record PrepareExamEntryRequest(
    string RequestId,
    string SessionId,
    string ExamId);

public sealed record ExamResourceDescriptor(
    string EncryptedPackageUri,
    long PackageLength,
    long PaperVersion,
    long InstructionsVersion);

public sealed record EntryPreparationResult(
    string PreparationId,
    string ExamId,
    EntryPreparationStatus Status,
    ExamResourceDescriptor? Resources,
    string? ParticipationId,
    string? AuthorizedDeviceId,
    string? PendingDeviceId,
    long? EntryVersion,
    long? AuthorizationVersion,
    DateTimeOffset PreparedAt);

public sealed record ConfirmParticipationRequest(
    string RequestId,
    string SessionId,
    string ExamId,
    string PreparationId,
    long DownloadedPaperVersion,
    long DownloadedInstructionsVersion);

public sealed record ParticipationResult(
    string ParticipationId,
    string ExamId,
    string CandidateId,
    ParticipationBusinessState BusinessState,
    string AuthorizedDeviceId,
    long EntryVersion,
    long AuthorizationVersion,
    long Version,
    string? UnlockReference,
    ExamLifecycle? Lifecycle = null);

public sealed record WithdrawParticipationRequest(
    string RequestId,
    string SessionId,
    string ParticipationId,
    long ExpectedVersion);

public sealed record DeviceDecisionRequest(
    string RequestId,
    string ParticipationId,
    string SelectedDeviceId,
    long ExpectedVersion,
    string OperatorId,
    string Reason);

public sealed record DeviceDecisionResult(
    string ParticipationId,
    string AuthorizedDeviceId,
    string RevokedDeviceId,
    long AuthorizationVersion,
    long Version,
    string OperatorId,
    string Reason,
    DateTimeOffset DecidedAt);

public sealed record AuthorizationCheckRequest(
    string ParticipationId,
    string DeviceId,
    long EntryVersion,
    long AuthorizationVersion);

public sealed record FinalizeParticipationRequest(
    string RequestId,
    string ParticipationId,
    string DeviceId,
    long EntryVersion,
    long AuthorizationVersion);

public sealed record AdvanceExamToAnsweringRequest(
    string RequestId,
    string ExamId);

public sealed record ExamStartTransitionResult(
    string ExamId,
    int ChangedParticipationCount,
    DateTimeOffset TransitionedAt);

public static class ParticipationErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string NotActivated = "NOT_ACTIVATED";
    public const string RosterMismatch = "ROSTER_MISMATCH";
    public const string SessionNotFound = "SESSION_NOT_FOUND";
    public const string ExamNotFound = "EXAM_NOT_FOUND";
    public const string EntryNotAllowed = "ENTRY_NOT_ALLOWED";
    public const string ResourcesNotReady = "RESOURCES_NOT_READY";
    public const string PreparationNotFound = "PREPARATION_NOT_FOUND";
    public const string PreparationVersionMismatch = "PREPARATION_VERSION_MISMATCH";
    public const string DeviceDecisionPending = "DEVICE_DECISION_PENDING";
    public const string AuthorizationRevoked = "AUTHORIZATION_REVOKED";
    public const string AlreadySubmitted = "ALREADY_SUBMITTED";
    public const string VersionConflict = "VERSION_CONFLICT";
    public const string InvalidBusinessState = "INVALID_BUSINESS_STATE";
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";
}
