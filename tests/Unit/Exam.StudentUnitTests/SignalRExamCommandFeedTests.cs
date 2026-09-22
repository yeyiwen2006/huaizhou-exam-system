using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Exam.Application.Activation;
using Exam.Application.Commands;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Domain.Commands;
using Exam.Server.Realtime;
using Exam.Student.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Exam.StudentUnitTests;

public sealed class SignalRExamCommandFeedTests
{
    [Fact]
    public async Task RealHub_ReceivesDurableStartAndDoesNotResendAfterAcknowledgement()
    {
        var access = new InMemoryParticipationAccessRegistry();
        access.Set(new("exam", "candidate", "participation", 1, "device", 1,
            ExamLifecycle.Running, ParticipationBusinessState.Answering, 1, 1));
        var serverIdentity = new ServerNodeEnvelope("server", "node", 1);
        var commands = new ReliableCommandService(serverIdentity, new InMemoryCommandEventStore(), access);
        Assert.True((await commands.CreateAsync(new("create-start", "start", "exam", "participation", 1,
            CommandEventType.Start, 1, "device", 1, 1, "resource-envelope:package"))).IsSuccess);
        await using var app = await CreateHostAsync(access, serverIdentity, commands);
        var host = app.GetTestServer();
        var received = new ConcurrentQueue<CommandDelivery>();
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var feed = CreateFeed(host, serverIdentity))
        {
            await feed.StartAsync(Participation(), async (delivery, ct) =>
            {
                received.Enqueue(delivery);
                Assert.Equal("participation", delivery.ParticipationId);
                var ack = await commands.AcknowledgeAsync(new("ack-start", delivery.EventId, delivery.ParticipationId,
                    1, "device", 1, CommandProcessingResult.Applied, 1, null), ct);
                Assert.True(ack.IsSuccess);
                applied.TrySetResult();
            });
            await applied.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(feed.IsConnected);
        }
        Assert.NotEmpty(received);

        await using var reconnected = CreateFeed(host, serverIdentity);
        await reconnected.StartAsync(Participation(), (_, _) => throw new Xunit.Sdk.XunitException("已确认事件不应重发"));
        Assert.True(reconnected.IsConnected);
        Assert.Null(reconnected.LastError);
    }

    [Fact]
    public async Task RealHub_ReconnectCatchesUpUnacknowledgedEvent()
    {
        var access = new InMemoryParticipationAccessRegistry();
        access.Set(new("exam", "candidate", "participation", 1, "device", 1,
            ExamLifecycle.Ended, ParticipationBusinessState.Answering, 1, 1));
        var identity = new ServerNodeEnvelope("server", "node", 1);
        var commands = new ReliableCommandService(identity, new InMemoryCommandEventStore(), access);
        Assert.True((await commands.CreateAsync(new("create-end", "end", "exam", "participation", 1,
            CommandEventType.End, 2, "device", 1, null, null))).IsSuccess);
        await using var app = await CreateHostAsync(access, identity, commands);
        var host = app.GetTestServer();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var delivered = new TaskCompletionSource<CommandDelivery>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var feed = CreateFeed(host, identity);
            await feed.StartAsync(Participation(), (delivery, _) =>
            {
                delivered.TrySetResult(delivery);
                return Task.CompletedTask;
            });
            Assert.Equal("end", (await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10))).EventId);
        }
    }

    [Fact]
    public async Task RealHub_ExplicitRevocationStopsTheClientRatherThanTreatingItAsNetworkLoss()
    {
        var access = new InMemoryParticipationAccessRegistry();
        var original = new ParticipationAccessSnapshot("exam", "candidate", "participation", 1, "device", 1,
            ExamLifecycle.Running, ParticipationBusinessState.Answering, 1, 1);
        access.Set(original);
        var identity = new ServerNodeEnvelope("server", "node", 1);
        var commands = new ReliableCommandService(identity, new InMemoryCommandEventStore(), access);
        await using var app = await CreateHostAsync(access, identity, commands);
        await using var feed = CreateFeed(app.GetTestServer(), identity);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        feed.AuthorizationLost = _ => { stopped.TrySetResult(); return Task.CompletedTask; };
        await feed.StartAsync(Participation(), (_, _) => Task.CompletedTask);
        access.Set(original with { AuthorizedDeviceId = "replacement-device", AuthorizationVersion = 2 });
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(feed.IsConnected);
        Assert.Contains("失效", feed.LastError);
    }

    [Theory]
    [InlineData("End")]
    [InlineData("AuthorizationRevoked")]
    [InlineData("ConnectionReplaced")]
    public async Task RealHub_TerminalSignalPreemptsSlowStart_AndStopWaitsForTrackedWork(string signal)
    {
        var access = new InMemoryParticipationAccessRegistry();
        access.Set(new("exam", "candidate", "participation", 1, "device", 1,
            ExamLifecycle.Running, ParticipationBusinessState.Answering, 1, 1));
        var identity = new ServerNodeEnvelope("server", "node", 1);
        var commands = new ReliableCommandService(identity, new InMemoryCommandEventStore(), access);
        await using var app = await CreateHostAsync(access, identity, commands);
        await using var feed = CreateFeed(app.GetTestServer(), identity, TimeSpan.FromHours(1));
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var startToken = CancellationToken.None;
        feed.AuthorizationLost = ct =>
        {
            Assert.False(ct.IsCancellationRequested);
            stopped.TrySetResult();
            return Task.CompletedTask;
        };
        try
        {
            await feed.StartAsync(Participation(), async (delivery, ct) =>
            {
                if (delivery.EventType == CommandEventType.Start)
                {
                    Interlocked.Increment(ref starts);
                    startToken = ct;
                    startEntered.TrySetResult();
                    // Deliberately ignore cancellation to prove the terminal callback itself can
                    // arrive while old work is still running, not merely after it gets cancelled.
                    await releaseStart.Task;
                }
                else if (delivery.EventType == CommandEventType.End) { stopped.TrySetResult(); }
            });
            var clients = app.Services.GetRequiredService<IHubContext<ExamEventHub>>().Clients;
            var start = Delivery(identity, "start", CommandEventType.Start, 1);
            await clients.All.SendAsync("Command", start);
            await startEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await clients.All.SendAsync("Command", start with { DeliveryAttempt = attempt + 2 });
            }
            if (signal == "End")
            {
                await clients.All.SendAsync("Command", Delivery(identity, "end", CommandEventType.End, 2));
            }
            else { await clients.All.SendAsync(signal, "current authorization lost"); }

            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(releaseStart.Task.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref starts));
            if (signal != "End")
            {
                Assert.False(feed.IsConnected);
                Assert.True(startToken.IsCancellationRequested);
            }
            var stopping = feed.StopAsync();
            Assert.False(stopping.IsCompleted);
            releaseStart.TrySetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { releaseStart.TrySetResult(); }
    }

    [Fact]
    public async Task RealHub_BoundedBacklogReservesEndCapacity_AndOverflowCanBeRetried()
    {
        var access = new InMemoryParticipationAccessRegistry();
        access.Set(new("exam", "candidate", "participation", 1, "device", 1,
            ExamLifecycle.Running, ParticipationBusinessState.Answering, 1, 1));
        var identity = new ServerNodeEnvelope("server", "node", 1);
        var commands = new ReliableCommandService(identity, new InMemoryCommandEventStore(), access);
        await using var app = await CreateHostAsync(access, identity, commands);
        await using var feed = CreateFeed(app.GetTestServer(), identity, TimeSpan.FromHours(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptedDrained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retryHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordinary = 0;
        try
        {
            await feed.StartAsync(Participation(), async (delivery, _) =>
            {
                if (delivery.EventType == CommandEventType.End) { endEntered.TrySetResult(); return; }
                var count = Interlocked.Increment(ref ordinary);
                if (count == 1) { entered.TrySetResult(); await release.Task; }
                if (count == 16) { acceptedDrained.TrySetResult(); }
                if (delivery.EventId == "command-63") { retryHandled.TrySetResult(); }
            });
            var clients = app.Services.GetRequiredService<IHubContext<ExamEventHub>>().Clients;
            await clients.All.SendAsync("Command", Delivery(identity, "command-0", CommandEventType.Start, 1));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var index = 1; index < 64; index++)
            {
                await clients.All.SendAsync("Command", Delivery(identity, $"command-{index}", CommandEventType.Start, index + 1));
            }
            await clients.All.SendAsync("Command", Delivery(identity, "end", CommandEventType.End, 65));
            await endEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref ordinary));
            release.TrySetResult();
            await acceptedDrained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(retryHandled.Task.IsCompleted);
            await clients.All.SendAsync("Command", Delivery(identity, "command-63", CommandEventType.Start, 64));
            await retryHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(17, Volatile.Read(ref ordinary));
        }
        finally { release.TrySetResult(); }
    }

    [Fact]
    public async Task RealHub_CatchUpStopsBeforeApplyingAnOlderStartInTheSameBatch()
    {
        var access = new InMemoryParticipationAccessRegistry();
        var identity = new ServerNodeEnvelope("server", "node", 1);
        var commands = new ReliableCommandService(identity, new InMemoryCommandEventStore(), access);
        var batch = new[]
        {
            Delivery(identity, "old-start", CommandEventType.Start, 1),
            Delivery(identity, "end", CommandEventType.End, 2)
        };
        await using var app = await CreateHostAsync(access, identity, commands, batch);
        await using var feed = CreateFeed(app.GetTestServer(), identity, TimeSpan.FromHours(1));
        var received = new List<CommandEventType>();
        await feed.StartAsync(Participation(), (delivery, _) =>
        {
            received.Add(delivery.EventType);
            return Task.CompletedTask;
        });
        Assert.Equal(new[] { CommandEventType.End, CommandEventType.Start }, received);
    }

    [Fact]
    public async Task RealHub_ProcessingFailureIsObserved_AndUnacknowledgedEventCanRetry()
    {
        var access = new InMemoryParticipationAccessRegistry();
        var identity = new ServerNodeEnvelope("server", "node", 1);
        var commands = new ReliableCommandService(identity, new InMemoryCommandEventStore(), access);
        var delivery = Delivery(identity, "retryable-start", CommandEventType.Start, 1);
        await using var app = await CreateHostAsync(access, identity, commands, [delivery]);
        await using var feed = CreateFeed(app.GetTestServer(), identity, TimeSpan.FromMilliseconds(100));
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        await feed.StartAsync(Participation(), (_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) { throw new IOException("synthetic local failure"); }
            retried.TrySetResult();
            return Task.CompletedTask;
        });
        Assert.Contains("尚未处理完成", feed.LastError);
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Volatile.Read(ref attempts) >= 2);
    }

    private static async Task<WebApplication> CreateHostAsync(InMemoryParticipationAccessRegistry access,
        ServerNodeEnvelope identity, ReliableCommandService commands, IReadOnlyList<CommandDelivery>? catchUpBatch = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        var services = builder.Services;
        services.AddRouting();
        services.AddSignalR().AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.AddSingleton<IDeviceCredentialValidator>(new Validator());
        services.AddSingleton<IParticipationAccessReader>(access);
        services.AddSingleton(identity);
        services.AddSingleton(commands);
        services.AddSingleton<RealtimeDeviceAuthorizer>();
        services.AddSingleton(new RealtimeDispatchOptions(300, 4));
        services.AddSingleton<RealtimeConnectionRegistry>();
        if (catchUpBatch is not null) { services.AddSingleton(new CommandBatch(catchUpBatch)); }
        var app = builder.Build();
        app.UseWebSockets();
        if (catchUpBatch is null) { app.MapHub<ExamEventHub>("/hubs/exam-events"); }
        else { app.MapHub<CatchUpBatchHub>("/hubs/exam-events"); }
        await app.StartAsync();
        return app;
    }

    private static SignalRExamCommandFeed CreateFeed(TestServer server, ServerNodeEnvelope identity, TimeSpan? catchUpInterval = null) =>
        new(new("device", "credential", "20260922001", "127.0.0.1", 5443, default,
            "server", Exam.Contracts.Activation.ActivationStatus.Active, ServerEpoch: 1), identity,
            endpoint =>
            {
                Assert.DoesNotContain("credential", endpoint.Query);
                return new HubConnectionBuilder().WithUrl(endpoint, options =>
                {
                    options.Transports = HttpTransportType.WebSockets;
                    options.SkipNegotiation = true;
                    options.WebSocketFactory = async (context, ct) =>
                    {
                        var client = server.CreateWebSocketClient();
                        client.ConfigureRequest = request => request.Headers["X-Exam-Device-Credential"] = "credential";
                        return await client.ConnectAsync(context.Uri, ct);
                    };
                }).AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter())).Build();
            }, catchUpInterval: catchUpInterval ?? TimeSpan.FromMilliseconds(100));

    private static CommandDelivery Delivery(ServerNodeEnvelope identity, string eventId, CommandEventType type, long version) =>
        new(identity, eventId, "exam", "participation", 1, type, version, "device", 1, 1,
            "resource-envelope:package", new Dictionary<string, string>(), DateTimeOffset.UtcNow, 1);

    public sealed record CommandBatch(IReadOnlyList<CommandDelivery> Events);

    public sealed class CatchUpBatchHub(CommandBatch batch) : Hub
    {
        public IReadOnlyList<CommandDelivery> CatchUp() => batch.Events;
    }

    private static ParticipationResult Participation() => new("participation", "exam", "candidate",
        ParticipationBusinessState.Answering, "device", 1, 1, 1, "resource-envelope:package");

    private sealed class Validator : IDeviceCredentialValidator
    {
        public ValueTask<bool> ValidateActiveDeviceAsync(string deviceId, string credential, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(deviceId == "device" && credential == "credential");
        public ValueTask<bool> IsActiveDeviceAsync(string deviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(deviceId == "device");
    }
}
