using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Exam.Contracts.Common;
using Exam.Contracts.Versioning;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.Infrastructure.Security;
using Exam.IntegrationTests.Postgres;
using Exam.Server.Runtime;
using System.Text.Json;
using Exam.Contracts.Importing;
using Exam.Domain.Scoring;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Exam.IntegrationTests.Versioning;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed partial class PostgresExamVersioningServiceTests(PostgresDatabaseFixture fixture)
{
    [Fact]
    public async Task ScheduleChange_PersistsFullWindowAndDeliversItFromPostgresAfterRestart()
    {
        var examId = "schedule-delivery-" + Guid.NewGuid().ToString("N");
        var deviceId = "device-" + Guid.NewGuid().ToString("N");
        var participantId = "participation-" + Guid.NewGuid().ToString("N");
        var service = new PostgresExamVersioningService(fixture.DataSource);
        var now = InitialNow.AddYears(100);
        Assert.True(service.CreateExam(new(examId,
            new(now.AddHours(1), now.AddHours(2)), now, examId + "-create")).Succeeded);
        await using (var seed = fixture.DataSource.CreateCommand("""
            WITH device AS (
              INSERT INTO exam.activation_devices
                (device_id,client_number,activation_date,activation_status,protected_credential,credential_version,created_at,version)
                VALUES ($1,$2,DATE '2026-10-02','active','synthetic-verifier-not-a-secret',1,$5,1)
                RETURNING device_id),
            candidate AS (
              INSERT INTO exam.candidate_roster_entries
                (exam_id,candidate_id,candidate_name,class_text,admission_no)
                VALUES ($3,'synthetic-candidate','合成考生','001','000001')
                RETURNING exam_id,candidate_id)
            INSERT INTO exam.participations
              (participation_id,exam_id,candidate_id,entry_version,business_state,authorized_device_id,authorization_version,version)
              SELECT $4,candidate.exam_id,candidate.candidate_id,1,'waiting',device.device_id,1,1
                FROM device CROSS JOIN candidate;
            """))
        {
            seed.Parameters.AddWithValue(deviceId);
            seed.Parameters.AddWithValue(Random.Shared.NextInt64(10000000000,99999999999).ToString());
            seed.Parameters.AddWithValue(examId);
            seed.Parameters.AddWithValue(participantId);
            seed.Parameters.AddWithValue(now);
            await seed.ExecuteNonQueryAsync();
        }
        var window = new ScheduleWindow(now.AddHours(2),now.AddHours(4));
        Assert.True(service.UpdateSchedule(new(examId,1,window,now,examId+"-update")).Succeeded);
        var commands = new Exam.Application.Commands.ReliableCommandService(
            new("synthetic-server","synthetic-node",1),
            new Exam.Infrastructure.Postgres.Commands.PostgresCommandEventStore(fixture.DataSource),
            new Exam.Infrastructure.Postgres.Commands.PostgresParticipationAccessReader(fixture.DataSource));
        var due = await commands.GetDueForDeviceAsync(participantId,1,deviceId,1,"synthetic-server",1);
        Assert.True(due.IsSuccess,due.Message);
        var delivery = Assert.Single(due.Value!);
        Assert.Equal(CommandEventType.ScheduleChanged,delivery.EventType);
        Assert.Equal(2,delivery.StateVersion);
        Assert.Equal(window.StartsAt,delivery.NewScheduledStart);
        Assert.Equal(window.EndsAt,delivery.NewScheduledEnd);
        Assert.Equal(TimeSpan.FromHours(8),delivery.NewScheduledEnd!.Value.Offset);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ScheduleRetryIgnoresArrivalTimeButPreservesOriginalAuditAndLegacyHashes(bool legacyHash, bool withAudit)
    {
        var now = InitialNow.AddYears(100);
        var exam = "audit-replay-" + Guid.NewGuid().ToString("N");
        var service = new PostgresExamVersioningService(fixture.DataSource);
        Assert.True(service.CreateExam(new(exam, new(now.AddHours(1), now.AddHours(2)), now, exam + "-create")).Succeeded);
        var request = new UpdateScheduleRequest(exam, 1, new(now.AddHours(2), now.AddHours(3)), now, exam + "-update")
        { Operator = withAudit ? "synthetic-operator" : null, Reason = withAudit ? "合成延时" : null };
        Assert.True(service.UpdateSchedule(request).Succeeded);
        if (legacyHash)
        {
            var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(request, JsonDefaults.Create()))));
            await using var legacy = fixture.DataSource.CreateCommand("UPDATE exam.version_idempotency SET payload_hash=$1 WHERE request_id=$2;");
            legacy.Parameters.AddWithValue(hash); legacy.Parameters.AddWithValue(request.RequestId);
            Assert.Equal(1, await legacy.ExecuteNonQueryAsync());
        }
        var reopened = new PostgresExamVersioningService(fixture.DataSource);
        Assert.Equal(2, reopened.UpdateSchedule(request with { Now = now.AddMinutes(1) }).Value!.Versions.ScheduleVersion);
        foreach (var changed in new[] { request with { Operator = "other-operator" },
                     request with { Reason = "另一原因" }, request with { Schedule = request.Schedule with { EndsAt = now.AddHours(4) } } })
            Assert.Equal(VersioningErrorCode.IdempotencyConflict, reopened.UpdateSchedule(changed).ErrorCode);
        await using var query = fixture.DataSource.CreateCommand(
            "SELECT payload::text FROM exam.version_events WHERE exam_id=$1 AND operation_type='schedule.update';");
        query.Parameters.AddWithValue(exam);
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var original = JsonSerializer.Deserialize<UpdateScheduleRequest>(reader.GetString(0), JsonDefaults.Create());
        Assert.Equal(request, original);
        Assert.False(await reader.ReadAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportedRulesSurviveCommandReplay_WithoutLeakingAnswersIntoSnapshots(bool legacyReceipt)
    {
        // Keep these rule-only fixtures outside the shared schedule-worker clock window.
        var rulesNow = InitialNow.AddYears(100);
        var examId = "imported-rules-" + Guid.NewGuid().ToString("N");
        var manifest = new ExamManifest(1, examId, "合成规则考试",
            [new("subject", "科目", "1")],
            [new("group", "subject", "单选", "single", 1, 1, true, "1", new(new("1", "0", "0"), null))],
            [new("q1", 1, "subject", "group", "questions/合成规则考试_1.tex", ["A", "B"], null)], []);
        var key = new AnswerKeyFile(1, examId, [new("q1", 1, ["A"])]);
        var manifestJson = JsonSerializer.Serialize(manifest, ImportJson.Create());
        var keyJson = JsonSerializer.Serialize(key, ImportJson.Create());
        var rules = ImportedGradingRules.Create(manifestJson, keyJson);
        var service = new PostgresExamVersioningService(fixture.DataSource);
        Assert.True(service.CreateExam(new(examId, new(rulesNow.AddHours(1), rulesNow.AddHours(2)), rulesNow, examId + "-create")).Succeeded);
        var artifactId = examId + "-paper";
        await StorePaperPackageAsync(examId, artifactId, 1);
        Assert.True(service.PublishPaper(new(examId, 0, artifactId, rules.Structure, [], new(true, true, true, true),
            "A", "合成接线测试", rulesNow, examId + "-paper")).Succeeded);
        var request = new PublishGradingRulesRequest(examId, 0, "rules", new(true, true, true), "A", "合成接线测试",
            rulesNow, examId + "-grading", manifestJson, keyJson);
        var published = service.PublishGradingRules(request);
        Assert.True(published.Succeeded, published.Error);
        if (legacyReceipt)
        {
            var oldHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(request, JsonDefaults.Create()))));
            await using var legacy = fixture.DataSource.CreateCommand(
                "UPDATE exam.version_idempotency SET payload_hash=$1 WHERE request_id=$2");
            legacy.Parameters.AddWithValue(oldHash);
            legacy.Parameters.AddWithValue(request.RequestId);
            Assert.Equal(1, await legacy.ExecuteNonQueryAsync());
        }
        var restarted = new PostgresExamVersioningService(fixture.DataSource);
        // A different server receive time must replay the original receipt, even after restart.
        var retry = request with { Now = request.Now.AddMinutes(2) };
        var replies = await Task.WhenAll(Enumerable.Range(1, 3).Select(i => Task.Run(() =>
            restarted.PublishGradingRules(retry with { Now = retry.Now.AddSeconds(i) }))));
        foreach (var reply in replies)
        {
            Assert.True(reply.Succeeded, reply.Error);
            Assert.Equal(JsonSerializer.Serialize(published, JsonDefaults.Create()),
                JsonSerializer.Serialize(reply, JsonDefaults.Create()));
        }
        foreach (var changed in new[]
        {
            retry with { ExamId = examId + "-other" },
            retry with { ExpectedVersion = 1 },
            retry with { RuleSetId = "another-rule-set" },
            retry with { Operator = "another-operator" },
            retry with { Reason = "another-reason" },
            retry with { Validation = new(false, true, true) },
            retry with { ManifestJson = manifestJson + " " },
            retry with { AnswerKeyJson = keyJson + " " }
        })
            Assert.Equal(VersioningErrorCode.IdempotencyConflict, restarted.PublishGradingRules(changed).ErrorCode);
        Assert.True(restarted.PublishGradingRules(request with
        {
            RequestId = examId + "-grading-next", ExpectedVersion = 1, RuleSetId = "rules-next", Now = retry.Now
        }).Succeeded);
        Assert.Equal(1, restarted.PublishGradingRules(retry).Value!.Versions.GradingVersion);
        Assert.Equal(2, restarted.GetExam(examId).Value!.Versions.GradingVersion);
        // A new write replays the durable command stream, including the imported rule validation.
        var updated = restarted.UpdateSchedule(new(examId, 1, new(rulesNow.AddHours(2), rulesNow.AddHours(3)), rulesNow, examId + "-schedule"));
        Assert.True(updated.Succeeded, updated.Error);
        Assert.Equal(2, updated.Value!.Versions.GradingVersion);
        Assert.True(updated.Value.Paper!.Structure.Questions[0].AllowAbstain);
        var snapshot = JsonSerializer.Serialize(updated.Value, JsonDefaults.Create());
        Assert.DoesNotContain("correctOptionIds", snapshot);
        Assert.DoesNotContain("answerKeyJson", snapshot);
    }

    private static readonly DateTimeOffset InitialNow =
        new(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Persists_versions_serializes_concurrent_updates_and_preserves_terminal_state()
    {
        var examId = $"versioning-{Guid.NewGuid():N}";
        var service = new PostgresExamVersioningService(fixture.DataSource);
        var created = service.CreateExam(new CreateVersionedExamRequest(
            examId,
            new ScheduleWindow(InitialNow.AddHours(1), InitialNow.AddHours(2)),
            InitialNow,
            "create"));
        var replayedCreate = service.CreateExam(new CreateVersionedExamRequest(
            examId,
            new ScheduleWindow(InitialNow.AddHours(1), InitialNow.AddHours(2)),
            InitialNow,
            "create"));
        var conflictingCreate = service.CreateExam(new CreateVersionedExamRequest(
            $"{examId}-other",
            new ScheduleWindow(InitialNow.AddHours(1), InitialNow.AddHours(2)),
            InitialNow,
            "create"));
        await StorePaperPackageAsync(examId, "synthetic-paper-v1", 1);
        var paper = service.PublishPaper(new PublishPaperRequest(
            examId,
            0,
            "synthetic-paper-v1",
            Structure(questionNo: 1),
            [],
            new PaperPublicationValidation(true, true, true, true),
            "integration-test",
            "initial synthetic paper",
            InitialNow,
            "publish-paper-v1"));
        await MarkInstructionsReadyAsync(examId);
        var started = service.UpdateSchedule(new UpdateScheduleRequest(
            examId,
            1,
            new ScheduleWindow(InitialNow.AddHours(-1), InitialNow.AddHours(2)),
            InitialNow,
            "start-by-schedule"));
        var structuralChange = service.PublishPaper(new PublishPaperRequest(
            examId,
            1,
            "synthetic-paper-v2",
            Structure(questionNo: 2),
            ["q1"],
            new PaperPublicationValidation(true, true, true, true),
            "integration-test",
            "illegal renumbering",
            InitialNow,
            "illegal-paper-v2"));

        var concurrent = await Task.WhenAll(
            Task.Run(() => service.UpdateSchedule(new UpdateScheduleRequest(
                examId,
                2,
                new ScheduleWindow(InitialNow.AddHours(-1), InitialNow.AddHours(3)),
                InitialNow,
                "concurrent-three-hours"))),
            Task.Run(() => service.UpdateSchedule(new UpdateScheduleRequest(
                examId,
                2,
                new ScheduleWindow(InitialNow.AddHours(-1), InitialNow.AddHours(4)),
                InitialNow,
                "concurrent-four-hours"))));

        var ended = service.UpdateSchedule(new UpdateScheduleRequest(
            examId,
            3,
            new ScheduleWindow(InitialNow.AddHours(-2), InitialNow.AddHours(-1)),
            InitialNow,
            "end-by-schedule"));
        var extendedAfterEnd = service.UpdateSchedule(new UpdateScheduleRequest(
            examId,
            4,
            new ScheduleWindow(InitialNow.AddHours(-2), InitialNow.AddHours(8)),
            InitialNow,
            "extend-ended"));
        var reloaded = new PostgresExamVersioningService(fixture.DataSource).GetExam(examId);

        Assert.True(created.Succeeded);
        Assert.True(replayedCreate.Succeeded);
        Assert.Equal(created.Value?.ExamId, replayedCreate.Value?.ExamId);
        Assert.Equal(created.Value?.Versions, replayedCreate.Value?.Versions);
        Assert.Equal(created.Value?.Schedule, replayedCreate.Value?.Schedule);
        Assert.Equal(VersioningErrorCode.IdempotencyConflict, conflictingCreate.ErrorCode);
        Assert.Equal(1, paper.Value?.Versions.PaperVersion);
        Assert.Equal(ExamLifecycle.Running, started.Value?.LifecycleState);
        Assert.False(structuralChange.Succeeded);
        Assert.Equal(VersioningErrorCode.StructuralChangeRejected, structuralChange.ErrorCode);
        Assert.Single(concurrent, result => result.Succeeded);
        Assert.Single(concurrent, result => result.ErrorCode == VersioningErrorCode.VersionConflict);
        Assert.Equal(ExamLifecycle.Ended, ended.Value?.LifecycleState);
        Assert.Equal(ExamLifecycle.Ended, extendedAfterEnd.Value?.LifecycleState);
        Assert.True(reloaded.Succeeded);
        Assert.Equal(new VersionVector(5, 1, 0), reloaded.Value?.Versions);
        Assert.Equal(ExamLifecycle.Ended, reloaded.Value?.LifecycleState);

        await using var access = fixture.DataSource.CreateCommand(
            "SELECT lifecycle, package_ready, instructions_ready, paper_version FROM exam.exam_entry_access WHERE exam_id = $1;");
        access.Parameters.AddWithValue(examId);
        await using var reader = await access.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("ended", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.Equal(1, reader.GetInt64(3));
    }

    [Fact]
    public async Task Due_schedule_worker_starts_and_ends_a_ready_exam()
    {
        var examId = $"scheduled-{Guid.NewGuid():N}";
        var service = new PostgresExamVersioningService(fixture.DataSource);
        Assert.True(service.CreateExam(new CreateVersionedExamRequest(
            examId,
            new ScheduleWindow(InitialNow.AddHours(1), InitialNow.AddHours(2)),
            InitialNow,
            $"create-{examId}")).Succeeded);
        await StorePaperPackageAsync(examId, $"paper-{examId}", 1);
        Assert.True(service.PublishPaper(new PublishPaperRequest(
            examId,
            0,
            $"paper-{examId}",
            Structure(questionNo: 1),
            [],
            new PaperPublicationValidation(true, true, true, true),
            "integration-test",
            "schedule worker fixture",
            InitialNow,
            $"publish-{examId}")).Succeeded);
        await MarkInstructionsReadyAsync(examId);

        var clock = new MutableTimeProvider(InitialNow.AddMinutes(90));
        var processor = new PostgresScheduleProcessor(
            fixture.DataSource,
            service,
            new ActiveNodeGuard(
                fixture.DataSource,
                new ServerIdentityOptions
                {
                    ServerId = $"schedule-server-{Guid.NewGuid():N}",
                    NodeId = "node-a",
                    ServerEpoch = 1
                },
                allowUninitializedState: true),
            clock,
            NullLogger<PostgresScheduleProcessor>.Instance);

        Assert.Equal(1, await processor.ExecuteDueAsync());
        Assert.Equal(ExamLifecycle.Running, service.GetExam(examId).Value?.LifecycleState);
        clock.Now = InitialNow.AddHours(3);
        Assert.Equal(1, await processor.ExecuteDueAsync());
        Assert.Equal(ExamLifecycle.Ended, service.GetExam(examId).Value?.LifecycleState);
        Assert.Equal(0, await processor.ExecuteDueAsync());
    }

    private async Task StorePaperPackageAsync(string examId, string resourceId, long version)
    {
        using var certificate = CreateCertificate();
        var policy = new ManagedNodeEncryptionPolicy(
            new HashSet<string>(["node-a"], StringComparer.Ordinal));
        var encryption = new PackageEncryptionService(4096, policy);
        await using var encrypted = new MemoryStream();
        var envelope = await encryption.EncryptAsync(
            new MemoryStream(RandomNumberGenerator.GetBytes(1024)),
            encrypted,
            resourceId,
            version,
            [new("node-a", certificate)]);
        await new PostgresVersionedResourceStore(fixture.DataSource, policy).SaveAsync(
            new VersionedEncryptedResource(
                resourceId,
                examId,
                "paper-package",
                version,
                encrypted.ToArray(),
                envelope,
                InitialNow,
                "integration-test"));
    }

    private async Task MarkInstructionsReadyAsync(string examId)
    {
        await using var command = fixture.DataSource.CreateCommand(
            "UPDATE exam.exam_entry_access SET instructions_ready = true WHERE exam_id = $1;");
        command.Parameters.AddWithValue(examId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=node-a",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static PaperStructure Structure(int questionNo) =>
        new([
            new PaperQuestionStructure(
                "q1",
                questionNo,
                "subject-1",
                "group-1",
                "single",
                ["A", "B"])
        ]);
}
