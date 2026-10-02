using Exam.Contracts.Activation;

namespace Exam.Contracts.Administration;

public enum DeviceConnectionState { Unknown, Online, Offline, Recovering }
public sealed record RoomGroup(string GroupId, string Name, long Version, int DeviceCount);
public sealed record CreateRoomGroupRequest(string RequestId, string Name);
public sealed record RenameRoomGroupRequest(string RequestId, long ExpectedVersion, string Name);
public sealed record DeleteRoomGroupRequest(string RequestId, long ExpectedVersion);
public sealed record DeletedRoomGroup(string GroupId, bool Deleted);
public sealed record DeviceMoveTarget(string ClientNumber, long ExpectedVersion);
public sealed record MoveDevicesRequest(string RequestId, string? RoomGroupId,
    IReadOnlyList<DeviceMoveTarget> Devices, string Reason);
public sealed record MoveDevicesResult(int Moved, IReadOnlyList<string> ClientNumbers);
public sealed record DeviceRoomHistory(string ClientNumber, string? FromGroupId, string FromGroupName,
    string? ToGroupId, string ToGroupName, string OperatorId, string Reason, DateTimeOffset ChangedAt);
public sealed record DeviceDirectoryEntry(string DeviceId, string ClientNumber, string? RoomGroupId,
    string RoomGroupName, ActivationStatus ActivationStatus, DeviceConnectionState ConnectionState,
    string? CandidateName, string? ClassText, string? AdmissionNo, string? ExamId, string? ExamName,
    string Page, string? CurrentQuestionId, DateTimeOffset? LastCommunicationAt, long Version);
public sealed record DeviceDirectoryPage(IReadOnlyList<DeviceDirectoryEntry> Items, int? NextOffset,
    DateTimeOffset AsOf);
