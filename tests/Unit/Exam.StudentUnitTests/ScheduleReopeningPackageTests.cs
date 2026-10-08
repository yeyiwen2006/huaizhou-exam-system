using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

// Synthetic encrypted package fixture shared with the existing command tests.
public sealed partial class ExamPackageManagerTests
{
    private static CommandDelivery Reopening(CommandDelivery start, long version = 3) => start with
    {
        EventId = "synthetic-reopen-" + version,
        StateVersion = version,
        Metadata = new Dictionary<string, string> { ["reopenedAtScheduleVersion"] = version.ToString() },
        NewScheduledStart = DateTimeOffset.UtcNow.AddHours(-1),
        NewScheduledEnd = DateTimeOffset.UtcNow.AddHours(1)
    };

    [Fact]
    public async Task Explicit_reopening_unlocks_and_survives_restart_without_accepting_old_end()
    {
        using var fixture = await Fixture.CreateAsync();
        var end = fixture.Api.Start() with { EventId = "synthetic-end", EventType = CommandEventType.End, StateVersion = 2 };
        var resume = Reopening(fixture.Api.Start());
        using (var manager = fixture.Manager())
        {
            Assert.True(await manager.PrepareAndDownloadAsync());
            Assert.True((await manager.ConfirmParticipationAsync())?.IsSuccess);
            Assert.True(await manager.HandleCommandAsync(fixture.Api.Start()));
            Assert.True(await manager.HandleCommandAsync(end));
            Assert.True(manager.ExamEnded);
            Assert.True(await manager.HandleCommandAsync(resume));
            Assert.False(manager.ExamEnded);
            Assert.Equal(ExamPackageManager.Phase.Unlocked, manager.CurrentPhase);
            Assert.Equal(resume.NewScheduledEnd, manager.OverriddenScheduledEnd);
            Assert.Equal(fixture.Api.Plaintext, manager.UnlockedPayload);
            Assert.True(await manager.HandleCommandAsync(end with { EventId = "synthetic-delayed-end" }));
            Assert.False(manager.ExamEnded);
            Assert.Equal(CommandProcessingResult.RejectedAsStale, fixture.Api.Acks[^1].ProcessingResult);
        }
        fixture.Api.BusinessState = ParticipationBusinessState.Answering;
        fixture.Api.Lifecycle = ExamLifecycle.Running;
        using var restored = fixture.Manager();
        Assert.False(restored.ExamEnded);
        Assert.Equal(resume.NewScheduledEnd, restored.OverriddenScheduledEnd);
        Assert.True(await restored.PrepareAndDownloadAsync());
        Assert.True((await restored.ConfirmParticipationAsync())?.IsSuccess);
        Assert.True(await restored.HandleCommandAsync(end with { EventId = "synthetic-delayed-after-restart" }));
        Assert.False(restored.ExamEnded);
        Assert.True(await restored.HandleCommandAsync(end with { EventId = "synthetic-new-end", StateVersion = 3 }));
        Assert.True(restored.ExamEnded);
        Assert.True(await restored.HandleCommandAsync(resume with { EventId = "synthetic-late-reopen" }));
        Assert.True(restored.ExamEnded);
    }

    [Fact]
    public async Task Extension_rejects_old_end_even_when_schedule_ack_is_lost()
    {
        using var fixture = await Fixture.CreateAsync();
        using var manager = fixture.Manager();
        Assert.True(await manager.PrepareAndDownloadAsync());
        await manager.ConfirmParticipationAsync();
        Assert.True(await manager.HandleCommandAsync(fixture.Api.Start()));
        var schedule = Reopening(fixture.Api.Start()) with { EventType = CommandEventType.ScheduleChanged, ProtectedUnlockReference = null };
        fixture.Api.DropAck = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => manager.HandleCommandAsync(schedule));
        Assert.True(await manager.HandleCommandAsync(fixture.Api.Start() with
        { EventId = "synthetic-old-end", EventType = CommandEventType.End, StateVersion = 2 }));
        Assert.False(manager.ExamEnded);
        Assert.Equal(fixture.Api.Plaintext, manager.UnlockedPayload);
        Assert.True(await manager.HandleCommandAsync(schedule));
    }

    [Fact]
    public async Task Reconnect_uses_current_server_reopening_version_and_window()
    {
        using var fixture = await Fixture.CreateAsync();
        using (var manager = fixture.Manager())
        {
            Assert.True(await manager.PrepareAndDownloadAsync());
            await manager.ConfirmParticipationAsync();
            await manager.HandleCommandAsync(fixture.Api.Start() with { EventId = "synthetic-end", EventType = CommandEventType.End, StateVersion = 2 });
        }
        fixture.Api.BusinessState = ParticipationBusinessState.Answering;
        fixture.Api.Lifecycle = ExamLifecycle.Running;
        fixture.Api.ScheduleVersion = fixture.Api.ReopenedAtScheduleVersion = 3;
        fixture.Api.ScheduledStart = DateTimeOffset.UtcNow.AddHours(-1);
        fixture.Api.ScheduledEnd = DateTimeOffset.UtcNow.AddHours(1);
        using var restored = fixture.Manager();
        Assert.True(await restored.PrepareAndDownloadAsync());
        Assert.True((await restored.ConfirmParticipationAsync())?.IsSuccess);
        Assert.False(restored.ExamEnded);
        Assert.Equal(ExamPackageManager.Phase.Unlocked, restored.CurrentPhase);
        Assert.Equal(fixture.Api.ScheduledEnd, restored.OverriddenScheduledEnd);
    }

    [Fact]
    public async Task A_new_end_preempts_a_slow_reopening_unlock()
    {
        using var fixture = await Fixture.CreateAsync();
        using var manager = fixture.Manager();
        Assert.True(await manager.PrepareAndDownloadAsync());
        await manager.ConfirmParticipationAsync();
        await manager.HandleCommandAsync(fixture.Api.Start() with { EventId = "synthetic-end", EventType = CommandEventType.End, StateVersion = 2 });
        fixture.Api.UnlockEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Api.ContinueUnlock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = manager.HandleCommandAsync(Reopening(fixture.Api.Start()));
        await fixture.Api.UnlockEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var ended = manager.HandleCommandAsync(fixture.Api.Start() with
        { EventId = "synthetic-current-end", EventType = CommandEventType.End, StateVersion = 3 });
        try { Assert.True(manager.ExamEnded); Assert.Null(manager.UnlockedPayload); }
        finally { fixture.Api.ContinueUnlock.TrySetResult(); }
        await Task.WhenAll(resumed, ended);
        Assert.True(manager.ExamEnded);
        Assert.Null(manager.UnlockedPayload);
    }

    [Fact]
    public async Task Current_ended_confirmation_prevents_same_version_reopening_even_without_a_received_end_event()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Api.BusinessState = ParticipationBusinessState.Answering;
        fixture.Api.Lifecycle = ExamLifecycle.Ended;
        fixture.Api.ScheduleVersion = fixture.Api.ReopenedAtScheduleVersion = 3;
        using var manager = fixture.Manager();
        Assert.True(await manager.PrepareAndDownloadAsync());
        await manager.ConfirmParticipationAsync();
        Assert.True(manager.ExamEnded);
        Assert.True(await manager.HandleCommandAsync(Reopening(fixture.Api.Start())));
        Assert.True(manager.ExamEnded);
        Assert.Equal(0, fixture.Api.UnlockCalls);
    }
}
