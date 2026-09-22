using System.Text.Json;
using Exam.Application.Activation;
using Exam.Application.Participation;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Infrastructure.Postgres.Activation;
using Exam.Infrastructure.Postgres.Student;
using Exam.IntegrationTests.Postgres;

namespace Exam.IntegrationTests.Student;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresStudentDirectoryTests(PostgresDatabaseFixture fixture)
{
    [Fact]
    public async Task SelectorUsesCurrentSessionRosterAndConfiguredPublicMetadata()
    {
        var seed = await SeedAsync();
        var directory = new PostgresStudentDirectory(fixture.DataSource);
        var exams = await directory.ListExamsAsync(seed.SessionId, seed.DeviceId);

        Assert.Equal(2, exams.Count);
        var configured = Assert.Single(exams, exam => exam.ExamId == seed.ConfiguredExamId);
        Assert.Equal("独立合成考试", configured.Name);
        Assert.Equal("练习类别", configured.CategoryName);
        Assert.Equal(["合成科目甲", "合成科目乙"], configured.Subjects);
        Assert.Equal(ExamLifecycle.Scheduled, configured.Lifecycle);
        var unconfigured = Assert.Single(exams, exam => exam.ExamId == seed.UnconfiguredExamId);
        Assert.Equal(seed.UnconfiguredExamId, unconfigured.Name);
        Assert.Empty(unconfigured.Subjects);
        Assert.Null(unconfigured.CategoryName);

        await using var editRoster = fixture.DataSource.CreateCommand("""
            UPDATE exam.candidate_roster_entries SET candidate_name = '另一位考生'
             WHERE exam_id = @exam_id;
            """);
        editRoster.Parameters.AddWithValue("exam_id", seed.ConfiguredExamId);
        await editRoster.ExecuteNonQueryAsync();
        Assert.Equal(seed.UnconfiguredExamId,
            Assert.Single(await directory.ListExamsAsync(seed.SessionId, seed.DeviceId)).ExamId);
    }

    [Fact]
    public async Task SelectorRejectsAnotherDeviceAndRevokedSessionOwner()
    {
        var seed = await SeedAsync();
        var directory = new PostgresStudentDirectory(fixture.DataSource);
        var otherDevice = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            directory.ListExamsAsync(seed.SessionId, "another-device").AsTask());
        Assert.Equal(ParticipationErrorCodes.AuthorizationRevoked, otherDevice.Code);

        await using var revoke = fixture.DataSource.CreateCommand("""
            UPDATE exam.activation_devices SET activation_status = 'revoked' WHERE device_id = @device_id;
            """);
        revoke.Parameters.AddWithValue("device_id", seed.DeviceId);
        await revoke.ExecuteNonQueryAsync();
        var revoked = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            directory.ListExamsAsync(seed.SessionId, seed.DeviceId).AsTask());
        Assert.Equal(ParticipationErrorCodes.AuthorizationRevoked, revoked.Code);
    }

    private async Task<Seed> SeedAsync()
    {
        var prefix = $"student-{Guid.NewGuid():N}";
        var deviceId = $"{prefix}-device";
        var service = new ActivationService(
            new PostgresActivationPersistence(fixture.DataSource),
            new Pbkdf2DeviceCredentialProtector());
        var pending = await service.SubmitRequestAsync(new($"{prefix}-apply", deviceId, "synthetic-secret"));
        await service.ApproveAsync(new($"{prefix}-approve", pending.ActivationRequestId));
        var seed = new Seed($"{prefix}-session", deviceId, $"{prefix}-exam-1", $"{prefix}-exam-2");
        var hiddenExamId = $"{prefix}-exam-hidden";

        await using var command = fixture.DataSource.CreateCommand("""
            INSERT INTO exam.exam_entry_access
                (exam_id, scheduled_start, scheduled_end, lifecycle)
            SELECT unnest(@all_exams), clock_timestamp(), clock_timestamp() + interval '2 hours', 'scheduled';

            INSERT INTO exam.candidate_roster_entries
                (exam_id, candidate_id, candidate_name, class_text, admission_no)
            SELECT unnest(@all_exams), @candidate_id, '合成考生', '001', '0001';

            INSERT INTO exam.student_sessions
                (session_id, device_id, candidate_id, candidate_name, class_text, admission_no,
                 exam_ids, candidate_ids_by_exam, authenticated_at)
            VALUES (@session_id, @device_id, @candidate_id, '合成考生', '001', '0001',
                    @visible_exams, CAST(@candidate_map AS jsonb), clock_timestamp());

            INSERT INTO exam.student_exam_metadata
                (exam_id, display_name, category_name, subjects, version, updated_at, updated_by)
            VALUES (@configured_exam, '独立合成考试', '练习类别',
                    '["合成科目甲","合成科目乙"]'::jsonb, 1, clock_timestamp(), 'test-operator');
            """);
        command.Parameters.AddWithValue("all_exams", new[] { seed.ConfiguredExamId, seed.UnconfiguredExamId, hiddenExamId });
        command.Parameters.AddWithValue("visible_exams", new[] { seed.ConfiguredExamId, seed.UnconfiguredExamId });
        command.Parameters.AddWithValue("candidate_id", $"{prefix}-candidate");
        command.Parameters.AddWithValue("session_id", seed.SessionId);
        command.Parameters.AddWithValue("device_id", seed.DeviceId);
        command.Parameters.AddWithValue("configured_exam", seed.ConfiguredExamId);
        command.Parameters.AddWithValue("candidate_map", JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [seed.ConfiguredExamId] = $"{prefix}-candidate",
            [seed.UnconfiguredExamId] = $"{prefix}-candidate",
            [hiddenExamId] = $"{prefix}-candidate"
        }));
        await command.ExecuteNonQueryAsync();
        return seed;
    }

    private sealed record Seed(string SessionId, string DeviceId, string ConfiguredExamId, string UnconfiguredExamId);
}
