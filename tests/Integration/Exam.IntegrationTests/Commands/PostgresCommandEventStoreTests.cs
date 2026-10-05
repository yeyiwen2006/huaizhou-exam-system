using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Domain.Commands;
using Exam.Infrastructure.Postgres.Commands;
using Exam.IntegrationTests.Postgres;

namespace Exam.IntegrationTests.Commands;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresCommandEventStoreTests(PostgresDatabaseFixture fixture)
{
    [Fact]
    public async Task ConcurrentCreate_WithSameRequestId_PersistsOneOriginalEvent()
    {
        var requestId = Id("create-request");
        var firstEvent = CreateEvent(Id("event-a"));
        var secondEvent = CreateEvent(Id("event-b"));
        var firstStore = new PostgresCommandEventStore(fixture.DataSource);
        var secondStore = new PostgresCommandEventStore(fixture.DataSource);

        var results = await Task.WhenAll(
            firstStore.CreateOrGetAsync(requestId, firstEvent).AsTask(),
            secondStore.CreateOrGetAsync(requestId, secondEvent).AsTask());

        Assert.Single(results, result => result.Status == CommandCreatePersistenceStatus.Created);
        Assert.Single(results, result => result.Status == CommandCreatePersistenceStatus.RequestReplay);
        Assert.Single(results.Select(result => result.Event.EventId).Distinct(StringComparer.Ordinal));
        var replay = await firstStore.FindByCreateRequestAsync(requestId);
        Assert.Equal(results[0].Event.EventId, replay!.EventId);
    }

    [Fact]
    public async Task ConcurrentCreate_WithSameEventId_ReturnsOneConflictInsteadOfThrowing()
    {
        var commandEvent = CreateEvent(Id("shared-event"));
        var firstStore = new PostgresCommandEventStore(fixture.DataSource);
        var secondStore = new PostgresCommandEventStore(fixture.DataSource);

        var results = await Task.WhenAll(
            firstStore.CreateOrGetAsync(Id("request-a"), commandEvent).AsTask(),
            secondStore.CreateOrGetAsync(Id("request-b"), commandEvent).AsTask());

        Assert.Single(results, result => result.Status == CommandCreatePersistenceStatus.Created);
        Assert.Single(results, result => result.Status == CommandCreatePersistenceStatus.EventIdConflict);
        Assert.All(results, result => Assert.Equal(commandEvent.EventId, result.Event.EventId));
    }

    [Fact]
    public async Task DueClaim_IsExclusiveAcrossProcessesAndLeaseMakesCrashRecoverable()
    {
        var now = new DateTimeOffset(2026, 9, 22, 2, 0, 0, TimeSpan.Zero);
        var commandEvent = CreateEvent(Id("due-event"), now);
        var creator = new PostgresCommandEventStore(fixture.DataSource, TimeSpan.FromMilliseconds(200));
        await creator.CreateOrGetAsync(Id("create-request"), commandEvent);
        var firstStore = new PostgresCommandEventStore(fixture.DataSource, TimeSpan.FromMilliseconds(200));
        var secondStore = new PostgresCommandEventStore(fixture.DataSource, TimeSpan.FromMilliseconds(200));

        var claims = await Task.WhenAll(
            ClaimAsync(firstStore, commandEvent, now),
            ClaimAsync(secondStore, commandEvent, now));

        Assert.Equal(1, claims.Sum(batch => batch.Count));
        var recovered = await ClaimAsync(firstStore, commandEvent, now.AddMilliseconds(201));
        Assert.Single(recovered);
        Assert.Equal(commandEvent.EventId, recovered[0].EventId);
    }

    [Fact]
    public async Task DueClaim_DoesNotLeaseAnotherDevicesEvent()
    {
        var now = new DateTimeOffset(2026, 9, 22, 2, 0, 0, TimeSpan.Zero);
        var target = CreateEvent(Id("target"), now);
        var other = CreateEvent(Id("other"), now) with
        {
            ParticipationId = Id("other-participation"),
            TargetDeviceId = Id("other-device")
        };
        var store = new PostgresCommandEventStore(fixture.DataSource, TimeSpan.FromMinutes(1));
        await store.CreateOrGetAsync(Id("create-target"), target);
        await store.CreateOrGetAsync(Id("create-other"), other);

        var claimedTarget = await ClaimAsync(store, target, now);
        var claimedOther = await ClaimAsync(store, other, now);

        Assert.Single(claimedTarget);
        Assert.Single(claimedOther);
        Assert.Equal(target.EventId, claimedTarget[0].EventId);
        Assert.Equal(other.EventId, claimedOther[0].EventId);
    }

    [Fact]
    public async Task TerminalAcknowledgement_IsPermanentAndRequestIdsCannotCrossEvents()
    {
        var store = new PostgresCommandEventStore(fixture.DataSource);
        var first = CreateEvent(Id("event-terminal"));
        var second = CreateEvent(Id("event-other"));
        await store.CreateOrGetAsync(Id("create-terminal"), first);
        await store.CreateOrGetAsync(Id("create-other"), second);
        var applied = new CommandAcknowledgement(
            first.EventId,
            Id("ack-terminal"),
            CommandProcessingResult.Applied,
            1,
            null,
            DateTimeOffset.UtcNow);

        var saved = await store.AcknowledgeAsync(applied.RequestId, first.EventId, applied);
        var laterFailure = new CommandAcknowledgement(
            first.EventId,
            Id("ack-later"),
            CommandProcessingResult.ResourceUpdateFailed,
            1,
            "late failure",
            applied.AcknowledgedAt.AddSeconds(1));
        var immutable = await store.AcknowledgeAsync(
            laterFailure.RequestId,
            first.EventId,
            laterFailure);
        var crossEvent = await store.AcknowledgeAsync(applied.RequestId, second.EventId, applied with
        {
            EventId = second.EventId
        });
        var stored = await store.FindAsync(first.EventId);

        Assert.Equal(CommandAckPersistenceStatus.Acknowledged, saved.Status);
        Assert.Equal(CommandAckPersistenceStatus.RequestReplay, immutable.Status);
        Assert.Equal(CommandProcessingResult.Applied, immutable.Acknowledgement!.ProcessingResult);
        Assert.Equal(applied.RequestId, immutable.Acknowledgement.RequestId);
        Assert.Equal(CommandAckPersistenceStatus.RequestIdConflict, crossEvent.Status);
        Assert.Equal(CommandProcessingResult.Applied, stored!.Acknowledgement!.ProcessingResult);
    }

    [Fact]
    public async Task ManualEndRetry_RecordsAttemptWithoutErasingAppliedAcknowledgement()
    {
        var store = new PostgresCommandEventStore(fixture.DataSource);
        var command = CreateEvent(Id("end-retry")) with { EventType = CommandEventType.End, ProtectedUnlockReference = null };
        await store.CreateOrGetAsync(Id("create-end"), command);
        var acknowledgement = new CommandAcknowledgement(command.EventId, Id("ack-end"), CommandProcessingResult.Applied,
            1, null, DateTimeOffset.UtcNow);
        await store.AcknowledgeAsync(acknowledgement.RequestId, command.EventId, acknowledgement);
        var now = DateTimeOffset.UtcNow;
        Assert.Null(await store.RecordDeliveryAttemptAsync(command.EventId, now, now));
        var retry = await store.RecordManualDeliveryAttemptAsync(command.EventId, now, now);
        Assert.NotNull(retry);
        Assert.Equal(1, retry.DeliveryAttempts);
        Assert.Equal(CommandProcessingResult.Applied, retry.Acknowledgement!.ProcessingResult);
        Assert.Equal(acknowledgement.RequestId, retry.Acknowledgement.RequestId);
        Assert.Empty(await ClaimAsync(store, command, now.AddMinutes(1)));
    }

    private static PersistentCommandEvent CreateEvent(
        string eventId,
        DateTimeOffset? createdAt = null)
    {
        var now = createdAt ?? DateTimeOffset.UtcNow;
        return new PersistentCommandEvent(
            eventId,
            Id("exam"),
            Id("participation"),
            1,
            CommandEventType.Start,
            1,
            Id("device"),
            1,
            1,
            "protected:key-reference",
            new Dictionary<string, string> { ["source"] = "integration-test" },
            now,
            now,
            0,
            null);
    }

    private static Task<IReadOnlyList<PersistentCommandEvent>> ClaimAsync(
        PostgresCommandEventStore store,
        PersistentCommandEvent commandEvent,
        DateTimeOffset now) =>
        store.ClaimDueForDeviceAsync(
            commandEvent.ParticipationId!,
            commandEvent.EntryVersion,
            commandEvent.TargetDeviceId!,
            commandEvent.TargetAuthorizationVersion!.Value,
            now).AsTask();

    private static string Id(string prefix) => $"{prefix}-{Guid.NewGuid():N}";
}
