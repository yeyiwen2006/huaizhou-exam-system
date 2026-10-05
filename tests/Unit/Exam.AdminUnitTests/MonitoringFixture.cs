using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;

namespace Exam.AdminUnitTests;

internal static class MonitoringFixture
{
    public static MonitoringSnapshot Snapshot(IEnumerable<ParticipationResult> rows, string examId = "exam-1")
    {
        var participants = rows.Select(item => Participant(item)).ToArray();
        return new(examId, DateTimeOffset.UtcNow, new(1, 1, 1), ExamLifecycle.Running, 1,
            participants.Length, participants.Count(item => item.ReceiptId is not null), participants,
            participants.Where(item => item.ReceiptId is null).ToArray())
        { Questions = [new("1", 1, "subject-1", "group-1", "single", ["A", "B"])] };
    }

    public static MonitoringParticipant Participant(ParticipationResult item) => new(item.ParticipationId,
        item.CandidateId, "合成考生", "01", "0001", item.AuthorizedDeviceId, "001", "room-1", "第一试场",
        item.BusinessState, DeviceConnectionState.Online, "答题页", "1", DateTimeOffset.UtcNow,
        item.EntryVersion, item.AuthorizationVersion, item.Version, 1, 1, 1, "已安装",
        new Dictionary<string, AnswerValue> { ["1"] = AnswerValue.Selected("A") },
        item.BusinessState == ParticipationBusinessState.Submitted ? "已回收" : "未回收",
        item.BusinessState == ParticipationBusinessState.Submitted ? "receipt-1" : null,
        null, 0, 0, null, [], []);
}
