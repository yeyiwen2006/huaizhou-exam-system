using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Exam.Contracts.Common;
using Exam.Contracts.Versioning;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.Infrastructure.Security;
using Exam.IntegrationTests.Postgres;
using Exam.Server.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Exam.IntegrationTests.Versioning;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresExamVersioningServiceTests(PostgresDatabaseFixture fixture)
{
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
