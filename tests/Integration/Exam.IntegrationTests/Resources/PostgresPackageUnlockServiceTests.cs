using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Contracts.Resources;
using Exam.Infrastructure.Postgres.HighAvailability;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;
using Exam.IntegrationTests.Postgres;
using Exam.Server.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Exam.IntegrationTests.Resources;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresPackageUnlockServiceTests(PostgresDatabaseFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingDeploymentStateIsAllowedOnlyByTheExplicitLocalDebugProfile(bool localDebug)
    {
        using var node = CreateCertificate();
        var data = await SeedAsync(node);
        await using (var remove = fixture.DataSource.CreateCommand("DELETE FROM exam.server_state WHERE server_id = $1;"))
        {
            remove.Parameters.AddWithValue(data.Identity.ServerId);
            await remove.ExecuteNonQueryAsync();
        }
        var unwrapper = new TestUnwrapper(node);
        var service = new PostgresPackageUnlockService(fixture.DataSource, data.Identity, unwrapper, localDebug);
        if (!localDebug)
        {
            var rejected = await Assert.ThrowsAsync<PackageUnlockException>(() => service.UnlockAsync(data.Request, data.Request.DeviceId));
            Assert.Equal(ErrorCodes.NotActiveServer, rejected.Code);
            return;
        }
        using var material = await service.UnlockAsync(data.Request, data.Request.DeviceId);
        await using var plaintext = new MemoryStream();
        await PackageCiphertext.DecryptAsync(new MemoryStream(data.Ciphertext), plaintext, material);
        Assert.Equal(data.Plaintext, plaintext.ToArray());
        await ChangeParticipationAsync(data.Request.ParticipationId, "waiting", 1);
        var waiting = await Assert.ThrowsAsync<PackageUnlockException>(() => service.UnlockAsync(data.Request, data.Request.DeviceId));
        Assert.Equal(ErrorCodes.KeyNotReleased, waiting.Code);
    }

    [Fact]
    public async Task AuthorizedHttpsUnlock_ReturnsOnlyClientMaterial_AndDecryptsPersistedV2Package()
    {
        using var node = CreateCertificate();
        var data = await SeedAsync(node);
        var unwrapper = new TestUnwrapper(node);
        var service = new PostgresPackageUnlockService(fixture.DataSource, data.Identity, unwrapper);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(service);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServerIdentity:ServerId"] = data.Identity.ServerId,
            ["ServerIdentity:NodeId"] = data.Identity.NodeId,
            ["ServerIdentity:ServerEpoch"] = data.Identity.ServerEpoch.ToString()
        });
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Items["Exam.AuthenticatedDeviceId"] = data.Request.DeviceId;
            await next(context);
        });
        app.MapGroup("/api/v1").MapPackageUnlockApi();
        await app.StartAsync();
        using var httpClient = app.GetTestClient();
        using var cleartext = await httpClient.PostAsJsonAsync("/api/v1/resources/unlock", data.Request);
        Assert.Equal(HttpStatusCode.Forbidden, cleartext.StatusCode);
        Assert.Null(unwrapper.LastReleasedKey);

        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost");
        using var response = await client.PostAsJsonAsync("/api/v1/resources/unlock", data.Request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("wrappedKeys", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certificateThumbprints", body, StringComparison.OrdinalIgnoreCase);
        var result = await response.Content.ReadFromJsonAsync<ApiEnvelope<PackageUnlockMaterial>>(JsonDefaults.Create());
        Assert.NotNull(result?.Result);
        Assert.NotNull(unwrapper.LastReleasedKey);
        Assert.All(unwrapper.LastReleasedKey, value => Assert.Equal(0, value));
        using var material = result.Result;
        await using var plaintext = new MemoryStream();
        await PackageCiphertext.DecryptAsync(new MemoryStream(data.Ciphertext), plaintext, material);
        Assert.Equal(data.Plaintext, plaintext.ToArray());
        Assert.All(material.DataKey, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task UnlockRejectsWaitingRevokedStaleWrongVersionAndWrongIdentityRequests()
    {
        using var node = CreateCertificate();
        var data = await SeedAsync(node);
        var unwrapper = new TestUnwrapper(node);
        var service = new PostgresPackageUnlockService(fixture.DataSource, data.Identity, unwrapper);
        foreach (var (request, code) in new[]
                 {
                     (data.Request with { EntryVersion = 2 }, ErrorCodes.AuthorizationRevoked),
                     (data.Request with { AuthorizationVersion = 2 }, ErrorCodes.AuthorizationRevoked),
                     (data.Request with { PaperVersion = 3 }, ErrorCodes.VersionConflict),
                     (data.Request with { UnlockReference = "resource-envelope:another-package" }, ErrorCodes.VersionConflict),
                     (data.Request with { DeviceId = "other-device" }, ErrorCodes.AuthorizationRevoked),
                     (data.Request with { ParticipationId = "other-entry" }, ErrorCodes.NotFound),
                     (data.Request with { ServerId = "other-server" }, ErrorCodes.StaleServerEpoch),
                     (data.Request with { ServerEpoch = 2 }, ErrorCodes.StaleServerEpoch)
                 })
        {
            var rejected = await Assert.ThrowsAsync<PackageUnlockException>(
                () => service.UnlockAsync(request, data.Request.DeviceId));
            Assert.Equal(code, rejected.Code);
        }

        foreach (var state in new[] { "waiting", "submitted", "not_participating" })
        {
            await ChangeParticipationAsync(data.Request.ParticipationId, state, 1);
            var rejected = await Assert.ThrowsAsync<PackageUnlockException>(
                () => service.UnlockAsync(data.Request, data.Request.DeviceId));
            Assert.Equal(ErrorCodes.KeyNotReleased, rejected.Code);
        }

        await ChangeParticipationAsync(data.Request.ParticipationId, "answering", 2);
        var revoked = await Assert.ThrowsAsync<PackageUnlockException>(
            () => service.UnlockAsync(data.Request, data.Request.DeviceId));
        Assert.Equal(ErrorCodes.AuthorizationRevoked, revoked.Code);
        await ChangeParticipationAsync(data.Request.ParticipationId, "answering", 1);
        await using var end = fixture.DataSource.CreateCommand(
            "UPDATE exam.exam_entry_access SET lifecycle = 'ended' WHERE exam_id = $1;");
        end.Parameters.AddWithValue(data.ExamId);
        await end.ExecuteNonQueryAsync();
        var ended = await Assert.ThrowsAsync<PackageUnlockException>(
            () => service.UnlockAsync(data.Request, data.Request.DeviceId));
        Assert.Equal(ErrorCodes.KeyNotReleased, ended.Code);

        foreach (var sql in new[]
                 {
                     "UPDATE exam.exam_entry_access SET lifecycle = 'running' WHERE exam_id = $1;",
                     "UPDATE exam.version_state SET actual_started_at = NULL WHERE exam_id = $1;"
                 })
        {
            await using var reset = fixture.DataSource.CreateCommand(sql);
            reset.Parameters.AddWithValue(data.ExamId);
            await reset.ExecuteNonQueryAsync();
        }
        var notStarted = await Assert.ThrowsAsync<PackageUnlockException>(
            () => service.UnlockAsync(data.Request, data.Request.DeviceId));
        Assert.Equal(ErrorCodes.KeyNotReleased, notStarted.Code);

        await using var deactivate = fixture.DataSource.CreateCommand(
            "UPDATE exam.activation_devices SET activation_status = 'revoked' WHERE device_id = $1;");
        deactivate.Parameters.AddWithValue(data.Request.DeviceId);
        await deactivate.ExecuteNonQueryAsync();
        var inactive = await Assert.ThrowsAsync<PackageUnlockException>(
            () => service.UnlockAsync(data.Request, data.Request.DeviceId));
        Assert.Equal(ErrorCodes.NotActivated, inactive.Code);

        await using var obsoleteNode = fixture.DataSource.CreateCommand(
            "UPDATE exam.server_state SET server_epoch = 2 WHERE server_id = $1;");
        obsoleteNode.Parameters.AddWithValue(data.Identity.ServerId);
        await obsoleteNode.ExecuteNonQueryAsync();
        var staleProcess = await Assert.ThrowsAsync<PackageUnlockException>(
            () => service.UnlockAsync(data.Request, data.Request.DeviceId));
        Assert.Equal(ErrorCodes.StaleServerEpoch, staleProcess.Code);
        Assert.Null(unwrapper.LastReleasedKey);
    }

    [Fact]
    public async Task ScheduledDeadline_RejectsNewUnlockEvenBeforeEndWorkerRuns()
    {
        using var node = CreateCertificate();
        var data = await SeedAsync(node);
        var unwrapper = new TestUnwrapper(node);
        var service = new PostgresPackageUnlockService(fixture.DataSource, data.Identity, unwrapper);
        await using var expire = fixture.DataSource.CreateCommand(
            "UPDATE exam.exam_entry_access SET scheduled_start = CURRENT_TIMESTAMP - INTERVAL '2 hours', scheduled_end = CURRENT_TIMESTAMP - INTERVAL '1 second' WHERE exam_id = $1;");
        expire.Parameters.AddWithValue(data.ExamId);
        await expire.ExecuteNonQueryAsync();
        var rejected = await Assert.ThrowsAsync<PackageUnlockException>(
            () => service.UnlockAsync(data.Request, data.Request.DeviceId));
        Assert.Equal(ErrorCodes.KeyNotReleased, rejected.Code);
        Assert.Null(unwrapper.LastReleasedKey);
    }

    [Fact]
    public async Task DeadlineCrossedWhileUnwrapping_RejectsAndClearsNewKey()
    {
        using var node = CreateCertificate();
        var data = await SeedAsync(node);
        using var releaseUnwrapper = new ManualResetEventSlim();
        var enteredUnwrapper = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unwrapper = new TestUnwrapper(node, () =>
        {
            enteredUnwrapper.TrySetResult();
            Assert.True(releaseUnwrapper.Wait(TimeSpan.FromSeconds(10)), "测试未及时释放密钥解包等待。");
        });
        var service = new PostgresPackageUnlockService(fixture.DataSource, data.Identity, unwrapper);
        await using (var nearingDeadline = fixture.DataSource.CreateCommand(
                         "UPDATE exam.exam_entry_access SET scheduled_end = clock_timestamp() + INTERVAL '2 seconds' WHERE exam_id = $1;"))
        {
            nearingDeadline.Parameters.AddWithValue(data.ExamId);
            await nearingDeadline.ExecuteNonQueryAsync();
        }

        var unlockTask = Task.Run(() => service.UnlockAsync(data.Request, data.Request.DeviceId));
        try
        {
            await enteredUnwrapper.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            await using var deadline = fixture.DataSource.CreateCommand(
                "SELECT clock_timestamp() >= scheduled_end FROM exam.exam_entry_access WHERE exam_id = $1;");
            deadline.Parameters.AddWithValue(data.ExamId);
            while (await deadline.ExecuteScalarAsync(timeout.Token) is not true)
            {
                await timer.WaitForNextTickAsync(timeout.Token);
            }
        }
        finally
        {
            releaseUnwrapper.Set();
        }

        var rejected = await Assert.ThrowsAsync<PackageUnlockException>(() => unlockTask);
        Assert.Equal(ErrorCodes.KeyNotReleased, rejected.Code);
        Assert.NotNull(unwrapper.LastReleasedKey);
        Assert.All(unwrapper.LastReleasedKey, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task UnlockAndRevocationAreSerialized_AndRetriesNeverReplayARevokedKey()
    {
        using var node = CreateCertificate();
        var data = await SeedAsync(node);
        var unwrapper = new TestUnwrapper(node, () =>
        {
            using var competingConnection = fixture.DataSource.OpenConnection();
            using var competingTransaction = competingConnection.BeginTransaction();
            using var command = new NpgsqlCommand("""
                SET LOCAL lock_timeout = '150ms';
                UPDATE exam.participations SET authorization_version = 2 WHERE participation_id = @id;
                """, competingConnection, competingTransaction);
            command.Parameters.AddWithValue("id", data.Request.ParticipationId);
            var blocked = Assert.Throws<PostgresException>(() => command.ExecuteNonQuery());
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, blocked.SqlState);
        });
        var service = new PostgresPackageUnlockService(fixture.DataSource, data.Identity, unwrapper);
        using var released = await service.UnlockAsync(data.Request, data.Request.DeviceId);
        await ChangeParticipationAsync(data.Request.ParticipationId, "answering", 2);

        var rejected = await Assert.ThrowsAsync<PackageUnlockException>(
            () => service.UnlockAsync(data.Request, data.Request.DeviceId));
        Assert.Equal(ErrorCodes.AuthorizationRevoked, rejected.Code);
    }

    private async Task<SeedData> SeedAsync(X509Certificate2 node)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var serverId = $"unlock-server-{suffix}";
        var examId = $"unlock-exam-{suffix}";
        var deviceId = $"unlock-device-{suffix}";
        var candidateId = $"unlock-candidate-{suffix}";
        var participationId = $"unlock-participation-{suffix}";
        var packageId = $"unlock-package-{suffix}";
        var now = DateTimeOffset.UtcNow;
        var plaintext = RandomNumberGenerator.GetBytes(9000);
        await using var encrypted = new MemoryStream();
        var envelope = await new PackageEncryptionService(4096).EncryptAsync(
            new MemoryStream(plaintext), encrypted, packageId, 2, [new("node-a", node)]);
        await new PostgresVersionedResourceStore(fixture.DataSource).SaveAsync(new VersionedEncryptedResource(
            packageId, examId, "paper-package", 2, encrypted.ToArray(), envelope, now, "unlock-test"));
        await new PostgresFailoverStateStore(fixture.DataSource).SaveAsync(new ServerDeploymentState(
            serverId, "node-a", 1, ProtectionMode.EmergencySingleNode,
            new Dictionary<string, NodeState>
            {
                ["node-a"] = new("node-a", DeploymentNodeRole.Primary, true, false, 0, true, true, now)
            }, 1, []));

        await using var command = fixture.DataSource.CreateCommand("""
            INSERT INTO exam.activation_devices
                (device_id, client_number, activation_date, activation_status,
                 protected_credential, credential_version, created_at, version)
            VALUES (@device, @number, @date, 'active', 'test-verifier', 1, @now, 1);
            INSERT INTO exam.candidate_roster_entries
                (exam_id, candidate_id, candidate_name, class_text, admission_no)
            VALUES (@exam, @candidate, '合成测试考生', '1', '1001');
            INSERT INTO exam.exam_entry_access
                (exam_id, scheduled_start, scheduled_end, lifecycle, package_ready, instructions_ready,
                 encrypted_package_uri, package_length, paper_version, instructions_version, unlock_reference)
            VALUES (@exam, @start, @end, 'running', true, true, @uri, @length, 2, 1, @reference);
            INSERT INTO exam.participations
                (participation_id, exam_id, candidate_id, entry_version, business_state,
                 authorized_device_id, authorization_version, version, current_paper_version, current_grading_version)
            VALUES (@participation, @exam, @candidate, 1, 'answering', @device, 1, 1, 2, 1);
            INSERT INTO exam.version_streams (exam_id, stream_revision, created_at, updated_at)
            VALUES (@exam, 1, @now, @now);
            INSERT INTO exam.version_state
                (exam_id, stream_revision, schedule_version, paper_version, grading_version,
                 starts_at, ends_at, lifecycle, actual_started_at, snapshot, updated_at)
            VALUES (@exam, 1, 1, 2, 1, @start, @end, 'Running', @start, '{}'::jsonb, @now);
            """);
        command.Parameters.AddWithValue("device", deviceId);
        command.Parameters.AddWithValue("number", Random.Shared.NextInt64(10_000_000_000, 99_999_999_999).ToString());
        command.Parameters.AddWithValue("date", DateOnly.FromDateTime(now.UtcDateTime));
        command.Parameters.AddWithValue("now", now);
        command.Parameters.AddWithValue("exam", examId);
        command.Parameters.AddWithValue("candidate", candidateId);
        command.Parameters.AddWithValue("start", now.AddMinutes(-10));
        command.Parameters.AddWithValue("end", now.AddHours(1));
        command.Parameters.AddWithValue("uri", $"/api/v1/resources/{packageId}");
        command.Parameters.AddWithValue("length", encrypted.Length);
        command.Parameters.AddWithValue("reference", $"resource-envelope:{packageId}");
        command.Parameters.AddWithValue("participation", participationId);
        await command.ExecuteNonQueryAsync();
        return new SeedData(
            new PackageUnlockIdentity(serverId, "node-a", 1),
            new PackageUnlockRequest($"request-{suffix}", participationId, 1, deviceId, 1, 2,
                $"resource-envelope:{packageId}", serverId, 1),
            examId, encrypted.ToArray(), plaintext);
    }

    private async Task ChangeParticipationAsync(string participationId, string state, long authorizationVersion)
    {
        await using var command = fixture.DataSource.CreateCommand("""
            UPDATE exam.participations SET business_state = $2, authorization_version = $3
            WHERE participation_id = $1;
            """);
        command.Parameters.AddWithValue(participationId);
        command.Parameters.AddWithValue(state);
        command.Parameters.AddWithValue(authorizationVersion);
        await command.ExecuteNonQueryAsync();
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=unlock-test-node", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed record SeedData(PackageUnlockIdentity Identity, PackageUnlockRequest Request,
        string ExamId, byte[] Ciphertext, byte[] Plaintext);

    private sealed class TestUnwrapper(X509Certificate2 node, Action? beforeUnwrap = null) : IPackageKeyUnwrapper
    {
        public byte[]? LastReleasedKey { get; private set; }

        public byte[] Unwrap(EncryptedPackageEnvelope envelope)
        {
            beforeUnwrap?.Invoke();
            LastReleasedKey = PackageEncryptionService.UnwrapDataKey(envelope, "node-a", node);
            return LastReleasedKey;
        }
    }
}
