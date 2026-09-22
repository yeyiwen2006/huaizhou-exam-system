using Exam.Application.Activation;
using Exam.Application.Commands;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Server.Realtime;

namespace Exam.IntegrationTests.Realtime;

public sealed class RealtimeDeviceAuthorizerTests
{
    [Fact]
    public async Task RequiresCurrentServerEpoch_DeviceCredential_AndExactAuthorizationTuple()
    {
        var access = new InMemoryParticipationAccessRegistry();
        access.Set(new ParticipationAccessSnapshot(
            "exam-1",
            "candidate-1",
            "participation-1",
            2,
            "device-a",
            3,
            ExamLifecycle.Running,
            ParticipationBusinessState.Answering,
            5,
            1));
        var authorizer = new RealtimeDeviceAuthorizer(
            new TestCredentialValidator(),
            access,
            new ServerNodeEnvelope("server-1", "node-b", 9));
        var valid = new RealtimeConnectRequest(
            "participation-1",
            2,
            "device-a",
            3,
            "server-1",
            9);

        Assert.True(await authorizer.AuthorizeAsync(valid, "valid-credential"));
        Assert.False(await authorizer.AuthorizeAsync(valid, "wrong-credential"));
        Assert.False(await authorizer.AuthorizeAsync(valid with { ServerEpoch = 8 }, "valid-credential"));
        Assert.False(await authorizer.AuthorizeAsync(valid with { AuthorizationVersion = 2 }, "valid-credential"));
        Assert.False(await authorizer.AuthorizeAsync(valid with { DeviceId = "device-b" }, "valid-credential"));
    }

    private sealed class TestCredentialValidator : IDeviceCredentialValidator
    {
        public ValueTask<bool> ValidateActiveDeviceAsync(
            string deviceId,
            string credential,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                deviceId == "device-a" && credential == "valid-credential");

        public ValueTask<bool> IsActiveDeviceAsync(
            string deviceId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(deviceId == "device-a");
    }
}
