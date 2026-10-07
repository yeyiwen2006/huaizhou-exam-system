using Exam.Contracts.Submissions;
using Exam.Domain.Submissions;
using Exam.Infrastructure.Postgres.Submissions;

namespace Exam.IntegrationTests.Submissions;

public sealed partial class PostgresSubmissionStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FocusStartAndCompletionAreDurableAndDoNotDoubleCount(bool completionFirst)
    {
        var context = await SeedParticipationAsync();
        var store = new PostgresFocusEventStore(fixture.DataSource);
        var lost = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var completed = new ReportFocusEventsRequest(Id("focus-request"), context.ExamId, context.CandidateId,
            context.ParticipationId, 1, context.DeviceId, 1, 1, [new(Id("focus-event"), lost, lost.AddSeconds(5), 5000)]);
        var initial = completed with { Events = [completed.Events[0] with { RegainedAt = lost, DurationMilliseconds = 0 }] };
        foreach (var request in new[] { completionFirst ? completed : initial, completionFirst ? initial : completed, initial, completed })
            Assert.Equal(FocusEventPersistenceStatus.Saved, (await store.SaveAsync(request, DateTimeOffset.UtcNow)).Status);
        var restored = new PostgresFocusEventStore(fixture.DataSource);
        var summary = Assert.Single(await restored.ListSummariesAsync(context.ExamId));
        Assert.Equal(1, summary.EventCount);
        Assert.Equal(5000, summary.TotalDurationMilliseconds);
        Assert.Equal(lost, summary.LastLostAt);
        var wrongDevice = completed with { DeviceId = Id("other-device") };
        Assert.Equal(FocusEventPersistenceStatus.AuthorizationRevoked,
            (await restored.SaveAsync(wrongDevice, DateTimeOffset.UtcNow)).Status);
    }
}
