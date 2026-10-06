using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;

namespace Exam.UnitTests.Administration;

/// <summary>
/// 监测事件订阅的本地判定：冲突与失败每类每目标只通知一次、消失后再出现重新通知，
/// 输出只用公开编号与试场名称，不暴露内部设备标识。
/// </summary>
public sealed class MonitorEventWatchTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 6, 9, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Accept_ReportsNewConflictsAndFailuresOnceEach()
    {
        var watch = new MonitorEventWatch();
        var snapshot = Snapshot(
            Participant(clientNumber: "20261006001", conflicts: [Conflict("设备甲", "第一试场", authorized: true)]),
            Participant(participationId: "part-2", candidateId: "cand-2", clientNumber: "20261006002", downloadState: "失败"));

        var first = MonitorEventWatch.Order(watch.Accept("exam-1", snapshot, ObservedAt));
        Assert.Equal(new[] { MonitorEventNotification.Conflict, MonitorEventNotification.Failure },
            first.Select(item => item.Kind));
        Assert.Contains("设备甲", first[0].Detail, StringComparison.Ordinal);
        Assert.Contains("试题包下载失败", first[1].Detail, StringComparison.Ordinal);

        // 状态没变就不重复通知：持续存在的冲突与失败不得每轮刷屏。
        Assert.Empty(watch.Accept("exam-1", snapshot, ObservedAt));
    }

    [Fact]
    public void Accept_NotifiesAgainAfterTheConflictDisappearsAndReturns()
    {
        var watch = new MonitorEventWatch();
        var conflict = Snapshot(Participant(clientNumber: "20261006001", conflicts: [Conflict("设备甲", "第一试场", true)]));
        var clear = Snapshot(Participant(clientNumber: "20261006001"));

        Assert.Single(watch.Accept("exam-1", conflict, ObservedAt));
        Assert.Empty(watch.Accept("exam-1", clear, ObservedAt));
        Assert.Single(watch.Accept("exam-1", conflict, ObservedAt));
    }

    [Fact]
    public void Accept_TreatsADifferentDeviceSetAsANewConflict()
    {
        var watch = new MonitorEventWatch();
        var twoDevices = Snapshot(Participant(clientNumber: "20261006001",
            conflicts: [Conflict("设备甲", "第一试场", true), Conflict("设备乙", "第二试场", false)]));
        var oneDevice = Snapshot(Participant(clientNumber: "20261006001",
            conflicts: [Conflict("设备甲", "第一试场", true)]));

        Assert.Single(watch.Accept("exam-1", twoDevices, ObservedAt));
        Assert.Single(watch.Accept("exam-1", oneDevice, ObservedAt));
    }

    [Fact]
    public void Describe_UsesPublicNumbersAndNeverInternalDeviceIdentifiers()
    {
        var events = MonitorEventWatch.Describe("exam-1",
            Snapshot(Participant(clientNumber: "20261006001",
                conflicts: [Conflict("设备甲", "第一试场", true)], deviceId: "internal-device-9")), ObservedAt);

        var conflict = Assert.Single(events);
        // 明细列出涉事设备的公开编号与试场；考生本人的公开编号在事件字段里。
        Assert.Contains("设备甲", conflict.Detail, StringComparison.Ordinal);
        Assert.Contains("第一试场", conflict.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(conflict.Detail, "internal-device-9", StringComparison.Ordinal);
        Assert.Equal("20261006001", conflict.ClientNumber);
    }

    [Fact]
    public void Describe_ReportsBothDownloadFailureAndMissingRevisionInOneEvent()
    {
        var events = MonitorEventWatch.Describe("exam-1", Snapshot(Participant(
            clientNumber: "20261006001", downloadState: "失败",
            revision: new ParticipantVersionStatus("part-1", ParticipationBusinessState.Answering, 1, 2,
                PaperUpdateState.DownloadFailed, null))), ObservedAt);

        var failure = Assert.Single(events);
        Assert.Contains("试题包下载失败", failure.Detail, StringComparison.Ordinal);
        Assert.Contains("修订未安装", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectedLateSnapshotCannotClearNotificationDeduplication()
    {
        var watch=new MonitorEventWatch();
        var conflict=Snapshot(Participant(conflicts:[Conflict("设备甲","第一试场",true)]));
        Assert.Single(watch.Accept("exam-1",conflict,ObservedAt));
        Assert.Throws<InvalidOperationException>(()=>watch.Accept("exam-1",
            Snapshot(Participant()) with{AsOf=ObservedAt.AddSeconds(-1)},ObservedAt));
        Assert.Empty(watch.Accept("exam-1",conflict,ObservedAt));
        Assert.Throws<InvalidDataException>(()=>watch.Accept("exam-2",conflict with{ExamId="exam-2"},ObservedAt));
    }

    [Fact]
    public void InvalidRowsRejectWholePageBeforeDeduplicationChanges()
    {
        var watch=new MonitorEventWatch();var p=Participant(conflicts:[Conflict("设备甲","第一试场",true)]);
        Assert.Throws<InvalidOperationException>(()=>watch.Accept("exam-1",Snapshot(p,p),ObservedAt));
        Assert.Throws<InvalidOperationException>(()=>watch.Accept("exam-1",Snapshot(p,null!),ObservedAt));
        Assert.Single(watch.Accept("exam-1",Snapshot(p),ObservedAt));
        Assert.Throws<InvalidOperationException>(()=>watch.Accept("exam-1",Snapshot(p) with{Versions=new(0,0,0)},ObservedAt));
        Assert.Empty(watch.Accept("exam-1",Snapshot(p),ObservedAt));
    }

    [Fact]
    public void ANewEntryAndNewFailedCommandAreNewNotifications()
    {
        var watch=new MonitorEventWatch();var p=Participant() with{Page="本地保存失败"};
        Assert.Contains("本地答案保存失败",Assert.Single(watch.Accept("exam-1",Snapshot(p),ObservedAt)).Detail);
        Assert.Empty(watch.Accept("exam-1",Snapshot(p),ObservedAt));
        Assert.Single(watch.Accept("exam-1",Snapshot(p with{EntryVersion=2}),ObservedAt));
        var command=new MonitoringCommand("command-1",Exam.Contracts.Common.CommandEventType.PaperUpdated,1,"资源更新失败",ObservedAt,null,false);
        var failed=p with{Page="正在答题",Commands=[command]};
        Assert.Contains("指令资源更新失败",Assert.Single(watch.Accept("exam-1",Snapshot(failed),ObservedAt)).Detail);
        Assert.Empty(watch.Accept("exam-1",Snapshot(failed),ObservedAt));
        Assert.Single(watch.Accept("exam-1",Snapshot(failed with{Commands=[command with{EventId="command-2",StateVersion=2}]}),ObservedAt));
    }

    private static MonitoringSnapshot Snapshot(params MonitoringParticipant[] participants) =>
        new("exam-1", ObservedAt, new VersionVector(1, 1, 1), ExamLifecycle.Running, 1, participants.Length, 0,
            participants, []);

    private static MonitoringParticipant Participant(
        string participationId = "part-1", string candidateId = "cand-1", string clientNumber = "20261006001",
        string downloadState = "已下载", string deviceId = "internal-device-1",
        IReadOnlyList<ConflictDevice>? conflicts = null, ParticipantVersionStatus? revision = null) => new(
        participationId, candidateId, "合成考生", "001", "00001", deviceId, clientNumber, "room-1", "第一试场",
        ParticipationBusinessState.Answering, DeviceConnectionState.Online, "正在答题", "q-1", ObservedAt,
        1, 1, 1, 1, 1, 1, downloadState,
        new Dictionary<string, AnswerValue>(StringComparer.Ordinal), "未回收", null, null, 0, 0, revision,
        [], conflicts ?? []);

    private static ConflictDevice Conflict(string clientNumber, string roomGroupName, bool authorized) =>
        new($"internal-{clientNumber}", clientNumber, roomGroupName, DeviceConnectionState.Online, ObservedAt,
            authorized, 1, "正在答题");
}
