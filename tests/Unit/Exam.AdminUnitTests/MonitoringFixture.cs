using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;

namespace Exam.AdminUnitTests;

/// <summary>
/// 监测快照的合成夹具：默认构造一份自洽的合法快照，用例只改写要验证的字段。
/// 人数、未回收名单与列表互相自洽，避免用例自己拼出边界校验必然拒绝的结构。
/// </summary>
internal static class MonitoringFixture
{
    public static readonly DateTimeOffset At = new(2026, 10, 5, 9, 30, 0, TimeSpan.FromHours(8));

    public static MonitoringParticipant Participant(
        string participationId = "part-1",
        string candidateId = "candidate-1",
        string name = "合成考生",
        string classText = "001",
        string admissionNo = "00001",
        string clientNumber = "20261005001",
        string? authorizedDeviceId = null,
        string? roomGroupId = "room-1",
        string roomGroupName = "第一试场",
        ParticipationBusinessState businessState = ParticipationBusinessState.Waiting,
        DeviceConnectionState connectionState = DeviceConnectionState.Online,
        DateTimeOffset? lastCommunicationAt = null,
        string page = "正在答题",
        string? currentQuestionId = "1",
        IReadOnlyDictionary<string, AnswerValue>? answers = null,
        string? receiptId = null,
        DateTimeOffset? receivedAt = null,
        string? collectionState = null,
        long entryVersion = 1,
        long authorizationVersion = 2,
        long participationVersion = 3,
        long answerVersion = 1,
        long installedPaperVersion = 1,
        long targetPaperVersion = 1,
        string downloadState = "已下载",
        int focusLossCount = 0,
        long focusLossMilliseconds = 0,
        ParticipantVersionStatus? revisionStatus = null,
        IReadOnlyList<MonitoringCommand>? commands = null,
        IReadOnlyList<ConflictDevice>? conflicts = null, int? currentQuestionNo = 1) => new(
            participationId, candidateId, name, classText, admissionNo,
            authorizedDeviceId ?? "device-" + participationId, clientNumber, roomGroupId, roomGroupName,
            businessState, connectionState, page, currentQuestionId,
            // 没有有效通信记录时不存在最后通信时间（与“未登录”一致）。
            lastCommunicationAt ?? (connectionState == DeviceConnectionState.Unknown ? null : At),
            entryVersion, authorizationVersion, participationVersion, answerVersion,
            installedPaperVersion, targetPaperVersion, downloadState,
            answers ?? new Dictionary<string, AnswerValue>(StringComparer.Ordinal) { ["1"] = AnswerValue.Selected("A") },
            collectionState ?? (receiptId is null ? "未回收" : "已回收"),
            receiptId, receiptId is null ? null : receivedAt ?? At,
            focusLossCount, focusLossMilliseconds, revisionStatus, commands ?? [], conflicts ?? [], currentQuestionNo);

    public static MonitoringSnapshot Snapshot(string examId, params MonitoringParticipant[] participants) =>
        Snapshot(examId, participants, null);

    /// <summary>默认快照：exam-1 里一名候考中、在线、未回收的考生。</summary>
    public static MonitoringSnapshot Default() => Snapshot("exam-1", Participant());

    public static MonitoringSnapshot Snapshot(string examId, IReadOnlyList<MonitoringParticipant> participants,
        IReadOnlyList<MonitoringParticipant>? uncollected, ExamLifecycle lifecycle = ExamLifecycle.Running,
        DateTimeOffset? asOf = null)
    {
        var missing = uncollected ?? participants.Where(item => item.ReceiptId is null).ToArray();
        return new MonitoringSnapshot(examId, asOf ?? At, new VersionVector(3, 2, 1), lifecycle, 7,
            participants.Count, participants.Count - missing.Count, participants, missing);
    }

    public static MonitoringCommand Command(CommandEventType type = CommandEventType.End, bool canResend = true,
        string eventId = "event-1", string confirmation = "待确认", DateTimeOffset? acknowledgedAt = null) =>
        new(eventId, type, 1, confirmation, At.AddMinutes(-5), acknowledgedAt, canResend);

    public static ConflictDevice Conflict(string deviceId = "device-part-1", bool authorized = true,
        DeviceConnectionState connectionState = DeviceConnectionState.Online, long answerVersion = 4,
        string page = "正在答题") => new(deviceId, "20261005009", "第一试场", connectionState, At, authorized,
            answerVersion, page);
}
