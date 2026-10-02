namespace Exam.Contracts.Administration;

public sealed record OpenDevicePresenceRequest(string RequestId,string ConnectionId);
public sealed record DevicePresenceLease(string ConnectionId,long Generation,DateTimeOffset AcceptedAt);
/// <summary>Sequence 在一次运行中严格递增；服务端接收时间是连接判定依据，不信任客户端墙上时间。</summary>
public sealed record UpdateDevicePresenceRequest(string ConnectionId,long Generation,long Sequence,
    string? SessionId,string? ExamId,string Page,string? CurrentQuestionId,long InstalledPaperVersion,
    string DownloadState,bool Recovering,bool Disconnect = false);
public sealed record DevicePresenceReceipt(long Generation,long Sequence,DateTimeOffset AcceptedAt);
