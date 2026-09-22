using System.Text.Json;
using Exam.Application.Participation;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Student;
using Exam.Contracts.Versioning;
using Exam.Infrastructure.Postgres.Student;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.IntegrationTests.Postgres;

namespace Exam.IntegrationTests.Student;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresStudentExamMetadataServiceTests(PostgresDatabaseFixture fixture)
{
    [Fact]
    public async Task PublicationPersistsMetadataInstructionsAndIdempotentReceiptTogether()
    {
        var seed = await SeedAsync();
        var service = new PostgresStudentExamMetadataService(fixture.DataSource);
        var request = Request($"{seed.ExamId}-publish");
        var published = await service.UpdateAsync(seed.ExamId, request, "operator-1");
        var restarted = new PostgresStudentExamMetadataService(fixture.DataSource);
        Assert.Equal(published, await restarted.UpdateAsync(seed.ExamId, request, "operator-1"));
        Assert.Equal(1, published.Version);
        Assert.Equal(1, published.InstructionsVersion);

        var instructions = await restarted.GetInstructionsAsync(seed.PreparationId, seed.SessionId, seed.DeviceId);
        Assert.Equal(published.InstructionsVersion, instructions.Version);
        Assert.Equal(request.InstructionLines, instructions.Lines);
        var overview = Assert.Single(await new PostgresStudentDirectory(fixture.DataSource)
            .ListExamsAsync(seed.SessionId, seed.DeviceId));
        Assert.Equal(request.Name, overview.Name);
        Assert.Equal(request.CategoryName, overview.CategoryName);
        Assert.Equal(request.Subjects, overview.Subjects);

        var conflict = await Assert.ThrowsAsync<ParticipationRuleException>(() => restarted.UpdateAsync(
            seed.ExamId, request with { Name = "不同名称" }, "operator-1").AsTask());
        Assert.Equal(ParticipationErrorCodes.IdempotencyConflict, conflict.Code);
        var actorConflict = await Assert.ThrowsAsync<ParticipationRuleException>(() => restarted.UpdateAsync(
            seed.ExamId, request, "operator-2").AsTask());
        Assert.Equal(ParticipationErrorCodes.IdempotencyConflict, actorConflict.Code);

        var nextRequest = request with
        {
            RequestId = $"{seed.ExamId}-publish-next",
            ExpectedVersion = 1,
            InstructionLines = ["合成科目（满分8分）", "一、合成题组：共2小题，每小题4分。"]
        };
        var second = await restarted.UpdateAsync(seed.ExamId, nextRequest, "operator-1");
        Assert.Equal(2, second.Version);
        Assert.Equal(2, second.InstructionsVersion);
        var changedSchedule = new PostgresExamVersioningService(fixture.DataSource).UpdateSchedule(new(
            seed.ExamId, 1,
            new ScheduleWindow(DateTimeOffset.UtcNow.AddMinutes(15), DateTimeOffset.UtcNow.AddHours(3)),
            DateTimeOffset.UtcNow, $"{seed.ExamId}-schedule-update"));
        Assert.True(changedSchedule.Succeeded);
        var stale = await Assert.ThrowsAsync<ParticipationRuleException>(() => restarted.GetInstructionsAsync(
            seed.PreparationId, seed.SessionId, seed.DeviceId).AsTask());
        Assert.Equal(ParticipationErrorCodes.ResourcesNotReady, stale.Code);

        await using var count = fixture.DataSource.CreateCommand("""
            SELECT instructions_version, instructions_ready,
                   (SELECT COUNT(*) FROM exam.student_exam_instructions WHERE exam_id = $1)
              FROM exam.exam_entry_access WHERE exam_id = $1;
            """);
        count.Parameters.AddWithValue(seed.ExamId);
        await using (var reader = await count.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2L, reader.GetInt64(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal(2L, reader.GetInt64(2));
        }
        Assert.Equal(published, await restarted.UpdateAsync(seed.ExamId, request, "operator-1"));
    }

    [Fact]
    public async Task ConcurrentMetadataUpdatesHaveOneWinner_AndCannotMutateRunningExam()
    {
        var seed = await SeedAsync();
        var service = new PostgresStudentExamMetadataService(fixture.DataSource);
        var request = Request($"{seed.ExamId}-publish");
        var outcomes = await Task.WhenAll(Enumerable.Range(1, 2).Select(async index =>
            await Record.ExceptionAsync(() => service.UpdateAsync(seed.ExamId,
                request with { RequestId = $"{request.RequestId}-{index}" }, "operator-1").AsTask())));
        Assert.Single(outcomes, outcome => outcome is null);
        Assert.Equal(ParticipationErrorCodes.VersionConflict,
            Assert.IsType<ParticipationRuleException>(Assert.Single(outcomes, outcome => outcome is not null)).Code);

        await using var start = fixture.DataSource.CreateCommand("""
            UPDATE exam.exam_entry_access SET lifecycle = 'running' WHERE exam_id = $1;
            """);
        start.Parameters.AddWithValue(seed.ExamId);
        await start.ExecuteNonQueryAsync();
        var denied = await Assert.ThrowsAsync<ParticipationRuleException>(() => service.UpdateAsync(seed.ExamId,
            request with { RequestId = $"{request.RequestId}-after-start", ExpectedVersion = 1 }, "operator-1").AsTask());
        Assert.Equal(ParticipationErrorCodes.InvalidBusinessState, denied.Code);
    }

    [Fact]
    public async Task InstructionsRequireCurrentDeviceSessionAndAuthorizedRecovery()
    {
        var seed = await SeedAsync();
        var service = new PostgresStudentExamMetadataService(fixture.DataSource);
        await service.UpdateAsync(seed.ExamId, Request($"{seed.ExamId}-publish"), "operator-1");
        await Assert.ThrowsAsync<ParticipationRuleException>(() => service.GetInstructionsAsync(
            seed.PreparationId, seed.SessionId, "another-device").AsTask());
        await Assert.ThrowsAsync<ParticipationRuleException>(() => service.GetInstructionsAsync(
            seed.PreparationId, "another-session", seed.DeviceId).AsTask());

        await using var end = fixture.DataSource.CreateCommand("""
            UPDATE exam.exam_entry_access
               SET lifecycle = 'ended', scheduled_start = clock_timestamp() - interval '3 hours',
                   scheduled_end = clock_timestamp() - interval '1 hour'
             WHERE exam_id = $1;
            """);
        end.Parameters.AddWithValue(seed.ExamId);
        await end.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<ParticipationRuleException>(() => service.GetInstructionsAsync(
            seed.PreparationId, seed.SessionId, seed.DeviceId).AsTask());

        await using var participate = fixture.DataSource.CreateCommand("""
            INSERT INTO exam.participations
                (participation_id, exam_id, candidate_id, entry_version, business_state,
                 authorized_device_id, authorization_version, version)
            VALUES ($1, $2, $3, 1, 'answering', $4, 1, 1);
            """);
        participate.Parameters.AddWithValue($"{seed.ExamId}-participation");
        participate.Parameters.AddWithValue(seed.ExamId);
        participate.Parameters.AddWithValue(seed.CandidateId);
        participate.Parameters.AddWithValue(seed.DeviceId);
        await participate.ExecuteNonQueryAsync();
        Assert.Equal(1, (await service.GetInstructionsAsync(seed.PreparationId, seed.SessionId, seed.DeviceId)).Version);

        await using var submitted = fixture.DataSource.CreateCommand("""
            UPDATE exam.participations SET business_state = 'submitted' WHERE exam_id = $1;
            """);
        submitted.Parameters.AddWithValue(seed.ExamId);
        await submitted.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<ParticipationRuleException>(() => service.GetInstructionsAsync(
            seed.PreparationId, seed.SessionId, seed.DeviceId).AsTask());
    }

    private static UpdateStudentExamMetadataRequest Request(string requestId) => new(
        requestId, 0, "合成练习", "独立样例", ["合成科目"],
        ["合成科目（满分6分）", "一、合成题组：共2小题，每小题3分。"]);

    private async Task<Seed> SeedAsync()
    {
        var prefix = $"instructions-{Guid.NewGuid():N}";
        var seed = new Seed($"{prefix}-exam", $"{prefix}-candidate", $"{prefix}-device",
            $"{prefix}-session", $"{prefix}-preparation");
        var now = DateTimeOffset.UtcNow;
        var created = new PostgresExamVersioningService(fixture.DataSource).CreateExam(new CreateVersionedExamRequest(
            seed.ExamId, new ScheduleWindow(now.AddMinutes(10), now.AddHours(2)), now, $"{prefix}-create"));
        Assert.True(created.Succeeded);
        var clientNumber = (BitConverter.ToUInt64(Guid.NewGuid().ToByteArray()) % 90_000_000_000UL +
                            10_000_000_000UL).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var command = fixture.DataSource.CreateCommand("""
            INSERT INTO exam.activation_devices
                (device_id, client_number, activation_date, activation_status,
                 protected_credential, credential_version, created_at, version)
            VALUES (@device, @client, CURRENT_DATE, 'active', 'synthetic-verifier', 1, clock_timestamp(), 1);

            INSERT INTO exam.candidate_roster_entries
                (exam_id, candidate_id, candidate_name, class_text, admission_no)
            VALUES (@exam, @candidate, '合成考生', '001', '0001');

            UPDATE exam.exam_entry_access
               SET package_ready = true, package_length = 1024,
                   encrypted_package_uri = '/api/v1/resources/synthetic'
             WHERE exam_id = @exam;

            INSERT INTO exam.student_sessions
                (session_id, device_id, candidate_id, candidate_name, class_text, admission_no,
                 exam_ids, candidate_ids_by_exam, authenticated_at)
            VALUES (@session, @device, @candidate, '合成考生', '001', '0001', @exams,
                    CAST(@candidate_map AS jsonb), clock_timestamp());

            INSERT INTO exam.entry_preparations
                (preparation_id, session_id, exam_id, candidate_id, device_id,
                 paper_version, instructions_version, prepared_at)
            VALUES (@preparation, @session, @exam, @candidate, @device, 1, 1, clock_timestamp());
            """);
        command.Parameters.AddWithValue("device", seed.DeviceId);
        command.Parameters.AddWithValue("client", clientNumber);
        command.Parameters.AddWithValue("exam", seed.ExamId);
        command.Parameters.AddWithValue("candidate", seed.CandidateId);
        command.Parameters.AddWithValue("session", seed.SessionId);
        command.Parameters.AddWithValue("exams", new[] { seed.ExamId });
        command.Parameters.AddWithValue("candidate_map", JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [seed.ExamId] = seed.CandidateId
        }));
        command.Parameters.AddWithValue("preparation", seed.PreparationId);
        await command.ExecuteNonQueryAsync();
        return seed;
    }

    private sealed record Seed(string ExamId, string CandidateId, string DeviceId, string SessionId, string PreparationId);
}
