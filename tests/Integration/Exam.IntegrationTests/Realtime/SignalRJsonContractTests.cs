using System.Buffers;
using System.Text;
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
    [Theory]
    [InlineData("Received", CommandProcessingResult.Received)]
    [InlineData("applied", CommandProcessingResult.Applied)]
    public async Task ProductionHubProtocolBindsStringEnumsAndPersistsAcknowledgement(string wireValue, CommandProcessingResult expected)
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:Exam"] = "" }));
        });
        // Retrieve the actual Program.cs registration, not a test-only JSON configuration.
        var protocol = new JsonHubProtocol(factory.Services.GetRequiredService<IOptions<JsonHubProtocolOptions>>());
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

    private sealed class AckBinder : IInvocationBinder
    {
        public IReadOnlyList<Type> GetParameterTypes(string methodName) => [typeof(AcknowledgeCommandRequest)];
        public Type GetReturnType(string invocationId) => typeof(CommandOperationResult<CommandAcknowledgement>);
        public Type GetStreamItemType(string streamId) => typeof(object);
    }
}
