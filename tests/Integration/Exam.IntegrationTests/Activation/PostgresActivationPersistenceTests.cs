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
            Enumerable.Range(1, 300).Select(sequence => $"20260922{sequence:0000}"),
            approvals.Select(result => result.ClientNumber).Order(StringComparer.Ordinal));

        var restarted = CreateService(clock, protector);
        var replay = await restarted.ApproveAsync(new(
            "pg-approve-1",
            requests[0].ActivationRequestId));
        Assert.Equal(approvals[0], replay);
        Assert.True(await restarted.ValidateActiveDeviceAsync("pg-device-1", "pg-credential-1"));

        var nextRequest = await restarted.SubmitRequestAsync(new("pg-apply-next", "pg-device-next", "next-secret"));
        var next = await restarted.ApproveAsync(new("pg-approve-next", nextRequest.ActivationRequestId));
        Assert.Equal("202609220301", next.ClientNumber);

        await using var command = fixture.DataSource.CreateCommand("""
            SELECT protected_credential
              FROM exam.activation_devices
             WHERE device_id = 'pg-device-1';
            """);
        var storedCredential = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.NotEqual("pg-credential-1", storedCredential);
    }

    [Fact]
    public async Task Sequence9999BoundaryRollsBack_AndRevokedDeviceKeepsItsNumber()
    {
        await ResetAsync();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero));
        var protector = new FastCredentialProtector();
        var service = CreateService(clock, protector);
        await using (var seed = fixture.DataSource.CreateCommand("""
                         INSERT INTO exam.activation_daily_sequences (activation_date, last_sequence)
                         VALUES (DATE '2026-09-22', 9998);
                         """))
        {
            await seed.ExecuteNonQueryAsync();
        }

        var firstRequest = await service.SubmitRequestAsync(new("boundary-apply-1", "boundary-device-1", "secret-1"));
        var first = await service.ApproveAsync(new("boundary-approve-1", firstRequest.ActivationRequestId));
        Assert.Equal("202609229999", first.ClientNumber);

        var overflowRequest = await service.SubmitRequestAsync(new("boundary-apply-2", "boundary-device-2", "secret-2"));
        var overflow = await Assert.ThrowsAsync<ActivationRuleException>(() =>
            service.ApproveAsync(new("boundary-approve-2", overflowRequest.ActivationRequestId)).AsTask());
        Assert.Equal(ActivationErrorCodes.DailySequenceExhausted, overflow.Code);

        var revoked = await service.RevokeAsync(new("boundary-revoke", first.DeviceId, "测试撤销"));
        var reactivated = await service.ReactivateAsync(new("boundary-reactivate", first.DeviceId, "secret-1"));
        Assert.Equal(revoked.ClientNumber, reactivated.ClientNumber);

        clock.SetUtcNow(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero));
        var nextDay = await service.ApproveAsync(new("boundary-approve-next", overflowRequest.ActivationRequestId));
        Assert.Equal("202609230001", nextDay.ClientNumber);
    }

    [Fact]
    public async Task DirectConnections_PersistOneDailyNumberAndRolloverAcrossServiceRestart()
    {
        await ResetAsync();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 10, 8, 8, 0, 0, TimeSpan.Zero));
        var protector = new FastCredentialProtector();
        var service = CreateService(clock, protector);
        var sameDevice = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            CreateService(clock, protector).ConnectAsync("direct-device", "direct-credential").AsTask()));
        Assert.All(sameDevice, device => Assert.Equal("202610080001", device.ClientNumber));
        var others = await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            service.ConnectAsync($"direct-{index}", $"credential-{index}").AsTask()));
        Assert.Equal(16, others.Select(device => device.ClientNumber).Distinct().Count());
        Assert.Equal("202610080018", (await service.ConnectAsync("last", "credential")).ClientNumber);
        clock.SetUtcNow(new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero));
        var next = await CreateService(clock, protector).ConnectAsync("direct-device", "direct-credential");
        Assert.Equal("202610090001", next.ClientNumber);
        Assert.Equal(next, await service.FindDeviceAsync("direct-device"));
        Assert.Empty(await service.ListPendingRequestsAsync());
        var directory = await new Exam.Infrastructure.Postgres.Administration.PostgresDeviceDirectory(fixture.DataSource).ListAsync(query: next.ClientNumber);
        Assert.Equal(next.DeviceId, Assert.Single(directory.Items).DeviceId);

        var examTime = DateTimeOffset.UtcNow.AddYears(80);
        Assert.True(new Exam.Infrastructure.Postgres.Versioning.PostgresExamVersioningService(fixture.DataSource)
            .CreateExam(new("direct-exam", new(examTime.AddHours(1), examTime.AddHours(2)), examTime, "direct-exam-request")).Succeeded);
        await new Exam.Infrastructure.Postgres.Administration.PostgresCandidateRosterService(fixture.DataSource)
            .ImportAsync("direct-exam", new("direct-roster", 0, [new(1, "合成考生", "01", "0001")]), "synthetic-operator");
        var participation = new Exam.Application.Participation.ParticipationService(
            new Exam.Infrastructure.Postgres.Participation.PostgresParticipationPersistence(fixture.DataSource), service,
            new Exam.Infrastructure.Postgres.Participation.PostgresCandidateRosterDirectory(fixture.DataSource),
            new Exam.Infrastructure.Postgres.Participation.PostgresExamEntryDirectory(fixture.DataSource), clock);
        var login = await participation.ConnectAndLoginAsync(
            new("direct-login", "direct-device", "direct-credential", "合成考生", "01", "0001"), service);
        var presence = new Exam.Infrastructure.Postgres.Administration.PostgresDevicePresenceService(fixture.DataSource, clock);
        var lease = await presence.OpenAsync(login.Device.DeviceId, new("open-direct", "connection-direct"));
        await presence.UpdateAsync(login.Device.DeviceId,
            new(lease.ConnectionId, lease.Generation, 1, login.Session.SessionId, null, "选择考试", null, 0, "未下载", false, false));
        var visible = Assert.Single((await new Exam.Infrastructure.Postgres.Administration.PostgresDeviceDirectory(fixture.DataSource, clock)
            .ListAsync(query: login.Device.ClientNumber)).Items);
        Assert.Equal("合成考生", visible.CandidateName);
        Assert.Equal("01", visible.ClassText);
        Assert.Equal("0001", visible.AdmissionNo);
        Assert.Equal(Exam.Contracts.Administration.DeviceConnectionState.Online, visible.ConnectionState);
    }

    [Fact]
    public async Task LegacyDeviceUpgradesOnceWithoutChangingIdentityCredentialOrRoom()
    {
        await ResetAsync();
        var now = new DateTimeOffset(2026, 10, 8, 8, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var protector = new FastCredentialProtector();
        var room = await new Exam.Infrastructure.Postgres.Administration.PostgresRoomService(fixture.DataSource)
            .CreateAsync(new(Guid.NewGuid().ToString("N"), "合成旧编号试场"), "synthetic-operator");
        await new PostgresActivationPersistence(fixture.DataSource).ExecuteTransactionAsync(transaction =>
        {
            transaction.SaveDevice(new("legacy-device", "20261008007", new DateOnly(2026, 10, 8),
                ActivationStatus.Active, room.GroupId, protector.Protect("legacy-credential"), 1, now, now, 1));
            transaction.SetLastDailySequence(new DateOnly(2026, 10, 8), 7);
            return true;
        });

        var service = CreateService(clock, protector);
        var upgraded = await service.ConnectAsync("legacy-device", "legacy-credential");
        Assert.Equal("202610080008", upgraded.ClientNumber);
        Assert.Equal("legacy-device", upgraded.DeviceId);
        Assert.Equal(room.GroupId, upgraded.RoomGroupId);
        Assert.Equal(1, upgraded.CredentialVersion);
        Assert.True(await service.ValidateActiveDeviceAsync("legacy-device", "legacy-credential"));
        Assert.Equal(upgraded.ClientNumber,
            (await CreateService(clock, protector).ConnectAsync("legacy-device", "legacy-credential")).ClientNumber);
        Assert.Equal("202610080009", (await service.ConnectAsync("next-device", "next-credential")).ClientNumber);
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
