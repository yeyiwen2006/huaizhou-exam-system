using Exam.Contracts.Common;

namespace Exam.Contracts.Versioning;

public enum ScheduleTriggerKind
{
    Start,
    End
}

public enum PaperUpdateState
{
    NotReceived,
    DownloadFailed,
    Updated,
    ReviewRequired
}

public enum PaperTransferOutcome
{
    DownloadFailed,
    Installed
}

public enum VersioningErrorCode
{
    None,
    ExamAlreadyExists,
    ExamNotFound,
    ParticipantNotFound,
    VersionConflict,
    InvalidSchedule,
    InvalidRequest,
    InvalidStateTransition,
    ExamEnded,
    PublicationNotReady,
    StructuralChangeRejected,
    StaleScheduleTask,
    ScheduleTaskNotDue,
    ResourceRollbackRejected,
    IdempotencyConflict
}

public sealed record ScheduleWindow(DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public sealed record VersionVector(
    long ScheduleVersion,
    long PaperVersion,
    long GradingVersion);

public sealed record PaperQuestionStructure(
    string QuestionId,
    int QuestionNo,
    string SubjectId,
    string GroupId,
    string QuestionType,
    IReadOnlyList<string> OptionIds,
    bool AllowAbstain = false);

public sealed record PaperStructure(IReadOnlyList<PaperQuestionStructure> Questions);

public sealed record PaperPublicationValidation(
    bool ManifestValidated,
    bool Compiled,
    bool PreviewApproved,
    bool EncryptedPackageReady)
{
    public bool IsReady => ManifestValidated && Compiled && PreviewApproved && EncryptedPackageReady;
}

public sealed record GradingPublicationValidation(
    bool AnswersValidated,
    bool RulesValidated,
    bool RecalculationReady)
{
    public bool IsReady => AnswersValidated && RulesValidated && RecalculationReady;
}

public sealed record PaperPublicationInfo(
    long Version,
    string ArtifactId,
    PaperStructure Structure,
    IReadOnlyList<string> ChangedQuestionIds,
    DateTimeOffset PublishedAt,
    string Operator,
    string Reason);

public sealed record GradingPublicationInfo(
    long Version,
    string RuleSetId,
    DateTimeOffset PublishedAt,
    string Operator,
    string Reason);

public sealed record ParticipantVersionStatus(
    string ParticipantId,
    ParticipationBusinessState ExamState,
    long TargetPaperVersion,
    long InstalledPaperVersion,
    PaperUpdateState PaperUpdateState,
    string? LastError);

public sealed record ExamVersionSnapshot(
    string ExamId,
    VersionVector Versions,
    ScheduleWindow Schedule,
    ExamLifecycle LifecycleState,
    DateTimeOffset? ActualStartedAt,
    DateTimeOffset? ActualEndedAt,
    PaperPublicationInfo? Paper,
    GradingPublicationInfo? Grading,
    IReadOnlyDictionary<string, ParticipantVersionStatus> Participants);

public sealed record CreateVersionedExamRequest(
    string ExamId,
    ScheduleWindow Schedule,
    DateTimeOffset Now,
    string RequestId = "");

public sealed record UpdateScheduleRequest(
    string ExamId,
    long ExpectedVersion,
    ScheduleWindow Schedule,
    DateTimeOffset Now,
    string RequestId = "");

public sealed record ExecuteScheduleTriggerRequest(
    string ExamId,
    long ScheduleVersion,
    ScheduleTriggerKind Trigger,
    DateTimeOffset Now,
    string RequestId = "");

public sealed record PublishPaperRequest(
    string ExamId,
    long ExpectedVersion,
    string ArtifactId,
    PaperStructure Structure,
    IReadOnlyList<string> ChangedQuestionIds,
    PaperPublicationValidation Validation,
    string Operator,
    string Reason,
    DateTimeOffset Now,
    string RequestId = "");

public sealed record PublishGradingRulesRequest(
    string ExamId,
    long ExpectedVersion,
    string RuleSetId,
    GradingPublicationValidation Validation,
    string Operator,
    string Reason,
    DateTimeOffset Now,
    string RequestId = "",
    string? ManifestJson = null,
    string? AnswerKeyJson = null);

public sealed record RegisterVersionedParticipantRequest(
    string ExamId,
    string ParticipantId,
    ParticipationBusinessState InitialState,
    string RequestId = "");

public sealed record ChangeParticipantStateRequest(
    string ExamId,
    string ParticipantId,
    ParticipationBusinessState State,
    string RequestId = "");

public sealed record ReportPaperTransferRequest(
    string ExamId,
    string ParticipantId,
    long PaperVersion,
    long InstalledPaperVersion,
    PaperTransferOutcome Outcome,
    string? Error,
    string RequestId = "",
    string DeviceId = "",
    long EntryVersion = 0,
    long AuthorizationVersion = 0);

public sealed record ConfirmPaperReviewRequest(
    string ExamId,
    string ParticipantId,
    long PaperVersion,
    string RequestId = "",
    string DeviceId = "",
    long EntryVersion = 0,
    long AuthorizationVersion = 0);

public sealed record VersioningResult<T>(
    bool Succeeded,
    VersioningErrorCode ErrorCode,
    string? Error,
    T? Value)
{
    public static VersioningResult<T> Success(T value) =>
        new(true, VersioningErrorCode.None, null, value);

    public static VersioningResult<T> Failure(VersioningErrorCode code, string error) =>
        new(false, code, error, default);
}

public sealed record ScheduleTriggerResult(
    bool Applied,
    VersioningErrorCode ErrorCode,
    string? Error,
    ExamVersionSnapshot Snapshot);

public interface IExamVersioningService
{
    VersioningResult<ExamVersionSnapshot> CreateExam(CreateVersionedExamRequest request);

    VersioningResult<ExamVersionSnapshot> GetExam(string examId);

    VersioningResult<ExamVersionSnapshot> UpdateSchedule(UpdateScheduleRequest request);

    VersioningResult<ScheduleTriggerResult> ExecuteScheduleTrigger(ExecuteScheduleTriggerRequest request);

    VersioningResult<ExamVersionSnapshot> PublishPaper(PublishPaperRequest request);

    VersioningResult<ExamVersionSnapshot> PublishGradingRules(PublishGradingRulesRequest request);

    VersioningResult<ExamVersionSnapshot> RegisterParticipant(RegisterVersionedParticipantRequest request);

    VersioningResult<ExamVersionSnapshot> ChangeParticipantState(ChangeParticipantStateRequest request);

    VersioningResult<ExamVersionSnapshot> ReportPaperTransfer(ReportPaperTransferRequest request);

    VersioningResult<ExamVersionSnapshot> ConfirmPaperReview(ConfirmPaperReviewRequest request);
}
