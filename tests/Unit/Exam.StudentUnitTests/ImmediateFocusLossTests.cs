using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

public sealed partial class SubmissionSyncServiceTests
{
    [Fact]
    public void FocusStartAndCompletionShareStableIdentity()
    {
        var tracker = new FocusTracker();
        Assert.Null(tracker.CurrentLoss);
        Assert.True(tracker.OnFocusLost());
        var initial = Assert.IsType<FocusLossEvent>(tracker.CurrentLoss);
        Assert.Equal(TimeSpan.Zero, initial.Duration);
        Assert.False(tracker.OnFocusLost());
        Assert.Equal(initial.EventId, tracker.CurrentLoss!.EventId);
        Assert.True(tracker.OnFocusRegained());
        var completed = Assert.Single(tracker.Snapshot());
        Assert.Equal(initial.EventId, completed.EventId);
        Assert.Equal(initial.LostAt, completed.LostAt);
        Assert.Equal(1, tracker.FocusLostCount);
        Assert.Null(tracker.CurrentLoss);
    }

    [Fact]
    public async Task FocusStartCanBeRecoveredBeforeTheWindowRegainsFocus()
    {
        await using var fixture = await StartedAsync();
        var tracker = new FocusTracker();
        tracker.OnFocusLost();
        Assert.True(await fixture.Runtime.RecordFocusLossAsync(tracker.CurrentLoss!));
        await fixture.RestartForRecoveryAsync();
        await using var sync = fixture.CreateSync();
        await sync.KickAsync();
        var reported = Assert.Single(Assert.Single(fixture.SubmissionApi.FocusRequests).Events);
        Assert.Equal(tracker.CurrentLoss!.EventId, reported.EventId);
        Assert.Equal(0, reported.DurationMilliseconds);
        Assert.Empty(await fixture.Store.LoadUploadsAsync(fixture.Key, 10));
    }

    [Fact]
    public async Task OfflineStartAndCompletionAreCoalescedWithoutDuplicateEventIds()
    {
        await using var fixture = await StartedAsync();
        var start = new FocusLossEvent(DateTimeOffset.UtcNow.AddSeconds(-5), TimeSpan.Zero);
        Assert.True(await fixture.Runtime.RecordFocusLossAsync(start));
        Assert.True(await fixture.Runtime.RecordFocusLossAsync(start with { Duration = TimeSpan.FromSeconds(5) }));
        Assert.Equal(2, (await fixture.Store.LoadUploadsAsync(fixture.Key, 10)).Count);
        await using var sync = fixture.CreateSync();
        await sync.KickAsync();
        var item = Assert.Single(Assert.Single(fixture.SubmissionApi.FocusRequests).Events);
        Assert.Equal(start.EventId, item.EventId);
        Assert.Equal(5000, item.DurationMilliseconds);
        Assert.Empty(await fixture.Store.LoadUploadsAsync(fixture.Key, 10));
    }

    [Theory]
    [InlineData("wrong-exam")]
    [InlineData("wrong-candidate")]
    [InlineData("wrong-participation")]
    [InlineData("wrong-version")]
    [InlineData("missing-event")]
    [InlineData("null-receipt")]
    public async Task UnrelatedOrIncompleteFocusReceiptCannotErasePendingEvidence(string kind)
    {
        await using var fixture = await StartedAsync();
        Assert.True(await fixture.Runtime.RecordFocusLossAsync(new(DateTimeOffset.UtcNow, TimeSpan.Zero)));
        fixture.SubmissionApi.FocusHandler = request => new("server", "node", 1, "correlation",
            kind == "null-receipt" ? null : new FocusEventsReceipt(
                kind == "wrong-exam" ? "other" : request.ExamId,
                kind == "wrong-candidate" ? "other" : request.CandidateId,
                kind == "wrong-participation" ? "other" : request.ParticipationId,
                kind == "wrong-version" ? request.PaperVersion + 1 : request.PaperVersion,
                kind == "missing-event" ? [] : request.Events.Select(item => item.EventId).ToArray(), DateTimeOffset.UtcNow), null);
        await using var sync = fixture.CreateSync();
        await sync.KickAsync();
        Assert.Single(await fixture.Store.LoadUploadsAsync(fixture.Key, 10));
    }

    [Fact]
    public async Task CompletionEnqueuedDuringStartAcknowledgementRemainsPending()
    {
        await using var fixture = await StartedAsync();
        var start = new FocusLossEvent(DateTimeOffset.UtcNow.AddSeconds(-5), TimeSpan.Zero);
        Assert.True(await fixture.Runtime.RecordFocusLossAsync(start));
        fixture.SubmissionApi.FocusHandler = request =>
        {
            fixture.Runtime.RecordFocusLossAsync(start with { Duration = TimeSpan.FromSeconds(5) }).GetAwaiter().GetResult();
            return new("server", "node", 1, "correlation", new FocusEventsReceipt(request.ExamId, request.CandidateId,
                request.ParticipationId, request.PaperVersion, request.Events.Select(item => item.EventId).ToArray(), DateTimeOffset.UtcNow), null);
        };
        await using var sync = fixture.CreateSync();
        await sync.KickAsync();
        Assert.Single(await fixture.Store.LoadUploadsAsync(fixture.Key, 10));
        fixture.SubmissionApi.FocusHandler = null;
        await sync.KickAsync();
        Assert.Equal(5000, fixture.SubmissionApi.FocusRequests.Last().Events.Single().DurationMilliseconds);
        Assert.Empty(await fixture.Store.LoadUploadsAsync(fixture.Key, 10));
    }
}
