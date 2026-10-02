using System.Security.Cryptography;
using System.Text;
using Exam.Application.Activation;
using Exam.Contracts.Activation;
using Exam.Infrastructure.Postgres.Activation;
using Exam.IntegrationTests.Postgres;
using Npgsql;

namespace Exam.IntegrationTests.Activation;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresActivationPersistenceTests(PostgresDatabaseFixture fixture)
{
    [Fact]
    public async Task ConcurrentApprovals_AreUniqueIdempotentAndRecoverAfterServiceRestart()
    {
        await ResetAsync();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 22, 1, 0, 0, TimeSpan.Zero));
        var protector = new FastCredentialProtector();
        var service = CreateService(clock, protector);
        var requests = await Task.WhenAll(Enumerable.Range(1, 300).Select(index =>
            service.SubmitRequestAsync(new(
                $"pg-apply-{index}",
                $"pg-device-{index}",
                $"pg-credential-{index}")).AsTask()));

        var approvals = await Task.WhenAll(requests.Select((request, index) =>
            service.ApproveAsync(new(
                $"pg-approve-{index + 1}",
                request.ActivationRequestId)).AsTask()));

        Assert.Equal(300, approvals.Select(result => result.ClientNumber).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            Enumerable.Range(1, 300).Select(sequence => $"20260922{sequence:000}"),
            approvals.Select(result => result.ClientNumber).Order(StringComparer.Ordinal));

        var restarted = CreateService(clock, protector);
        var replay = await restarted.ApproveAsync(new(
            "pg-approve-1",
            requests[0].ActivationRequestId));
        Assert.Equal(approvals[0], replay);
        Assert.True(await restarted.ValidateActiveDeviceAsync("pg-device-1", "pg-credential-1"));

        var nextRequest = await restarted.SubmitRequestAsync(new("pg-apply-next", "pg-device-next", "next-secret"));
        var next = await restarted.ApproveAsync(new("pg-approve-next", nextRequest.ActivationRequestId));
        Assert.Equal("20260922301", next.ClientNumber);

        await using var command = fixture.DataSource.CreateCommand("""
            SELECT protected_credential
              FROM exam.activation_devices
             WHERE device_id = 'pg-device-1';
            """);
        var storedCredential = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.NotEqual("pg-credential-1", storedCredential);
    }

    [Fact]
    public async Task Sequence999BoundaryRollsBack_AndRevokedDeviceKeepsItsNumber()
    {
        await ResetAsync();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero));
        var protector = new FastCredentialProtector();
        var service = CreateService(clock, protector);
        await using (var seed = fixture.DataSource.CreateCommand("""
                         INSERT INTO exam.activation_daily_sequences (activation_date, last_sequence)
                         VALUES (DATE '2026-09-22', 998);
                         """))
        {
            await seed.ExecuteNonQueryAsync();
        }

        var firstRequest = await service.SubmitRequestAsync(new("boundary-apply-1", "boundary-device-1", "secret-1"));
        var first = await service.ApproveAsync(new("boundary-approve-1", firstRequest.ActivationRequestId));
        Assert.Equal("20260922999", first.ClientNumber);

        var overflowRequest = await service.SubmitRequestAsync(new("boundary-apply-2", "boundary-device-2", "secret-2"));
        var overflow = await Assert.ThrowsAsync<ActivationRuleException>(() =>
            service.ApproveAsync(new("boundary-approve-2", overflowRequest.ActivationRequestId)).AsTask());
        Assert.Equal(ActivationErrorCodes.DailySequenceExhausted, overflow.Code);

        var revoked = await service.RevokeAsync(new("boundary-revoke", first.DeviceId, "测试撤销"));
        var reactivated = await service.ReactivateAsync(new("boundary-reactivate", first.DeviceId, "secret-1"));
        Assert.Equal(revoked.ClientNumber, reactivated.ClientNumber);

        clock.SetUtcNow(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero));
        var nextDay = await service.ApproveAsync(new("boundary-approve-next", overflowRequest.ActivationRequestId));
        Assert.Equal("20260923001", nextDay.ClientNumber);
    }

    private ActivationService CreateService(TimeProvider clock, IDeviceCredentialProtector protector) =>
        new(new PostgresActivationPersistence(fixture.DataSource), protector, clock);

    [Fact]
    public async Task CancelOwnActivation_PersistsReplayAndPreservesNumberRoomAndCredential()
    {
        await ResetAsync();
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 9, 22, 1, 0, 0, TimeSpan.Zero));
        var protector = new FastCredentialProtector();
        var service = CreateService(clock, protector);
        var pending = await service.SubmitRequestAsync(new("own-apply", "own-device", "own-secret"));
        var room=await new Exam.Infrastructure.Postgres.Administration.PostgresRoomService(fixture.DataSource)
            .CreateAsync(new(Guid.NewGuid().ToString("N"),"合成取消激活试场-"+Guid.NewGuid().ToString("N")),"synthetic-test");
        var active = await service.ApproveAsync(new("own-approve", pending.ActivationRequestId, room.GroupId));
        var request = new CancelOwnActivationRequest("own-cancel", active.DeviceId, "own-secret");

        var wrongCredential = await Assert.ThrowsAsync<ActivationRuleException>(() =>
            service.CancelOwnAsync(request with { Credential = "wrong" }).AsTask());
        Assert.Equal(ActivationErrorCodes.InvalidCredential, wrongCredential.Code);

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => CreateService(clock, protector).CancelOwnAsync(request).AsTask()));
        Assert.All(results, result => Assert.Equal(results[0], result));
        var restarted = CreateService(clock, protector);
        Assert.Equal(results[0], await restarted.CancelOwnAsync(request));
        Assert.Equal(ActivationStatus.Revoked, results[0].Status);
        Assert.False(await restarted.ValidateActiveDeviceAsync(active.DeviceId, "own-secret"));
        Assert.Equal(ActivationStatus.Revoked, (await restarted.GetRequestStatusAsync(new(
            pending.ActivationRequestId, active.DeviceId, "own-secret"))).Status);

        var restored = await restarted.ReactivateAsync(new("own-reactivate", active.DeviceId, "own-secret"));
        Assert.Equal(active.ClientNumber, restored.ClientNumber);
        Assert.Equal(active.RoomGroupId, restored.RoomGroupId);
        Assert.Equal(active.CredentialVersion, restored.CredentialVersion);
        Assert.Equal(results[0], await restarted.CancelOwnAsync(request));
        Assert.True(await restarted.ValidateActiveDeviceAsync(active.DeviceId, "own-secret"));

        await using var command = fixture.DataSource.CreateCommand("""
            SELECT COUNT(*) FROM exam.activation_idempotency WHERE request_id = 'own-cancel';
            """);
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    private async Task ResetAsync()
    {
        await using var command = fixture.DataSource.CreateCommand("""
            TRUNCATE TABLE
                exam.participation_idempotency,
                exam.device_decision_audits,
                exam.participation_device_conflicts,
                exam.participations,
                exam.entry_preparations,
                exam.student_sessions,
                exam.exam_entry_access,
                exam.candidate_roster_entries,
                exam.activation_idempotency,
                exam.activation_devices,
                exam.device_activation_requests,
                exam.activation_daily_sequences
            CASCADE;
            """);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FastCredentialProtector : IDeviceCredentialProtector
    {
        public string Protect(string credential) => Hash(credential);

        public bool Verify(string credential, string protectedCredential) =>
            CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(Hash(credential)),
                Encoding.UTF8.GetBytes(protectedCredential));

        private static string Hash(string value) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        protected DateTimeOffset Current = now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => Current;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : FixedTimeProvider(now)
    {
        public void SetUtcNow(DateTimeOffset value) => Current = value;
    }
}
