using System.Security.Cryptography;
using System.Text;
using Exam.Application.Activation;
using Exam.Contracts.Activation;

namespace Exam.UnitTests.Activation;

public sealed class ActivationServiceTests
{
    [Fact]
    public async Task ConcurrentApprovals_AllocateEveryNumberExactlyOnce()
    {
        var fixture = new Fixture(new DateTimeOffset(2026, 9, 22, 1, 2, 3, TimeSpan.Zero));
        var requests = await Task.WhenAll(Enumerable.Range(1, 300).Select(index =>
            fixture.Service.SubmitRequestAsync(new(
                $"apply-{index}",
                $"device-{index}",
                $"credential-{index}")).AsTask()));

        var approvals = await Task.WhenAll(requests.Select((request, index) =>
            fixture.Service.ApproveAsync(new(
                $"approve-{index}",
                request.ActivationRequestId,
                index % 2 == 0 ? "room-a" : null)).AsTask()));

        Assert.Equal(300, approvals.Select(result => result.ClientNumber).Distinct().Count());
        Assert.Equal(
            Enumerable.Range(1, 300).Select(sequence => $"20260922{sequence:0000}"),
            approvals.Select(result => result.ClientNumber).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task DuplicateApprovalRequests_ReturnOriginalResult_WithoutConsumingAnotherNumber()
    {
        var fixture = new Fixture();
        var activationRequest = await fixture.Service.SubmitRequestAsync(new("apply-1", "device-1", "secret"));
        var command = new ApproveActivationRequest("approve-1", activationRequest.ActivationRequestId, "room-a");

        var first = await fixture.Service.ApproveAsync(command);
        var replay = await fixture.Service.ApproveAsync(command);
        var semanticReplay = await fixture.Service.ApproveAsync(new("approve-semantic", activationRequest.ActivationRequestId, "room-b"));
        var next = await fixture.ApplyAndApproveAsync("device-2", "secret-2", "2");

        Assert.Equal(first, replay);
        Assert.Equal(first, semanticReplay);
        Assert.Equal("room-a", semanticReplay.RoomGroupId);
        Assert.Equal("202609220001", first.ClientNumber);
        Assert.Equal("202609220002", next.ClientNumber);
    }

    [Fact]
    public async Task ActivationStatus_ReflectsLaterApproval_AndRequiresOriginalDeviceCredential()
    {
        var fixture = new Fixture();
        var pending = await fixture.Service.SubmitRequestAsync(new("apply-status", "device-status", "secret"));
        var query = new GetActivationStatusRequest(pending.ActivationRequestId, "device-status", "secret");

        Assert.Equal(ActivationStatus.Pending, (await fixture.Service.GetRequestStatusAsync(query)).Status);

        var approved = await fixture.Service.ApproveAsync(new("approve-status", pending.ActivationRequestId));
        var current = await fixture.Service.GetRequestStatusAsync(query);
        var invalid = await Assert.ThrowsAsync<ActivationRuleException>(() =>
            fixture.Service.GetRequestStatusAsync(query with { Credential = "wrong" }).AsTask());

        Assert.Equal(ActivationStatus.Active, current.Status);
        Assert.Equal(approved.ClientNumber, current.ClientNumber);
        Assert.Equal(ActivationErrorCodes.InvalidCredential, invalid.Code);
    }

    [Fact]
    public async Task RevokeAndReactivate_RequireOriginalCredential_AndKeepNumber()
    {
        var fixture = new Fixture();
        var approved = await fixture.ApplyAndApproveAsync("device-1", "right-secret", "1");

        var revoked = await fixture.Service.RevokeAsync(new("revoke-1", approved.DeviceId, "设备维修"));
        Assert.Equal(ActivationStatus.Revoked, revoked.Status);
        Assert.False(await fixture.Service.ValidateActiveDeviceAsync(approved.DeviceId, "right-secret"));

        var error = await Assert.ThrowsAsync<ActivationRuleException>(() =>
            fixture.Service.ReactivateAsync(new("reactivate-wrong", approved.DeviceId, "wrong-secret")).AsTask());
        Assert.Equal(ActivationErrorCodes.InvalidCredential, error.Code);

        var reactivated = await fixture.Service.ReactivateAsync(
            new("reactivate-right", approved.DeviceId, "right-secret"));
        Assert.Equal(approved.ClientNumber, reactivated.ClientNumber);
        Assert.Equal(approved.ActivationDate, reactivated.ActivationDate);
        Assert.Equal(ActivationStatus.Active, reactivated.Status);
        Assert.True(await fixture.Service.ValidateActiveDeviceAsync(approved.DeviceId, "right-secret"));
    }

    [Fact]
    public async Task ReusingRequestIdWithDifferentPayload_IsRejected()
    {
        var fixture = new Fixture();
        await fixture.Service.SubmitRequestAsync(new("same", "device-1", "secret-1"));

        var error = await Assert.ThrowsAsync<ActivationRuleException>(() =>
            fixture.Service.SubmitRequestAsync(new("same", "device-2", "secret-2")).AsTask());

        Assert.Equal(ActivationErrorCodes.IdempotencyConflict, error.Code);
    }

    [Fact]
    public async Task CancelOwnActivation_RequiresCredential_ReplaysReceipt_AndRetainsDeviceIdentity()
    {
        var fixture = new Fixture();
        var applied = await fixture.Service.SubmitRequestAsync(new("own-apply", "own-device", "own-secret"));
        var approved = await fixture.Service.ApproveAsync(new("own-approve", applied.ActivationRequestId, "room-7"));
        var request = new CancelOwnActivationRequest("own-cancel", approved.DeviceId, "own-secret");

        var denied = await Assert.ThrowsAsync<ActivationRuleException>(() =>
            fixture.Service.CancelOwnAsync(request with { Credential = "someone-else" }).AsTask());
        Assert.Equal(ActivationErrorCodes.InvalidCredential, denied.Code);
        Assert.True(await fixture.Service.ValidateActiveDeviceAsync(approved.DeviceId, "own-secret"));

        var cancelled = await fixture.Service.CancelOwnAsync(request);
        var restarted = new ActivationService(fixture.Persistence, fixture.CredentialProtector, fixture.Clock);
        Assert.Equal(cancelled, await restarted.CancelOwnAsync(request));
        Assert.Equal(cancelled, await restarted.CancelOwnAsync(request with { RequestId = "own-cancel-again" }));
        Assert.Equal(ActivationStatus.Revoked, cancelled.Status);
        Assert.Equal(approved.ClientNumber, cancelled.ClientNumber);
        Assert.Equal("room-7", cancelled.RoomGroupId);
        Assert.Equal(approved.CredentialVersion, cancelled.CredentialVersion);
        Assert.Equal(ActivationStatus.Revoked, (await restarted.GetRequestStatusAsync(new(
            applied.ActivationRequestId, approved.DeviceId, "own-secret"))).Status);

        var replayConflict = await Assert.ThrowsAsync<ActivationRuleException>(() =>
            restarted.CancelOwnAsync(request with { Credential = "someone-else" }).AsTask());
        Assert.Equal(ActivationErrorCodes.IdempotencyConflict, replayConflict.Code);

        var reactivated = await restarted.ReactivateAsync(new("own-reactivate", approved.DeviceId, "own-secret"));
        Assert.Equal(approved.ClientNumber, reactivated.ClientNumber);
        Assert.Equal("room-7", reactivated.RoomGroupId);
        // An old receipt cannot cancel a new activation a second time.
        Assert.Equal(cancelled, await restarted.CancelOwnAsync(request));
        Assert.True(await restarted.ValidateActiveDeviceAsync(approved.DeviceId, "own-secret"));
    }

    [Fact]
    public async Task SequenceStopsAt9999_AndStartsAt0001OnNextServerDate()
    {
        var fixture = new Fixture(new DateTimeOffset(2026, 9, 22, 23, 59, 0, TimeSpan.Zero));
        await fixture.Persistence.ExecuteTransactionAsync(transaction =>
        {
            transaction.SetLastDailySequence(new DateOnly(2026, 9, 22), 9998);
            return true;
        });
        var last = await fixture.Service.ConnectAsync("last-device", "last-secret");
        Assert.Equal("202609229999", last.ClientNumber);
        Assert.Equal(last.ClientNumber, (await fixture.Service.ConnectAsync("last-device", "last-secret")).ClientNumber);
        var error = await Assert.ThrowsAsync<ActivationRuleException>(() =>
            fixture.Service.ConnectAsync("overflow", "secret").AsTask());
        Assert.Equal(ActivationErrorCodes.DailySequenceExhausted, error.Code);

        fixture.Clock.SetUtcNow(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero));
        var nextDay = await fixture.Service.ConnectAsync("overflow", "secret");
        Assert.Equal("202609230001", nextDay.ClientNumber);
    }

    [Fact]
    public async Task NewServiceUsingSamePersistence_ContinuesSequenceAndActivationState()
    {
        var fixture = new Fixture();
        var first = await fixture.ApplyAndApproveAsync("device-1", "secret-1", "1");
        var restarted = new ActivationService(fixture.Persistence, fixture.CredentialProtector, fixture.Clock);

        Assert.True(await restarted.ValidateActiveDeviceAsync(first.DeviceId, "secret-1"));
        var request = await restarted.SubmitRequestAsync(new("apply-2", "device-2", "secret-2"));
        var second = await restarted.ApproveAsync(new("approve-2", request.ActivationRequestId));

        Assert.Equal("202609220002", second.ClientNumber);
    }

    [Fact]
    public async Task DirectConnection_UsesOneNumberPerDevicePerServerDate_AndSurvivesRestart()
    {
        var fixture = new Fixture();
        var first = await fixture.Service.ConnectAsync("direct-device", "credential");
        Assert.Equal("202609220001", first.ClientNumber);
        Assert.Null(first.RoomGroupId);
        Assert.Empty(await fixture.Service.ListPendingRequestsAsync());
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            fixture.Service.ConnectAsync("direct-device", "credential").AsTask()));
        Assert.All(duplicates, result => Assert.Equal(first.ClientNumber, result.ClientNumber));
        var restarted = new ActivationService(fixture.Persistence, fixture.CredentialProtector, fixture.Clock);
        Assert.Equal(first.ClientNumber, (await restarted.ConnectAsync("direct-device", "credential")).ClientNumber);
        Assert.Equal("202609220002", (await restarted.ConnectAsync("second", "other-credential")).ClientNumber);
        fixture.Clock.SetUtcNow(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero));
        var nextDay = await restarted.ConnectAsync("direct-device", "credential");
        Assert.Equal("202609230001", nextDay.ClientNumber);
        Assert.Equal(first.DeviceId, nextDay.DeviceId);
        Assert.Equal(first.CredentialVersion, nextDay.CredentialVersion);
        Assert.Equal(nextDay.ClientNumber, (await restarted.ConnectAsync("direct-device", "credential")).ClientNumber);
    }

    [Fact]
    public async Task DirectConnection_ConcurrentNewDevicesUseFourDigitsAndRejectWrongCredential()
    {
        var fixture = new Fixture();
        var devices = await Task.WhenAll(Enumerable.Range(1, 32).Select(index =>
            fixture.Service.ConnectAsync($"direct-{index}", $"credential-{index}").AsTask()));
        Assert.Equal(Enumerable.Range(1, 32).Select(index => $"20260922{index:0000}"),
            devices.Select(device => device.ClientNumber).Order(StringComparer.Ordinal));
        var error = await Assert.ThrowsAsync<ActivationRuleException>(() =>
            fixture.Service.ConnectAsync("direct-1", "wrong-credential").AsTask());
        Assert.Equal(ActivationErrorCodes.InvalidCredential, error.Code);
        Assert.Equal("202609220033", (await fixture.Service.ConnectAsync("next", "credential")).ClientNumber);
    }

    [Fact]
    public async Task DirectConnection_UpgradesLegacyDeviceWithoutLosingRoomOrCredential()
    {
        var fixture = new Fixture();
        await fixture.Persistence.ExecuteTransactionAsync(transaction =>
        {
            transaction.SaveDevice(new("legacy", "20260921017", new DateOnly(2026, 9, 21),
                ActivationStatus.Revoked, "room-7", fixture.CredentialProtector.Protect("credential"),
                1, fixture.Clock.GetUtcNow().AddDays(-1), null, 4));
            return true;
        });
        var device = await fixture.Service.ConnectAsync("legacy", "credential");
        Assert.Equal("202609220001", device.ClientNumber);
        Assert.Equal("room-7", device.RoomGroupId);
        Assert.Equal(ActivationStatus.Active, device.Status);
        Assert.True(await fixture.Service.ValidateActiveDeviceAsync("legacy", "credential"));
    }

    private sealed class Fixture
    {
        public Fixture(DateTimeOffset? now = null)
        {
            Clock = new MutableTimeProvider(now ?? new DateTimeOffset(2026, 9, 22, 1, 2, 3, TimeSpan.Zero));
            Service = new ActivationService(Persistence, CredentialProtector, Clock);
        }

        public InMemoryActivationPersistence Persistence { get; } = new();
        public FastCredentialProtector CredentialProtector { get; } = new();
        public MutableTimeProvider Clock { get; }
        public ActivationService Service { get; }

        public async Task<ActivationResult> ApplyAndApproveAsync(
            string deviceId,
            string credential,
            string requestSuffix)
        {
            var request = await Service.SubmitRequestAsync(
                new($"apply-{requestSuffix}", deviceId, credential));
            return await Service.ApproveAsync(new($"approve-{requestSuffix}", request.ActivationRequestId));
        }
    }

    public sealed class FastCredentialProtector : IDeviceCredentialProtector
    {
        public string Protect(string credential) => Hash(credential);

        public bool Verify(string credential, string protectedCredential) =>
            CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(Hash(credential)),
                Encoding.UTF8.GetBytes(protectedCredential));

        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    public sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => _now;

        public void SetUtcNow(DateTimeOffset value) => _now = value;
    }
}
