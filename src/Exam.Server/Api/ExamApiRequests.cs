using Exam.Contracts.Common;
using Exam.Contracts.Versioning;

namespace Exam.Server.Api;

public sealed record ResendCommandApiRequest(
    string RequestId,
    string ParticipationId,
    long EntryVersion,
    string DeviceId,
    long AuthorizationVersion);

public sealed record EvaluateCollectionApiRequest(
    string RequestId,
    string ExamId,
    long RosterVersion,
    IReadOnlyCollection<string> ActualParticipationIds);

public sealed record CreateVersionedExamApiRequest(
    string RequestId,
    string ExamId,
    ScheduleWindow Schedule);

public sealed record UpdateScheduleApiRequest(
    string RequestId,
    long ExpectedVersion,
    ScheduleWindow Schedule);

public sealed record ExecuteScheduleTriggerApiRequest(
    string RequestId,
    long ScheduleVersion,
    ScheduleTriggerKind Trigger);

public sealed record PublishPaperApiRequest(
    string RequestId,
    long ExpectedVersion,
    string ArtifactId,
    PaperStructure Structure,
    IReadOnlyList<string> ChangedQuestionIds,
    PaperPublicationValidation Validation,
    string Operator,
    string Reason);

public sealed record PublishGradingRulesApiRequest(
    string RequestId,
    long ExpectedVersion,
    string RuleSetId,
    GradingPublicationValidation Validation,
    string Operator,
    string Reason,
    string? ManifestJson = null,
    string? AnswerKeyJson = null);

public sealed record RegisterVersionedParticipantApiRequest(
    string RequestId,
    string ParticipantId,
    ParticipationBusinessState InitialState);

public sealed record ChangeParticipantStateApiRequest(
    string RequestId,
    ParticipationBusinessState State);

public sealed record ReportPaperTransferApiRequest(
    string RequestId,
    string DeviceId,
    long EntryVersion,
    long AuthorizationVersion,
    long PaperVersion,
    long InstalledPaperVersion,
    PaperTransferOutcome Outcome,
    string? Error);

public sealed record ConfirmPaperReviewApiRequest(
    string RequestId,
    string DeviceId,
    long EntryVersion,
    long AuthorizationVersion,
    long PaperVersion);
