using Exam.Application.Activation;
using Exam.Application.Participation;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Infrastructure.Postgres.Activation;
using Exam.Infrastructure.Postgres.Participation;
using Exam.IntegrationTests.Postgres;
using Npgsql;

namespace Exam.IntegrationTests.Participation;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresParticipationPersistenceTests(PostgresDatabaseFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 9, 22, 2, 0, 0, TimeSpan.Zero);
    private const string ExamId = "pg-exam-1";

    [Fact]
    public async Task LoginEntryDecisionAndSubmissionRecoverAcrossServiceInstances()
    {
        await ResetAndSeedAsync();
        var clock = new FixedTimeProvider(Start.AddMinutes(-10));
        var activation = CreateActivationService(clock);
        await ActivateAsync(activation, "pg-device-1", "secret-1", "1");
        await ActivateAsync(activation, "pg-device-2", "secret-2", "2");
        var firstService = CreateParticipationService(activation, clock);

        var firstSession = await LoginAsync(firstService, "pg-device-1", "secret-1", "login-1");
        var preparation = await firstService.PrepareEntryAsync(new("prepare-1", firstSession.SessionId, ExamId));
        var participation = await ConfirmAsync(firstService, firstSession, preparation, "confirm-1");
        Assert.Equal(ParticipationBusinessState.Waiting, participation.BusinessState);

        var restarted = CreateParticipationService(activation, clock);
        var restored = await restarted.FindParticipationAsync(participation.ParticipationId);
        Assert.Equal(participation, restored);

        var secondSession = await LoginAsync(restarted, "pg-device-2", "secret-2", "login-2");
        var pending = await restarted.PrepareEntryAsync(new("prepare-2", secondSession.SessionId, ExamId));
        Assert.Equal(EntryPreparationStatus.DeviceDecisionPending, pending.Status);

        await using (var setVersions = fixture.DataSource.CreateCommand("""
                         UPDATE exam.participations
                            SET current_paper_version = 7,
                                current_grading_version = 9
                          WHERE participation_id = @participation_id;
                         """))
        {
            setVersions.Parameters.AddWithValue("participation_id", participation.ParticipationId);
            await setVersions.ExecuteNonQueryAsync();
        }

        var decisionRequest = new DeviceDecisionRequest(
            "decision-1",
            participation.ParticipationId,
            "pg-device-2",
            participation.Version,
            "admin-1",
            "允许备用设备继续考试");
        var decision = await restarted.DecideDeviceAsync(decisionRequest);
        var replayedDecision = await CreateParticipationService(activation, clock).DecideDeviceAsync(decisionRequest);
        Assert.Equal(decision, replayedDecision);

        var oldAuthorization = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            restarted.EnsureAuthorizedForWriteAsync(new(
                participation.ParticipationId,
                "pg-device-1",
                participation.EntryVersion,
                participation.AuthorizationVersion)).AsTask());
        Assert.Equal(ParticipationErrorCodes.AuthorizationRevoked, oldAuthorization.Code);

        var resumedPreparation = await restarted.PrepareEntryAsync(
            new("prepare-after-decision", secondSession.SessionId, ExamId));
        var resumed = await ConfirmAsync(
            restarted,
            secondSession,
            resumedPreparation,
            "confirm-after-decision");
        await restarted.AdvanceExamToAnsweringAsync(new("start-1", ExamId));
        var answering = await restarted.FindParticipationAsync(resumed.ParticipationId);
        var submitted = await restarted.FinalizeAsync(new(
            "finalize-1",
            answering!.ParticipationId,
            answering.AuthorizedDeviceId,
            answering.EntryVersion,
            answering.AuthorizationVersion));
        Assert.Equal(ParticipationBusinessState.Submitted, submitted.BusinessState);

        await using var verify = fixture.DataSource.CreateCommand("""
            SELECT current_paper_version, current_grading_version, business_state,
                   COUNT(*) OVER ()
              FROM exam.participations
             WHERE exam_id = @exam_id AND candidate_id = 'pg-candidate-1';
            """);
        verify.Parameters.AddWithValue("exam_id", ExamId);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(7, reader.GetInt64(0));
        Assert.Equal(9, reader.GetInt64(1));
        Assert.Equal("submitted", reader.GetString(2));
        Assert.Equal(1, reader.GetInt64(3));
    }

    [Fact]
    public async Task StartAndWithdrawUseDatabaseLocksAndHaveOneSerializedWinner()
    {
        await ResetAndSeedAsync();
        var clock = new FixedTimeProvider(Start.AddMinutes(-10));
        var activation = CreateActivationService(clock);
        await ActivateAsync(activation, "race-device", "race-secret", "race");
        var service = CreateParticipationService(activation, clock);
        var session = await LoginAsync(service, "race-device", "race-secret", "race-login");
        var preparation = await service.PrepareEntryAsync(new("race-prepare", session.SessionId, ExamId));
        var waiting = await ConfirmAsync(service, session, preparation, "race-confirm");

        using var barrier = new Barrier(2);
        var startTask = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await service.AdvanceExamToAnsweringAsync(new("race-start", ExamId));
        });
        var withdrawTask = Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await Record.ExceptionAsync(() => service.WithdrawAsync(new(
                "race-withdraw",
                session.SessionId,
                waiting.ParticipationId,
                waiting.Version)).AsTask());
        });

        var startResult = await startTask;
        var withdrawError = await withdrawTask;
        var current = await service.FindParticipationAsync(waiting.ParticipationId);
        if (withdrawError is null)
        {
            Assert.Equal(0, startResult.ChangedParticipationCount);
            Assert.Equal(ParticipationBusinessState.NotParticipating, current!.BusinessState);
        }
        else
        {
            var rule = Assert.IsType<ParticipationRuleException>(withdrawError);
            Assert.True(rule.Code is ParticipationErrorCodes.VersionConflict or ParticipationErrorCodes.InvalidBusinessState);
            Assert.Equal(1, startResult.ChangedParticipationCount);
            Assert.Equal(ParticipationBusinessState.Answering, current!.BusinessState);
        }
    }

    private ActivationService CreateActivationService(TimeProvider clock) =>
        new(
            new PostgresActivationPersistence(fixture.DataSource),
            new Pbkdf2DeviceCredentialProtector(),
            clock);

    private ParticipationService CreateParticipationService(
        ActivationService activation,
        TimeProvider clock) =>
        new(
            new PostgresParticipationPersistence(fixture.DataSource),
            activation,
            new PostgresCandidateRosterDirectory(fixture.DataSource),
            new PostgresExamEntryDirectory(fixture.DataSource),
            clock);

    private static ValueTask<StudentSessionResult> LoginAsync(
        ParticipationService service,
        string deviceId,
        string credential,
        string requestId) =>
        service.LoginAsync(new LoginStudentRequest(
            requestId,
            deviceId,
            credential,
            "张三",
            "001",
            "0001"));

    private static ValueTask<ParticipationResult> ConfirmAsync(
        ParticipationService service,
        StudentSessionResult session,
        EntryPreparationResult preparation,
        string requestId) =>
        service.ConfirmParticipationAsync(new ConfirmParticipationRequest(
            requestId,
            session.SessionId,
            ExamId,
            preparation.PreparationId,
            preparation.Resources!.PaperVersion,
            preparation.Resources.InstructionsVersion));

    private static async Task ActivateAsync(
        ActivationService service,
        string deviceId,
        string credential,
        string suffix)
    {
        var request = await service.SubmitRequestAsync(new($"activate-{suffix}", deviceId, credential));
        await service.ApproveAsync(new($"approve-{suffix}", request.ActivationRequestId));
    }

    private async Task ResetAndSeedAsync()
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

            INSERT INTO exam.candidate_roster_entries
                (exam_id, candidate_id, candidate_name, class_text, admission_no)
            VALUES
                ('pg-exam-1', 'pg-candidate-1', '张三', '001', '0001');

            INSERT INTO exam.exam_entry_access
                (exam_id, scheduled_start, scheduled_end, lifecycle,
                 package_ready, instructions_ready, encrypted_package_uri,
                 package_length, paper_version, instructions_version, unlock_reference)
            VALUES
                ('pg-exam-1', TIMESTAMPTZ '2026-09-22 02:00:00+00',
                 TIMESTAMPTZ '2026-09-22 04:00:00+00', 'scheduled',
                 true, true, 'https://exam.invalid/pg-exam-1.enc',
                 2048, 1, 1, 'protected-key-reference');
            """);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
