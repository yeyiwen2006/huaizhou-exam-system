using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using Exam.Application.Activation;
using Exam.Contracts.Common;
using Exam.Contracts.Commands;
using Exam.Contracts.ServiceManagement;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Domain.Submissions;
using Exam.Infrastructure.Postgres.Submissions;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.Infrastructure.Security;
using Exam.Infrastructure.ServiceManagement;
using Exam.IntegrationTests.Postgres;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Exam.IntegrationTests.ServiceManagement;

// Each test owns a fresh migrated database. Deliberate corruption never reaches other modules' fixtures.
[Trait("Category", "Postgres")]
public sealed class PostgresRecoveryReadinessProbeTests : IAsyncLifetime
{
    private readonly PostgresDatabaseFixture _database = new();
    private readonly RSA _key = RSA.Create(2048);
    private X509Certificate2 _certificate = null!;
    private static readonly ServiceConfiguration Configuration = new("server", "node", "127.0.0.1", 8443, "synthetic", false, 1);
    public async Task InitializeAsync()
    {
        _certificate = new CertificateRequest("CN=Synthetic-F01-Recovery", _key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        await _database.InitializeAsync();
    }
    public async Task DisposeAsync() { await _database.DisposeAsync(); _certificate.Dispose(); _key.Dispose(); }
    private Task<IReadOnlyList<StartupCheckResult>> InspectAsync(IPackageKeyUnwrapper? unwrap = null) =>
        new PostgresRecoveryReadinessProbe(_database.DataSource, unwrap ?? new TestUnwrapper(_certificate)).RunAsync(Configuration, default);
    private async Task ExecuteAsync(string sql)
    {
        await using var command = _database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }
    private void CreateExam()
    {
        var now = DateTimeOffset.UtcNow;
        var created = new PostgresExamVersioningService(_database.DataSource)
            .CreateExam(new("synthetic-exam", new(now.AddHours(1), now.AddHours(2)), now, "create-exam"));
        Assert.True(created.Succeeded, created.Error);
    }

    [Fact]
    public async Task RealVersioningProjectionPassesReadOnlyChecks_AndOpensRecoveryGuard()
    {
        CreateExam();
        var checks = await InspectAsync();
        Assert.Equal(5, checks.Count);
        Assert.All(checks, c => Assert.True(c.Passed, c.Name));
        var guard = new BusinessRecoveryGuard();
        Assert.False(guard.Snapshot.Ready);
        guard.Observe(checks, DateTimeOffset.UtcNow);
        Assert.True(guard.Snapshot.Ready);
        await using var command = _database.DataSource.CreateCommand("SELECT stream_revision FROM exam.version_streams WHERE exam_id = 'synthetic-exam'");
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData("DELETE FROM exam.version_events", "version-projections")]
    [InlineData("UPDATE exam.version_state SET snapshot = '{}'::jsonb", "version-projections")]
    [InlineData("UPDATE exam.exam_entry_access SET scheduled_start = scheduled_start + interval '1 minute'", "version-projections")]
    [InlineData("UPDATE exam.version_events SET payload = jsonb_set(payload, '{examId}', '\"wrong\"'::jsonb)", "version-projections")]
    [InlineData("UPDATE exam.version_events SET payload = jsonb_set(payload, '{schedule,startsAt}', to_jsonb((payload->'schedule'->>'startsAt')::timestamptz + interval '1 minute'))", "version-projections")]
    [InlineData("UPDATE exam.version_events SET payload = jsonb_set(payload, '{schedule}', 'null'::jsonb)", "version-projections")]
    public async Task DamagedVersionEvidenceClosesGuard(string damage, string expected)
    {
        CreateExam();
        await ExecuteAsync(damage);
        var checks = await InspectAsync();
        Assert.Contains(checks, c => c.Name == expected && !c.Passed && c.BlocksWrites);
        var guard = new BusinessRecoveryGuard();
        guard.Observe(checks, DateTimeOffset.UtcNow);
        Assert.False(guard.Snapshot.Ready);
    }

    [Fact]
    public async Task ExamEventReplayAllowsCanonicalParticipantsSynchronizedOutsideVersionEvents()
    {
        CreateExam();
        await ExecuteAsync("""
            INSERT INTO exam.activation_devices VALUES
              ('device', '20260927001', '2026-09-27', 'active', NULL, 'synthetic-verifier', 1, now(), NULL, 1);
            INSERT INTO exam.activation_daily_sequences VALUES ('2026-09-27', 1);
            INSERT INTO exam.candidate_roster_entries (exam_id, candidate_id, candidate_name, class_text, admission_no)
              VALUES ('synthetic-exam', 'candidate', '合成考生', '01', '000001');
            """);
        await new PostgresSubmissionStore(_database.DataSource).SetAuthorizationStateAsync(new(
            "synthetic-exam", "candidate", "participation", 1, "device", 1, ParticipationBusinessState.Waiting, 1, 1));
        var service = new PostgresExamVersioningService(_database.DataSource);
        var current = service.GetExam("synthetic-exam").Value!;
        var changed = service.UpdateSchedule(new("synthetic-exam", current.Versions.ScheduleVersion,
            new(current.Schedule.StartsAt.AddMinutes(10), current.Schedule.EndsAt.AddMinutes(10)),
            DateTimeOffset.UtcNow, "schedule-with-participant"));
        Assert.True(changed.Succeeded, changed.Error);
        Assert.Contains("participation", changed.Value!.Participants.Keys);
        Assert.All(await InspectAsync(), check => Assert.True(check.Passed, check.Name));
    }

    [Fact]
    public async Task DeviceSequenceMustCoverAlreadyAllocatedNumbers()
    {
        await ExecuteAsync("""
            INSERT INTO exam.activation_devices VALUES
              ('synthetic-device', '20260927001', '2026-09-27', 'active', NULL, 'synthetic-verifier', 1, now(), NULL, 1);
            """);
        Assert.Contains(await InspectAsync(), c => c.Name == "activation-sequences" && !c.Passed);
        await ExecuteAsync("INSERT INTO exam.activation_daily_sequences VALUES ('2026-09-27', 1)");
        Assert.All(await InspectAsync(), c => Assert.True(c.Passed, c.Name));
    }

    [Fact]
    public async Task RealEncryptedResourceAuthenticatesEveryChunk_AndTamperingBlocksStartup()
    {
        await StoreResourceAsync();
        Assert.All(await InspectAsync(), c => Assert.True(c.Passed, c.Name));
        await ExecuteAsync("UPDATE exam.versioned_resources SET encrypted_content = set_byte(encrypted_content, octet_length(encrypted_content) - 1, get_byte(encrypted_content, octet_length(encrypted_content) - 1) # 1)");
        Assert.Contains(await InspectAsync(), c => c.Name == "published-resources" && !c.Passed);
    }

    [Fact]
    public async Task MissingNodeKeyBlocksStartup_WithoutExposingKeyOrDatabaseErrors()
    {
        await StoreResourceAsync();
        var checks = await InspectAsync(new MissingKey());
        Assert.Contains(checks, c => c.Name == "published-resources" && !c.Passed);
        Assert.DoesNotContain(checks, c => c.Message.Contains("synthetic-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OrphanReliableEventBlocksRecovery()
    {
        await ExecuteAsync("""
            INSERT INTO exam.command_events (event_id, create_request_id, exam_id, participation_id, entry_version,
              event_type, state_version, target_device_id, target_authorization_version, created_at, next_delivery_at)
              VALUES ('event', 'request', 'synthetic-exam', 'missing', 1, 'End', 1, 'device', 1, now(), now());
            """);
        Assert.Contains(await InspectAsync(), c => c.Name == "reliable-events" && !c.Passed);
    }

    [Fact]
    public async Task RowPolicyCannotHideCorruptReliableEventFromRecoveryGate()
    {
        await ExecuteAsync("""
            INSERT INTO exam.command_events (event_id, create_request_id, exam_id, participation_id, entry_version,
              event_type, state_version, target_device_id, target_authorization_version, created_at, next_delivery_at)
              VALUES ('hidden-event', 'hidden-request', 'synthetic-exam', 'missing', 1, 'End', 1, 'device', 1, now(), now());
            """);
        var role = "f01_probe_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {role} NOLOGIN NOSUPERUSER NOBYPASSRLS");
        try
        {
            await ExecuteAsync($"GRANT USAGE ON SCHEMA exam TO {role}; GRANT SELECT ON ALL TABLES IN SCHEMA exam TO {role}");
            await ExecuteAsync("ALTER TABLE exam.command_events ENABLE ROW LEVEL SECURITY; " +
                "CREATE POLICY synthetic_visible_events ON exam.command_events FOR SELECT USING (event_id <> 'hidden-event')");
            var readerConnection = new NpgsqlConnectionStringBuilder(_database.ConnectionString)
                { Options = "-c role=" + role }.ConnectionString;
            await using var reader = NpgsqlDataSource.Create(readerConnection);
            await using (var visible = reader.CreateCommand("SELECT count(*) FROM exam.command_events"))
                Assert.Equal(0L, await visible.ExecuteScalarAsync());
            var checks = await new PostgresRecoveryReadinessProbe(reader, new TestUnwrapper(_certificate))
                .RunAsync(Configuration, default);
            Assert.Contains(checks, c => c.BlocksWrites && !c.Passed);
            var guard = new BusinessRecoveryGuard();
            guard.Observe(checks, DateTimeOffset.UtcNow);
            Assert.False(guard.Snapshot.Ready);
        }
        finally
        {
            await ExecuteAsync("DROP POLICY IF EXISTS synthetic_visible_events ON exam.command_events; ALTER TABLE exam.command_events DISABLE ROW LEVEL SECURITY");
            await ExecuteAsync($"REVOKE SELECT ON ALL TABLES IN SCHEMA exam FROM {role}; REVOKE USAGE ON SCHEMA exam FROM {role}; DROP ROLE {role}");
        }
    }

    [Fact]
    public async Task PublishedPaperUsesExistingVersioningAndResourceContracts()
    {
        CreateExam();
        await StoreResourceAsync();
        var service = new PostgresExamVersioningService(_database.DataSource);
        var published = service.PublishPaper(new("synthetic-exam", 0, "synthetic-resource",
            new PaperStructure([new("q1", 1, "subject", "group", "single", ["A", "B"])]), [],
            new(true, true, true, true), "operator", "合成恢复检查", DateTimeOffset.UtcNow, "publish"));
        Assert.True(published.Succeeded, published.Error);
        Assert.All(await InspectAsync(), c => Assert.True(c.Passed, c.Name));
        await ExecuteAsync("DELETE FROM exam.versioned_resources");
        Assert.Contains(await InspectAsync(), c => c.Name == "published-resources" && !c.Passed);
    }

    [Theory]
    [InlineData("DELETE FROM exam.submission_idempotency")]
    [InlineData("UPDATE exam.submission_idempotency SET result_payload = jsonb_set(result_payload, '{submission,receipt,receiptId}', '\"forged-receipt\"'::jsonb)")]
    [InlineData("DELETE FROM exam.grading_outbox")]
    public async Task CanonicalFinalSubmissionRequiresOriginalReplayReceiptAndOutbox(string damage)
    {
        CreateExam();
        await ExecuteAsync("""
            INSERT INTO exam.activation_devices VALUES
              ('device', '20260927001', '2026-09-27', 'active', NULL, 'synthetic-verifier', 1, now(), NULL, 1);
            INSERT INTO exam.activation_daily_sequences VALUES ('2026-09-27', 1);
            INSERT INTO exam.candidate_roster_entries (exam_id, candidate_id, candidate_name, class_text, admission_no)
              VALUES ('synthetic-exam', 'candidate', '合成考生', '01', '000001');
            """);
        var store = new PostgresSubmissionStore(_database.DataSource);
        await store.SetAuthorizationStateAsync(new("synthetic-exam", "candidate", "participation", 1, "device", 1,
            ParticipationBusinessState.Answering, 1, 1));
        var saved = await store.SaveSubmissionAsync(new("submit", "submission", "synthetic-exam", "candidate", "participation", 1,
            "device", 1, 1, new Dictionary<string, AnswerValue> { ["q1"] = AnswerValue.Selected("A") }), DateTimeOffset.UtcNow, "receipt");
        Assert.Equal(SubmissionPersistenceStatus.Saved, saved.Status);
        Assert.All(await InspectAsync(), c => Assert.True(c.Passed, c.Name));
        await ExecuteAsync(damage);
        Assert.Contains(await InspectAsync(), c => c.Name == "submission-receipts" && !c.Passed);
        Assert.Equal(saved.Submission!.Receipt, (await store.FindSubmissionAsync("synthetic-exam", "candidate"))!.Receipt);
    }

    [Fact]
    public async Task ActualBusinessMiddlewareBlocksUntilRecoveryPasses_AndKeepsActiveNodeGate()
    {
        const string token = "synthetic-recovery-operator-token-32-chars";
        var identity = new ServerIdentityOptions { ServerId = "server", NodeId = "node", ServerEpoch = 1 };
        var guard = new BusinessRecoveryGuard();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new ExamApiSecurityOptions(token, "operator"));
        builder.Services.AddSingleton<IDeviceCredentialValidator, NoDeviceCredentials>();
        builder.Services.AddSingleton(new ServerNodeEnvelope("server", "node", 1));
        builder.Services.AddSingleton(new ActiveNodeGuard(_database.DataSource, identity, true));
        builder.Services.AddSingleton(guard);
        await using var app = builder.Build();
        app.UseMiddleware<ExamApiAccessMiddleware>();
        app.MapGet("/api/v1/synthetic", () => "allowed");
        app.MapPost("/hubs/exam-events/negotiate", () => "hub boundary reached");
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/v1/synthetic")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync("/hubs/exam-events/negotiate", null)).StatusCode);
        guard.Observe(await InspectAsync(), DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/synthetic")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/hubs/exam-events/negotiate", null)).StatusCode);
        await ExecuteAsync("""
            BEGIN;
            INSERT INTO exam.server_state VALUES ('server', 'node', 2, 'EmergencySingleNode', 1, now());
            INSERT INTO exam.server_nodes VALUES ('server', 'node', 'Primary', true, false, 0, true, true, now());
            COMMIT;
            """);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/v1/synthetic")).StatusCode);
    }

    [Fact]
    public async Task ActualRuntimeRegistrationRunsRecoveryBeforeBusinessWorkers()
    {
        CreateExam();
        var identity = new ServerIdentityOptions { ServerId = "server", NodeId = "node", ServerEpoch = 1 };
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Exam"] = _database.ConnectionString,
            ["ExamSecurity:OperatorToken"] = "synthetic-runtime-registration-token-32-chars",
            ["ExamSecurity:OperatorId"] = "operator",
            ["ExamServer:ServerId"] = "server", ["ExamServer:NodeId"] = "node", ["ExamServer:ServerEpoch"] = "1"
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSignalR();
        builder.Services.AddPostgresExamRuntime(builder.Configuration, identity, true);
        await using var app = builder.Build();
        var hosted = app.Services.GetServices<IHostedService>().ToArray();
        Assert.Contains(hosted, service => service is BusinessRecoveryWorker);
        Assert.Equal(5, hosted.Count(service => service.GetType().IsGenericType &&
            service.GetType().GetGenericTypeDefinition() == typeof(RecoveryGatedWorker<>)));
        var guard = app.Services.GetRequiredService<BusinessRecoveryGuard>();
        Assert.False(guard.Snapshot.Ready);
        await app.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!guard.Snapshot.Ready) await Task.Delay(10, timeout.Token);
        Assert.All(guard.Snapshot.Checks, c => Assert.True(c.Passed, c.Name));
        await app.StopAsync(timeout.Token);
    }

    [Theory]
    [InlineData("wrong-exam")]
    [InlineData("wrong-version")]
    [InlineData("missing-pdf")]
    [InlineData("invalid-pdf")]
    [InlineData("unsafe-path")]
    [InlineData("server-answer")]
    [InlineData("not-zip")]
    public async Task ValidEncryptionDoesNotHideInvalidStudentPackage(string damage)
    {
        var bytes = damage switch
        {
            "wrong-exam" => SyntheticRecoveryPaper.Build(examId: "wrong-exam"),
            "wrong-version" => SyntheticRecoveryPaper.Build(version: 2),
            "missing-pdf" => SyntheticRecoveryPaper.Build(omitPdf: true),
            "invalid-pdf" => SyntheticRecoveryPaper.Build(invalidPdf: true),
            "unsafe-path" => SyntheticRecoveryPaper.Build(extraPath: "../outside.pdf"),
            "server-answer" => SyntheticRecoveryPaper.Build(extraPath: "server-only/answers.json"),
            _ => new byte[9000]
        };
        await StoreResourceAsync(bytes);
        Assert.Contains(await InspectAsync(), c => c.Name == "published-resources" && !c.Passed);
    }

    [Fact]
    public async Task StudentManifestMustMatchCurrentPublishedQuestionStructure()
    {
        CreateExam();
        await StoreResourceAsync(SyntheticRecoveryPaper.Build(questionId: "different-question"));
        var published = new PostgresExamVersioningService(_database.DataSource).PublishPaper(new("synthetic-exam", 0,
            "synthetic-resource", SyntheticRecoveryPaper.Structure(), [], new(true, true, true, true), "operator",
            "合成结构不一致", DateTimeOffset.UtcNow, "publish-mismatch"));
        Assert.True(published.Succeeded, published.Error);
        Assert.Contains(await InspectAsync(), c => c.Name == "published-resources" && !c.Passed);
    }

    private async Task StoreResourceAsync(byte[]? payload = null)
    {
        using var plain = new MemoryStream(payload ?? SyntheticRecoveryPaper.Build());
        using var cipher = new MemoryStream();
        var envelope = await new PackageEncryptionService(4096).EncryptAsync(plain, cipher, "synthetic-resource", 1, [new("node", _certificate)]);
        await using var command = _database.DataSource.CreateCommand("""
            INSERT INTO exam.versioned_resources VALUES ('synthetic-resource', 'synthetic-exam', 'paper-package', 1, $1, $2::jsonb, now(), 'synthetic-operator')
            """);
        command.Parameters.AddWithValue(cipher.ToArray());
        command.Parameters.AddWithValue(JsonSerializer.Serialize(envelope, JsonDefaults.Create()));
        await command.ExecuteNonQueryAsync();
    }
    private sealed class TestUnwrapper(X509Certificate2 certificate) : IPackageKeyUnwrapper
    { public byte[] Unwrap(EncryptedPackageEnvelope envelope) => PackageEncryptionService.UnwrapDataKey(envelope, "node", certificate); }
    private sealed class MissingKey : IPackageKeyUnwrapper
    { public byte[] Unwrap(EncryptedPackageEnvelope envelope) => throw new CryptographicException("synthetic-secret"); }
    private sealed class NoDeviceCredentials : IDeviceCredentialValidator
    {
        public ValueTask<bool> ValidateActiveDeviceAsync(string deviceId, string credential, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
        public ValueTask<bool> IsActiveDeviceAsync(string deviceId, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    }
}
