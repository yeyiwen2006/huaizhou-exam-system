using Exam.Contracts.Submissions;

namespace Exam.UnitTests.Administration;

/// <summary>
/// 需求“全部实际参试答卷持久保存后 GUI 弹窗、CLI 发出对应事件；每个名单版本一次，零参试者不触发”
/// 的 CLI 侧状态机用例（合成数据，不连服务器）。
/// </summary>
public sealed class CollectionWatchTests
{
    [Fact]
    public void Accept_EmitsEachRosterVersionOnceInVersionOrder()
    {
        var watch = new CollectionWatch();

        var emitted = watch.Accept("exam-1", [Event("event-3", 3, 20), Event("event-1", 1, 10)]);

        Assert.Equal(new[] { 1L, 3L }, emitted.Select(item => item.RosterVersion));
        Assert.Equal(3, watch.Cursor);
        // 同一页再次出现旧版本时不再发出，游标保持。
        Assert.Empty(watch.Accept("exam-1", [Event("event-1", 1, 10), Event("event-3", 3, 20)]));
        Assert.Equal(3, watch.Cursor);
    }

    [Fact]
    public void Accept_RejectsWithoutAdvancingCursorWhenAnyEntryIsInvalid()
    {
        var watch = new CollectionWatch();
        watch.Accept("exam-1", [Event("event-1", 1, 10)]);

        // 一页里只要有一条无效（零参试），整页都不发出，游标不前进。
        var invalid = new[]
        {
            Event("event-2", 2, 12),
            Event("event-3", 3, 0),
        };

        Assert.Throws<InvalidOperationException>(() => watch.Accept("exam-1", invalid));
        Assert.Equal(1, watch.Cursor);
        Assert.Equal(new[] { 2L }, watch.Accept("exam-1", [Event("event-2", 2, 12)]).Select(item => item.RosterVersion));
    }

    [Theory]
    [InlineData("", 2, 5)]
    [InlineData("event-2", 0, 5)]
    [InlineData("event-2", 2, 0)]
    public void Accept_RejectsEntriesWithoutIdentifierVersionOrParticipants(string eventId, long rosterVersion,
        int participantCount)
    {
        var watch = new CollectionWatch();

        Assert.Throws<InvalidOperationException>(() =>
            watch.Accept("exam-1", [Event(eventId, rosterVersion, participantCount)]));
        Assert.Equal(0, watch.Cursor);
    }

    [Fact]
    public void Accept_RejectsAnotherExamsEvent()
    {
        var watch = new CollectionWatch();

        Assert.Throws<InvalidOperationException>(() => watch.Accept("exam-1", [Event("event-1", 1, 5, "exam-2")]));
        Assert.Equal(0, watch.Cursor);
    }

    [Fact]
    public void Accept_RejectsSameVersionWithDifferentEvents()
    {
        var watch = new CollectionWatch();

        Assert.Throws<InvalidOperationException>(() =>
            watch.Accept("exam-1", [Event("event-1", 4, 5), Event("event-2", 4, 5)]));
        Assert.Equal(0, watch.Cursor);
    }

    [Fact]
    public void Accept_RejectsSameEventWithDifferentVersionsAcrossPages()
    {
        var watch = new CollectionWatch();
        watch.Accept("exam-1", [Event("event-1", 4, 5)]);

        Assert.Throws<InvalidOperationException>(() => watch.Accept("exam-1", [Event("event-1", 5, 5)]));
        Assert.Equal(4, watch.Cursor);
    }

    [Fact]
    public void Accept_RejectsAnEventReusingAnotherEventsVersion()
    {
        var watch = new CollectionWatch();
        watch.Accept("exam-1", [Event("event-1", 4, 5)]);

        // 名单版本 4 已经属于 event-1，换一个事件标识复用同一版本必须拒绝。
        Assert.Throws<InvalidOperationException>(() => watch.Accept("exam-1", [Event("event-2", 4, 5)]));
        Assert.Equal(4, watch.Cursor);
    }

    private static CollectionCompletedNotification Event(string eventId, long rosterVersion, int participantCount,
        string examId = "exam-1") =>
        new(eventId, examId, rosterVersion, participantCount, new DateTimeOffset(2026, 10, 5, 11, 2, 30, TimeSpan.FromHours(8)));
}
