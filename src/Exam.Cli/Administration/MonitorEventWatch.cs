using System.Text.Json;
using Exam.Contracts.Administration;
using Exam.Contracts.Submissions;

/// <summary>CLI 发出的监测事件：回收、设备冲突与失败三类，一行一个 JSON 便于脚本消费。</summary>
public sealed record MonitorEventNotification(
    string ExamId,
    string Kind,
    string ParticipantId,
    string ClientNumber,
    string Candidate,
    string Detail,
    DateTimeOffset ObservedAt)
{
    public const string Collection = "collection";
    public const string Conflict = "conflict";
    public const string Failure = "failure";
}

/// <summary>
/// 监测事件订阅的本地判定：从两次权威快照的差异里找出**新的**设备冲突与失败状态，每类每目标只通知一次。
/// 冲突按“参试记录 + 涉事设备集合”去重，失败按“参试记录 + 失败种类”去重；
/// 连续两次快照里持续存在的同一冲突不会反复提醒，冲突消失后再出现会重新通知。
/// </summary>
public sealed class MonitorEventWatch
{
    private string? _exam;
    private DateTimeOffset _lastSnapshot;
    private Exam.Contracts.Versioning.VersionVector? _versions;
    private long _rosterVersion;
    private readonly HashSet<string> _notifiedConflicts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notifiedFailures = new(StringComparer.Ordinal);

    /// <summary>把一次快照换算成当前存在的冲突与失败标识（用于判定“新”与“消失”）。</summary>
    public static IReadOnlyList<MonitorEventNotification> Describe(string examId, MonitoringSnapshot snapshot,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if(string.IsNullOrWhiteSpace(examId)||snapshot.ExamId!=examId)
            throw new InvalidDataException("监测快照属于另一场考试。");
        if(snapshot.AsOf==default||snapshot.Versions is null||snapshot.ActualRosterVersion<0||
            snapshot.Versions.ScheduleVersion<0||snapshot.Versions.PaperVersion<0||snapshot.Versions.GradingVersion<0||
            snapshot.ActualCount<0||snapshot.CollectedCount<0||snapshot.CollectedCount>snapshot.ActualCount||
            snapshot.Participants is null||snapshot.Uncollected is null||snapshot.Participants.Count>snapshot.ActualCount||
            snapshot.Participants.Any(p=>p is null||string.IsNullOrWhiteSpace(p.ParticipationId)||string.IsNullOrWhiteSpace(p.ClientNumber)||
                p.EntryVersion<1||p.AuthorizationVersion<1||p.Answers is null||p.ConflictDevices is null||p.Commands is null||
                p.ConflictDevices.Any(d=>d is null||string.IsNullOrWhiteSpace(d.DeviceId)||string.IsNullOrWhiteSpace(d.ClientNumber))||
                p.Commands.Any(c=>c is null||string.IsNullOrWhiteSpace(c.EventId)))||
            snapshot.Participants.Select(p=>p.ParticipationId).Distinct(StringComparer.Ordinal).Count()!=snapshot.Participants.Count)
            throw new InvalidOperationException("监测快照不完整或记录重复，整页未采用。");
        var events = new List<MonitorEventNotification>();
        foreach (var participant in snapshot.Participants)
        {
            if (participant.ConflictDevices.Count > 0)
            {
                events.Add(Conflict(examId, participant, observedAt));
            }
            var failure = FailureDetail(participant);
            if (failure is not null)
            {
                events.Add(Failure(examId, participant, failure, observedAt));
            }
        }
        return events;
    }

    /// <summary>
    /// 接收一次快照，返回**本次新出现**的事件；已通知过且仍然存在的不会重复返回。
    /// 冲突或失败消失后会从已通知集合里移除，之后再次出现算新事件。
    /// </summary>
    public IReadOnlyList<MonitorEventNotification> Accept(string examId, MonitoringSnapshot snapshot,
        DateTimeOffset observedAt)
    {
        var current = Describe(examId, snapshot, observedAt);
        if(_exam is not null&&_exam!=examId)throw new InvalidDataException("不能将另一考试的快照混入当前订阅。");
        if(snapshot.AsOf<_lastSnapshot||snapshot.ActualRosterVersion<_rosterVersion||_versions is not null&&
            (snapshot.Versions.ScheduleVersion<_versions.ScheduleVersion||snapshot.Versions.PaperVersion<_versions.PaperVersion||
             snapshot.Versions.GradingVersion<_versions.GradingVersion))
            throw new InvalidOperationException("迟到或版本回退的监测快照未采用。");
        _exam=examId;_lastSnapshot=snapshot.AsOf;_versions=snapshot.Versions;_rosterVersion=snapshot.ActualRosterVersion;
        var currentKeys = new HashSet<string>(current.Select(item => KeyOf(item, snapshot)), StringComparer.Ordinal);
        _notifiedConflicts.RemoveWhere(key => key.StartsWith("conflict|", StringComparison.Ordinal) && !currentKeys.Contains(key));
        _notifiedFailures.RemoveWhere(key => key.StartsWith("failure|", StringComparison.Ordinal) && !currentKeys.Contains(key));

        var fresh = new List<MonitorEventNotification>();
        foreach (var item in current)
        {
            var key = KeyOf(item, snapshot);
            var set = item.Kind == MonitorEventNotification.Conflict ? _notifiedConflicts : _notifiedFailures;
            if (set.Add(key)) fresh.Add(item);
        }
        return fresh;
    }

    /// <summary>按公开编号排序输出，保证同一状态每次得到同样的顺序。</summary>
    public static IReadOnlyList<MonitorEventNotification> Order(IEnumerable<MonitorEventNotification> events) =>
        events.OrderBy(item => item.Kind, StringComparer.Ordinal)
              .ThenBy(item => item.ClientNumber, StringComparer.Ordinal)
              .ThenBy(item => item.Detail, StringComparer.Ordinal)
              .ToArray();

    private static string KeyOf(MonitorEventNotification item, MonitoringSnapshot snapshot)
    {
        var participant = snapshot.Participants.Single(p => p.ParticipationId == item.ParticipantId);
        var state = item.Kind == MonitorEventNotification.Conflict
            ? JsonSerializer.Serialize(participant.ConflictDevices.Select(d => d.DeviceId).Order(StringComparer.Ordinal))
            : JsonSerializer.Serialize(new { participant.TargetPaperVersion,
                failure = FailureDetail(participant),
                commands = participant.Commands.Where(c=>c.Confirmation=="资源更新失败").OrderBy(c=>c.EventId,StringComparer.Ordinal)
                    .Select(c=>new {c.EventId,c.StateVersion}) });
        return item.Kind + "|" + JsonSerializer.Serialize(new { item.ExamId,item.ParticipantId,participant.EntryVersion,state });
    }

    private static MonitorEventNotification Conflict(string examId, MonitoringParticipant participant,
        DateTimeOffset observedAt) => new(
        examId, MonitorEventNotification.Conflict, participant.ParticipationId, participant.ClientNumber,
        participant.Name,
        // 只列公开编号与试场，不透露内部设备标识。
        string.Join("、", participant.ConflictDevices
            .OrderBy(device => device.ClientNumber, StringComparer.Ordinal)
            .Select(device => $"{device.ClientNumber}（{device.RoomGroupName}{(device.IsAuthorized ? "，当前授权" : "，等待裁决")}）")),
        observedAt);

    private static string? FailureDetail(MonitoringParticipant participant)
    {
        var reasons = new List<string>();
        if (string.Equals(participant.DownloadState, "失败", StringComparison.Ordinal))
        {
            reasons.Add("试题包下载失败");
        }
        if (participant.RevisionStatus is { } revision &&
            revision.PaperUpdateState == Exam.Contracts.Versioning.PaperUpdateState.DownloadFailed)
        {
            reasons.Add("修订未安装");
        }
        if(participant.Page=="本地保存失败")reasons.Add("本地答案保存失败");
        if(participant.Commands.Any(c=>c.Confirmation=="资源更新失败"))reasons.Add("指令资源更新失败");
        return reasons.Count == 0 ? null : string.Join("；", reasons);
    }

    private static MonitorEventNotification Failure(string examId, MonitoringParticipant participant, string detail,
        DateTimeOffset observedAt) => new(
        examId, MonitorEventNotification.Failure, participant.ParticipationId, participant.ClientNumber,
        participant.Name, detail, observedAt);
}

/// <summary>收齐事件与监测事件共用的一条输出：stdout 一行一个 JSON。</summary>
public static class MonitorEventLines
{
    public static string? Describe(MonitorEventNotification item) => item.Kind switch
    {
        MonitorEventNotification.Conflict => $"设备冲突待裁决：{item.Candidate}（{item.ClientNumber}）涉事设备 {item.Detail}。",
        MonitorEventNotification.Failure => $"考生状态异常：{item.Candidate}（{item.ClientNumber}）{item.Detail}。",
        _ => null,
    };

    public static string Describe(CollectionCompletedNotification item) =>
        $"本场考试全部答卷已成功回收：{item.ParticipantCount} 名实际参试考生（名单版本 {item.RosterVersion}，" +
        $"{item.CompletedAt.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm:ss}）。";
}
