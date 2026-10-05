using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;

namespace Exam.Contracts.Administration;

public sealed record MonitoringCommand(string EventId,CommandEventType EventType,long StateVersion,
    string Confirmation,DateTimeOffset CreatedAt,DateTimeOffset? AcknowledgedAt,bool CanResend);
public sealed record ConflictDevice(string DeviceId,string ClientNumber,string RoomGroupName,
    DeviceConnectionState ConnectionState,DateTimeOffset? LastCommunicationAt,bool IsAuthorized,
    long AnswerVersion,string Page)
{
    public DateTimeOffset? LastSyncedAt { get; init; }
    public int SavedAnswerCount { get; init; }
}
public sealed record MonitoringParticipant(string ParticipationId,string CandidateId,string Name,string ClassText,
    string AdmissionNo,string AuthorizedDeviceId,string ClientNumber,string? RoomGroupId,string RoomGroupName,
    ParticipationBusinessState BusinessState,DeviceConnectionState ConnectionState,string Page,string? CurrentQuestionId,
    DateTimeOffset? LastCommunicationAt,long EntryVersion,long AuthorizationVersion,long ParticipationVersion,
    long AnswerVersion,long InstalledPaperVersion,long TargetPaperVersion,string DownloadState,
    IReadOnlyDictionary<string,AnswerValue> Answers,string CollectionState,string? ReceiptId,DateTimeOffset? ReceivedAt,
    int FocusLossCount,long FocusLossMilliseconds,ParticipantVersionStatus? RevisionStatus,
    IReadOnlyList<MonitoringCommand> Commands,IReadOnlyList<ConflictDevice> ConflictDevices);
public sealed record MonitoringSnapshot(string ExamId,DateTimeOffset AsOf,VersionVector Versions,
    ExamLifecycle Lifecycle,long ActualRosterVersion,int ActualCount,int CollectedCount,
    IReadOnlyList<MonitoringParticipant> Participants,IReadOnlyList<MonitoringParticipant> Uncollected)
{
    public IReadOnlyList<PaperQuestionStructure> Questions { get; init; } = [];
}
