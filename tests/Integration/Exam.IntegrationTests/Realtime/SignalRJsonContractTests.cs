using Exam.Server.Runtime;
using System.Buffers;
using System.Text;
using System.Text.Json;
using Exam.Application.Commands;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Exam.IntegrationTests.Realtime;

public sealed class SignalRJsonContractTests
{
    [Fact]
    public async Task ProductionHttpAndHubOptionsWriteCurrentAndScheduleTimesAsUtc8()
    {
        await using var services = CreateTransportServices();
        var instant = new DateTimeOffset(2026, 10, 1, 20, 30, 0, TimeSpan.Zero);
        var payload = new { serverTime = instant, schedule = new { startsAt = instant, endsAt = instant.AddHours(2) } };
        var httpOptions = services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        using var http = JsonDocument.Parse(JsonSerializer.Serialize(payload, httpOptions));
        VerifyTimes(http.RootElement);

        var protocol = new JsonHubProtocol(services.GetRequiredService<IOptions<JsonHubProtocolOptions>>());
        var frame = Encoding.UTF8.GetString(protocol.GetMessageBytes(new InvocationMessage("TimeUpdated", [payload])).Span);
        using var hub = JsonDocument.Parse(frame.TrimEnd('\u001e'));
        VerifyTimes(hub.RootElement.GetProperty("arguments")[0]);

        void VerifyTimes(JsonElement result)
        {
            foreach (var time in new[] { result.GetProperty("serverTime"), result.GetProperty("schedule").GetProperty("startsAt"),
                         result.GetProperty("schedule").GetProperty("endsAt") })
                Assert.Equal(TimeSpan.FromHours(8), time.GetDateTimeOffset().Offset);
            Assert.Equal(instant, result.GetProperty("serverTime").GetDateTimeOffset());
            Assert.Equal(instant.AddHours(2), result.GetProperty("schedule").GetProperty("endsAt").GetDateTimeOffset());
        }
    }

    [Theory]
    [InlineData("Received", CommandProcessingResult.Received)]
    [InlineData("applied", CommandProcessingResult.Applied)]
    public async Task ProductionHubProtocolBindsStringEnumsAndPersistsAcknowledgement(string wireValue, CommandProcessingResult expected)
    {
        await using var services = CreateTransportServices();
        var protocol = new JsonHubProtocol(services.GetRequiredService<IOptions<JsonHubProtocolOptions>>());
        var frame = "{\"type\":1,\"invocationId\":\"1\",\"target\":\"Acknowledge\",\"arguments\":[" +
            "{\"requestId\":\"ack-1\",\"eventId\":\"event-1\",\"participationId\":\"participation-1\",\"entryVersion\":1," +
            "\"deviceId\":\"device-1\",\"authorizationVersion\":1,\"processingResult\":\"" + wireValue +
            "\",\"actualPaperVersion\":1,\"failureReason\":null}]}\u001e";
        var buffer = new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(frame));
        Assert.True(protocol.TryParseMessage(ref buffer, new AckBinder(), out var message));
        var invocation = Assert.IsType<InvocationMessage>(message);
        var request = Assert.IsType<AcknowledgeCommandRequest>(Assert.Single(invocation.Arguments));
        Assert.Equal(expected, request.ProcessingResult);

        var access = new InMemoryParticipationAccessRegistry();
        access.Set(new("exam-1", "candidate-1", "participation-1", 1, "device-1", 1,
            ExamLifecycle.Running, ParticipationBusinessState.Answering, 1, 1));
        var store = new InMemoryCommandEventStore();
        var service = new ReliableCommandService(new("server-1", "node-1", 1), store, access);
        var created = await service.CreateAsync(new("create-1", "event-1", "exam-1", "participation-1", 1,
            CommandEventType.ScheduleChanged, 1, "device-1", 1, null, null));
        Assert.True(created.IsSuccess);
        var ack = await service.AcknowledgeAsync(request);
        Assert.True(ack.IsSuccess);
        Assert.Equal(expected, ack.Value!.ProcessingResult);
        var stored = await store.FindAcknowledgementByRequestAsync("ack-1", "event-1");
        Assert.Equal(ack.Value, stored);
    }

    private static ServiceProvider CreateTransportServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddExamServerTransport();
        return services.BuildServiceProvider();
    }

    private sealed class AckBinder : IInvocationBinder
    {
        public IReadOnlyList<Type> GetParameterTypes(string methodName) => [typeof(AcknowledgeCommandRequest)];
        public Type GetReturnType(string invocationId) => typeof(CommandOperationResult<CommandAcknowledgement>);
        public Type GetStreamItemType(string streamId) => typeof(object);
    }
}
