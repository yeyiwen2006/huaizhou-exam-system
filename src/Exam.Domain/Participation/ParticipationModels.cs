using Exam.Contracts.Common;
using Exam.Contracts.Participation;

namespace Exam.Domain.Participation;

public sealed record StudentSession(
    string SessionId,
    string DeviceId,
    CandidateIdentity Candidate,
    IReadOnlyList<string> ExamIds,
    DateTimeOffset AuthenticatedAt,
    IReadOnlyDictionary<string, string> CandidateIdsByExam);

public sealed record EntryPreparation(
    string PreparationId,
    string SessionId,
    string ExamId,
    string CandidateId,
    string DeviceId,
    long PaperVersion,
    long InstructionsVersion,
    DateTimeOffset PreparedAt);

public sealed record ParticipationRecord(
    string ParticipationId,
    string ExamId,
    string CandidateId,
    long EntryVersion,
    ParticipationBusinessState BusinessState,
    string AuthorizedDeviceId,
    long AuthorizationVersion,
    long Version)
{
    public ParticipationRecord Reenter(string deviceId, ParticipationBusinessState state) => this with
    {
        EntryVersion = checked(EntryVersion + 1),
        BusinessState = state,
        AuthorizedDeviceId = deviceId,
        AuthorizationVersion = checked(AuthorizationVersion + 1),
        Version = checked(Version + 1)
    };

    public ParticipationRecord Withdraw() => this with
    {
        BusinessState = ParticipationBusinessState.NotParticipating,
        Version = checked(Version + 1)
    };

    public ParticipationRecord DecideDevice(string deviceId) => this with
    {
        AuthorizedDeviceId = deviceId,
        AuthorizationVersion = checked(AuthorizationVersion + 1),
        Version = checked(Version + 1)
    };

    public ParticipationRecord MarkSubmitted() => this with
    {
        BusinessState = ParticipationBusinessState.Submitted,
        Version = checked(Version + 1)
    };
}

public sealed record DeviceConflict(
    string ParticipationId,
    string OriginalDeviceId,
    string PendingDeviceId,
    DateTimeOffset DetectedAt);

public sealed record DeviceDecisionAudit(
    string DecisionId,
    string ParticipationId,
    string SelectedDeviceId,
    string RevokedDeviceId,
    long AuthorizationVersion,
    string OperatorId,
    string Reason,
    DateTimeOffset DecidedAt);

public sealed record ParticipationIdempotencyRecord(
    string RequestId,
    string Operation,
    string PayloadHash,
    string ResponseType,
    string ResponseJson);
