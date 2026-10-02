using Exam.Application.Commands;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;

namespace Exam.UnitTests.Commands;

public sealed class ReliableCommandServiceTests
{
    [Fact]
    public async Task ScheduleDelivery_CarriesPersistedTimesAndKeepsThemAcrossRetry()
    {
        var fixture = new CommandFixture();
        var start = new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.FromHours(8));
        var request = fixture.CreateRequest(CommandEventType.ScheduleChanged, unlockReference: null) with
        {
            Metadata = new Dictionary<string, string>
            {
                ["scheduledStart"] = start.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["scheduledEnd"] = start.AddHours(2).ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            }
        };
        Assert.True((await fixture.Service.CreateAsync(request)).IsSuccess);
        var first = Assert.Single((await fixture.GetDueAsync()).Value!);
        Assert.Equal(start, first.NewScheduledStart);
        Assert.Equal(start.AddHours(2), first.NewScheduledEnd);
        Assert.Equal(TimeSpan.FromHours(8), first.NewScheduledEnd!.Value.Offset);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        var retry = Assert.Single((await fixture.GetDueAsync()).Value!);
        Assert.Equal(first.NewScheduledStart, retry.NewScheduledStart);
        Assert.Equal(first.NewScheduledEnd, retry.NewScheduledEnd);
    }

    [Fact]
    public void ServerTime_UsesServerClockAndIncludesNodeIdentity()
    {
        var fixture = new CommandFixture();

        var snapshot = fixture.Service.GetServerTime();

        Assert.Equal(fixture.Clock.GetUtcNow(), snapshot.ServerTime);
        Assert.Equal(fixture.Server, snapshot.Server);
    }

    [Fact]
    public async Task Start_CannotBeCreatedBeforeExamIsRunning()
    {
        var fixture = new CommandFixture(ExamLifecycle.Scheduled);

        var result = await fixture.Service.CreateAsync(fixture.CreateRequest(CommandEventType.Start));

        Assert.False(result.IsSuccess);
        Assert.Equal(CommandErrorCode.KeyNotReleased, result.ErrorCode);
        Assert.Null(await fixture.Store.FindAsync("event-1"));
    }

    [Fact]
    public async Task MissingNotification_IsRedeliveredWithSameEventIdAfterBackoff()
    {
        var fixture = new CommandFixture();
        var created = await fixture.Service.CreateAsync(fixture.CreateRequest(CommandEventType.Start));
        Assert.True(created.IsSuccess);

        var first = await fixture.GetDueAsync();
        Assert.Single(first.Value!);
        Assert.Equal("event-1", first.Value![0].EventId);
        Assert.Equal("protected:key-reference", first.Value[0].ProtectedUnlockReference);
        Assert.Equal(1, first.Value[0].DeliveryAttempt);

        var tooSoon = await fixture.GetDueAsync();
        Assert.Empty(tooSoon.Value!);

        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var retry = await fixture.GetDueAsync();
        Assert.Single(retry.Value!);
        Assert.Equal("event-1", retry.Value![0].EventId);
        Assert.Equal(2, retry.Value[0].DeliveryAttempt);
    }

    [Fact]
    public async Task PersistedPendingEvent_IsRecoveredByNewServiceInstance()
    {
        var fixture = new CommandFixture();
        await fixture.Service.CreateAsync(fixture.CreateRequest(CommandEventType.Start));

        var restarted = new ReliableCommandService(
            fixture.Server,
            fixture.Store,
            fixture.Access,
            fixture.Clock);
        var due = await restarted.GetDueForDeviceAsync(
            "participation-1",
            1,
            "device-1",
            1,
            "server-1",
            7);

        Assert.True(due.IsSuccess);
        Assert.Single(due.Value!);
    }

    [Fact]
    public async Task AppliedAcknowledgement_IsIdempotentAndStopsRedelivery()
    {
        var fixture = new CommandFixture();
        await fixture.Service.CreateAsync(fixture.CreateRequest(CommandEventType.Start));
        await fixture.GetDueAsync();
        var request = fixture.Ack(CommandProcessingResult.Applied, "ack-1");

        var first = await fixture.Service.AcknowledgeAsync(request);
        fixture.Access.Set(fixture.AccessState with
        {
            AuthorizedDeviceId = "device-2",
            AuthorizationVersion = 2
        });
        var replay = await fixture.Service.AcknowledgeAsync(request with
        {
            ProcessingResult = CommandProcessingResult.ResourceUpdateFailed,
            FailureReason = "ignored replay payload"
        });
        fixture.Access.Set(fixture.AccessState);
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        var due = await fixture.GetDueAsync();

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value, replay.Value);
        Assert.Equal(CommandProcessingResult.Applied, replay.Value!.ProcessingResult);
        Assert.Empty(due.Value!);
    }

    [Fact]
    public async Task TerminalAcknowledgement_CannotBeOverwrittenByLaterFailure()
    {
        var fixture = new CommandFixture();
        await fixture.Service.CreateAsync(fixture.CreateRequest(CommandEventType.Start));
        await fixture.GetDueAsync();
        var terminal = await fixture.Service.AcknowledgeAsync(
            fixture.Ack(CommandProcessingResult.Applied, "ack-terminal"));

        var attemptedOverwrite = await fixture.Service.AcknowledgeAsync(
            fixture.Ack(CommandProcessingResult.ResourceUpdateFailed, "ack-later-failure") with
            {
                FailureReason = "late failure must not reopen terminal event"
            });
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        var due = await fixture.GetDueAsync();
        var stored = await fixture.Store.FindAsync("event-1");

        Assert.True(terminal.IsSuccess);
        Assert.True(attemptedOverwrite.IsSuccess);
        Assert.Equal(CommandProcessingResult.Applied, attemptedOverwrite.Value!.ProcessingResult);
        Assert.Equal("ack-terminal", attemptedOverwrite.Value.RequestId);
        Assert.Equal(CommandProcessingResult.Applied, stored!.Acknowledgement!.ProcessingResult);
        Assert.Empty(due.Value!);
    }

    [Fact]
    public async Task FailedResourceUpdate_RemainsPendingUntilActualVersionIsApplied()
    {
        var fixture = new CommandFixture();
        await fixture.Service.CreateAsync(fixture.CreateRequest(
            CommandEventType.PaperUpdated,
            resourceVersion: 3,
            unlockReference: null));
        await fixture.GetDueAsync();

        var failed = await fixture.Service.AcknowledgeAsync(
            fixture.Ack(CommandProcessingResult.ResourceUpdateFailed, "ack-failed") with
            {
                ActualPaperVersion = 2,
                FailureReason = "download failed"
            });
        var falseSuccess = await fixture.Service.AcknowledgeAsync(
            fixture.Ack(CommandProcessingResult.Applied, "ack-too-old") with { ActualPaperVersion = 2 });
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        var retry = await fixture.GetDueAsync();
        var applied = await fixture.Service.AcknowledgeAsync(
            fixture.Ack(CommandProcessingResult.Applied, "ack-applied") with { ActualPaperVersion = 3 });

        Assert.True(failed.IsSuccess);
        Assert.False(falseSuccess.IsSuccess);
        Assert.Equal(CommandErrorCode.VersionConflict, falseSuccess.ErrorCode);
        Assert.Single(retry.Value!);
        Assert.True(applied.IsSuccess);
    }

    [Fact]
    public async Task RevokedDevice_CannotReceiveUnlockReferenceOrAcknowledge()
    {
        var fixture = new CommandFixture();
        await fixture.Service.CreateAsync(fixture.CreateRequest(CommandEventType.Start));
        fixture.Access.Set(fixture.AccessState with
        {
            AuthorizedDeviceId = "device-2",
            AuthorizationVersion = 2
        });

        var delivery = await fixture.GetDueAsync();
        var acknowledgement = await fixture.Service.AcknowledgeAsync(
            fixture.Ack(CommandProcessingResult.Applied, "ack-revoked"));
        var resend = await fixture.Service.ResendAsync(
            "event-1",
            "participation-1",
            1,
            "device-1",
            1);

        Assert.False(delivery.IsSuccess);
        Assert.Equal(CommandErrorCode.AuthorizationRevoked, delivery.ErrorCode);
        Assert.False(acknowledgement.IsSuccess);
        Assert.Equal(CommandErrorCode.AuthorizationRevoked, acknowledgement.ErrorCode);
        Assert.False(resend.IsSuccess);
        Assert.Equal(CommandErrorCode.AuthorizationRevoked, resend.ErrorCode);
    }

    [Fact]
    public async Task EndEvent_IsDeliverableOnlyAfterExamReachedEndedState()
    {
        var running = new CommandFixture(ExamLifecycle.Running);
        var rejected = await running.Service.CreateAsync(running.CreateRequest(CommandEventType.End));
        Assert.False(rejected.IsSuccess);
        Assert.Equal(CommandErrorCode.VersionConflict, rejected.ErrorCode);

        var ended = new CommandFixture(ExamLifecycle.Ended);
        var created = await ended.Service.CreateAsync(ended.CreateRequest(CommandEventType.End));
        var delivered = await ended.GetDueAsync();
        var acknowledged = await ended.Service.AcknowledgeAsync(
            ended.Ack(CommandProcessingResult.Applied, "ack-end"));

        Assert.True(created.IsSuccess);
        Assert.Equal(CommandEventType.End, Assert.Single(delivered.Value!).EventType);
        Assert.True(acknowledged.IsSuccess);
    }

    [Fact]
    public async Task ClientWithStaleServerEpoch_MustRefreshBeforeReceivingEvents()
    {
        var fixture = new CommandFixture();
        await fixture.Service.CreateAsync(fixture.CreateRequest(CommandEventType.Start));

        var result = await fixture.Service.GetDueForDeviceAsync(
            "participation-1",
            1,
            "device-1",
            1,
            "server-1",
            6);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommandErrorCode.StaleServerEpoch, result.ErrorCode);
    }

    [Fact]
    public async Task CreateRequestReplay_ReturnsOriginalEvenAfterAuthorizationChanges()
    {
        var fixture = new CommandFixture();
        var request = fixture.CreateRequest(CommandEventType.Start);
        var first = await fixture.Service.CreateAsync(request);
        fixture.Access.Set(fixture.AccessState with
        {
            AuthorizedDeviceId = "device-2",
            AuthorizationVersion = 2
        });

        var replay = await fixture.Service.CreateAsync(request with
        {
            EventId = "changed-event-id",
            TargetDeviceId = "device-2",
            TargetAuthorizationVersion = 2
        });

        Assert.True(first.IsSuccess);
        Assert.True(replay.IsSuccess);
        Assert.Equal("event-1", replay.Value!.EventId);
    }

    private sealed class CommandFixture
    {
        public CommandFixture(ExamLifecycle lifecycle = ExamLifecycle.Running)
        {
            Clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 22, 1, 0, 0, TimeSpan.Zero));
            Server = new ServerNodeEnvelope("server-1", "node-a", 7);
            Store = new InMemoryCommandEventStore();
            Access = new InMemoryParticipationAccessRegistry();
            AccessState = new ParticipationAccessSnapshot(
                "exam-1",
                "candidate-1",
                "participation-1",
                1,
                "device-1",
                1,
                lifecycle,
                ParticipationBusinessState.Waiting,
                1,
                1);
            Access.Set(AccessState);
            Service = new ReliableCommandService(Server, Store, Access, Clock);
        }

        public MutableTimeProvider Clock { get; }
        public ServerNodeEnvelope Server { get; }
        public InMemoryCommandEventStore Store { get; }
        public InMemoryParticipationAccessRegistry Access { get; }
        public ParticipationAccessSnapshot AccessState { get; }
        public ReliableCommandService Service { get; }

        public CreateCommandEventRequest CreateRequest(
            CommandEventType type,
            long? resourceVersion = 1,
            string? unlockReference = "protected:key-reference") =>
            new(
                "create-1",
                "event-1",
                "exam-1",
                "participation-1",
                1,
                type,
                4,
                "device-1",
                1,
                resourceVersion,
                type == CommandEventType.Start ? unlockReference : null);

        public AcknowledgeCommandRequest Ack(CommandProcessingResult result, string requestId) =>
            new(
                requestId,
                "event-1",
                "participation-1",
                1,
                "device-1",
                1,
                result,
                1,
                null);

        public ValueTask<CommandOperationResult<IReadOnlyList<CommandDelivery>>> GetDueAsync() =>
            Service.GetDueForDeviceAsync(
                "participation-1",
                1,
                "device-1",
                1,
                "server-1",
                7);
    }

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan amount) => _utcNow += amount;
    }
}
