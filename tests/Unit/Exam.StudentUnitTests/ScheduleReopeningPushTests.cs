using System.Collections.Concurrent;
using Exam.Application.Commands;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Server.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Exam.StudentUnitTests;

public sealed partial class SignalRExamCommandFeedTests
{
    [Fact]
    public async Task RealHub_pushes_reopening_window_and_reconnect_retries_it_without_old_end()
    {
        var identity = new ServerNodeEnvelope("server", "node", 1);
        var access = new InMemoryParticipationAccessRegistry();
        var state = new ParticipationAccessSnapshot("exam", "candidate", "participation", 1, "device", 1,
            ExamLifecycle.Running, ParticipationBusinessState.Answering, 1, 1);
        access.Set(state);
        var clock = new ReopeningTestClock();
        var commands = new ReliableCommandService(identity, new InMemoryCommandEventStore(), access, clock);
        await using var app = await CreateHostAsync(access, identity, commands);
        var startTime = DateTimeOffset.UtcNow.AddHours(-1);
        var endTime = startTime.AddHours(3);
        var metadata = new Dictionary<string, string>
        {
            ["scheduledStart"] = startTime.ToString("O"), ["scheduledEnd"] = endTime.ToString("O"),
            ["reopenedAtScheduleVersion"] = "3"
        };
        var received = new ConcurrentDictionary<string, CommandDelivery>();
        var pushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var feed = CreateFeed(app.GetTestServer(), identity, TimeSpan.FromHours(1)))
        {
            await feed.StartAsync(Participation(), (delivery, _) =>
            {
                received[delivery.EventId] = delivery;
                if (received.Count == 2) pushed.TrySetResult();
                return Task.CompletedTask; // Simulate lost acknowledgements, requiring durable catch-up.
            });
            access.Set(state with { ExamLifecycle = ExamLifecycle.Ended });
            Assert.True((await commands.CreateAsync(new("synthetic-end-request", "synthetic-old-end", "exam", "participation", 1,
                CommandEventType.End, 2, "device", 1, null, null))).IsSuccess);
            access.Set(state with { ReopenedAtScheduleVersion = 3 });
            foreach (var type in new[] { CommandEventType.ScheduleChanged, CommandEventType.Start })
            {
                var id = "synthetic-reopen-" + type;
                Assert.True((await commands.CreateAsync(new(id + "-request", id, "exam", "participation", 1,
                    type, 3, "device", 1, 1, type == CommandEventType.Start ? "resource-envelope:package" : null, metadata))).IsSuccess);
                var delivery = await commands.ResendAsync(id, "participation", 1, "device", 1);
                Assert.True(delivery.IsSuccess);
                await app.Services.GetRequiredService<IHubContext<ExamEventHub>>().Clients.All.SendAsync("Command", delivery.Value);
            }
            await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(received.Values, delivery =>
            {
                Assert.Equal(endTime, delivery.NewScheduledEnd);
                Assert.Equal("3", delivery.Metadata["reopenedAtScheduleVersion"]);
                Assert.NotEqual(CommandEventType.End, delivery.EventType);
            });
        }
        clock.Now = clock.Now.AddMinutes(1); // Advance beyond the durable retry delay without sleeping.
        var caughtUp = new ConcurrentDictionary<string, CommandDelivery>();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var reconnected = CreateFeed(app.GetTestServer(), identity, TimeSpan.FromHours(1));
        await reconnected.StartAsync(Participation(), (delivery, _) =>
        {
            caughtUp[delivery.EventId] = delivery;
            if (caughtUp.Count == 2) recovered.TrySetResult();
            return Task.CompletedTask;
        });
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, caughtUp.Count);
        Assert.All(caughtUp.Values, delivery => Assert.Equal(3, delivery.StateVersion));
        Assert.DoesNotContain(caughtUp.Values, delivery => delivery.EventType == CommandEventType.End);
    }

    [Fact]
    public async Task RealHub_catchup_applies_new_schedule_before_older_end_and_same_version_end_before_start()
    {
        var identity = new ServerNodeEnvelope("server", "node", 1);
        var access = new InMemoryParticipationAccessRegistry();
        var commands = new ReliableCommandService(identity, new InMemoryCommandEventStore(), access);
        var batch = new[]
        {
            Delivery(identity, "old-end", CommandEventType.End, 2),
            Delivery(identity, "new-schedule", CommandEventType.ScheduleChanged, 4),
            Delivery(identity, "new-start", CommandEventType.Start, 3),
            Delivery(identity, "new-end", CommandEventType.End, 3)
        };
        await using var app = await CreateHostAsync(access, identity, commands, batch);
        await using var feed = CreateFeed(app.GetTestServer(), identity, TimeSpan.FromHours(1));
        var received = new List<string>();
        await feed.StartAsync(Participation(), (delivery, _) =>
        {
            received.Add(delivery.EventId);
            return Task.CompletedTask;
        });
        Assert.Equal(new[] { "new-schedule", "new-end", "new-start", "old-end" }, received);
    }

    private sealed class ReopeningTestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
