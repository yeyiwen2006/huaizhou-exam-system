using Exam.Admin.ViewModels;

namespace Exam.AdminUnitTests;

public sealed partial class CollectionPollingTests
{
    [Theory]
    [InlineData(false, "id")]
    [InlineData(false, "count")]
    [InlineData(false, "time")]
    [InlineData(true, "id")]
    [InlineData(true, "count")]
    [InlineData(true, "time")]
    public async Task Review_HistoricalConflictsRejectWholePageWithoutAdvancingCursor(bool reconnect, string change)
    {
        var conflict = false;
        var cursors = new List<long>();
        var original = Event("exam-a", 1);
        var altered = change switch
        {
            "id" => original with { EventId = "changed-id" },
            "count" => original with { ParticipantCount = 301 },
            _ => original with { CompletedAt = original.CompletedAt.AddSeconds(1) }
        };
        using var handler = new Handler((request, _) =>
        {
            cursors.Add(Cursor(request));
            return Task.FromResult(Success(conflict ? new[] { altered, Event("exam-a", 2) } : new[] { original }));
        });
        using var api = Configured(handler);
        using var vm = new MonitoringViewModel(api) { ExamId = "exam-a" };
        await vm.RefreshCollectionEventsAsync();
        if (reconnect)
        {
            api.Disconnect();
            api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        }
        conflict = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.RefreshCollectionEventsAsync());
        Assert.DoesNotContain(vm.CollectionEvents, x => x.RosterVersion == 2);
        conflict = false;
        await vm.RefreshCollectionEventsAsync();
        Assert.Equal(reconnect ? 0 : 1, cursors[^1]);
    }

    [Fact]
    public async Task Review_ZeroParticipantsNeverProduceACollectionReminder()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Success(new[] { Event("exam-a", 1) with { ParticipantCount = 0 } })));
        using var api = Configured(handler);
        using var vm = new MonitoringViewModel(api) { ExamId = "exam-a" };
        var reminders = 0;
        vm.CollectionCompleted += _ => reminders++;
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.RefreshCollectionEventsAsync());
        Assert.Empty(vm.CollectionEvents);
        Assert.Equal(0, reminders);
    }
}
