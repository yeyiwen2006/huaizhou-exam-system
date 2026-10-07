using System.Text.Json;
using Exam.Application.Participation;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Importing;
using Exam.Contracts.Participation;
using Exam.Contracts.Versioning;
using Exam.Infrastructure.Postgres.Exams;
using Exam.Infrastructure.Postgres.Student;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.IntegrationTests.Postgres;

namespace Exam.IntegrationTests.Exams;

/// <summary>考务端考试目录：名称唯一、设置持久化、学生端显示开关与锁定。全部为合成数据。</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresExamCatalogTests(PostgresDatabaseFixture fixture)
{
    [Fact]
    public async Task Create_PersistsSettingsAndInstructions_HiddenByDefault_AndRetryIsIdempotent()
    {
        var catalog = Catalog();
        var name = Unique("合成模拟考试");
        var request = new CreateExamRequest(Guid.NewGuid().ToString("N"), name, Window(), Settings());
        var created = await catalog.CreateAsync(request, "operator-1");

        Assert.Equal(name, created.Summary.Name);
        Assert.False(created.Summary.VisibleToStudents);
        Assert.False(created.Summary.VisibilityLocked);
        Assert.Equal(1, created.Summary.SettingsVersion);
        Assert.Equal(0, created.Summary.PaperVersion);
        Assert.Equal(ExamLifecycle.Scheduled, created.Summary.Lifecycle);
        Assert.Equal(Settings(), created.Settings, new JsonEquality<ExamSettings>());
        Assert.Equal(["合成数学（满分6分）", "一、单项选择题：本题共2小题，每小题3分，共计6分。选对得3分，不选得0分，选错得-0.75分。"],
            created.InstructionLines);
        Assert.Contains(await catalog.ListAsync(), item => item.ExamId == created.Summary.ExamId && item.Name == name);
        Assert.Equal(0, (await catalog.ListAsync()).Single(item => item.ExamId == created.Summary.ExamId).PaperVersion);

        // 同一请求重试（例如回执丢失）得到同一场考试，不会新建第二场。
        var retried = await Catalog().CreateAsync(request, "operator-1");
        Assert.Equal(created.Summary.ExamId, retried.Summary.ExamId);
        Assert.Single(await catalog.ListAsync(), item => item.Name == name);

        var conflict = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            catalog.CreateAsync(request with { Name = Unique("另一个名称") }, "operator-1").AsTask());
        Assert.Equal(ParticipationErrorCodes.IdempotencyConflict, conflict.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("名称:含冒号")]
    public async Task Create_RejectsInvalidName(string name)
    {
        var error = await Assert.ThrowsAsync<ParticipationRuleException>(() => Catalog().CreateAsync(
            new CreateExamRequest(Guid.NewGuid().ToString("N"), name, Window(), Settings()), "operator-1").AsTask());
        Assert.Equal(ParticipationErrorCodes.InvalidRequest, error.Code);
    }

    [Fact]
    public async Task Names_AreUnique_IgnoringCaseAndSurroundingSpaces_ForCreateAndRename()
    {
        var catalog = Catalog();
        var name = Unique("Mock Exam");
        await catalog.CreateAsync(new(Guid.NewGuid().ToString("N"), name, Window(), Settings()), "operator-1");
        var duplicate = await Assert.ThrowsAsync<ParticipationRuleException>(() => catalog.CreateAsync(
            new(Guid.NewGuid().ToString("N"), $"  {name.ToLowerInvariant()} ", Window(), Settings()), "operator-1").AsTask());
        Assert.Equal(ExamCatalogErrorCodes.NameTaken, duplicate.Code);

        var other = await catalog.CreateAsync(new(Guid.NewGuid().ToString("N"), Unique("其他考试"), Window(), Settings()), "operator-1");
        var rename = await Assert.ThrowsAsync<ParticipationRuleException>(() => catalog.UpdateSettingsAsync(other.Summary.ExamId,
            new(Guid.NewGuid().ToString("N"), 1, name, Settings()), "operator-1").AsTask());
        Assert.Equal(ExamCatalogErrorCodes.NameTaken, rename.Code);
    }

    [Fact]
    public async Task UpdateSettings_UsesExpectedVersion_AndIsRejectedAfterTheExamStarts()
    {
        var catalog = Catalog();
        var created = await catalog.CreateAsync(new(Guid.NewGuid().ToString("N"), Unique("设置修改"), Window(), Settings()), "operator-1");
        var examId = created.Summary.ExamId;
        var renamed = Unique("设置修改（新名称）");
        var changed = Settings() with { Subjects = [Settings().Subjects[0] with { Name = "合成物理" }] };
        var updated = await catalog.UpdateSettingsAsync(examId, new(Guid.NewGuid().ToString("N"), 1, renamed, changed), "operator-2");
        Assert.Equal(2, updated.Summary.SettingsVersion);
        Assert.Equal(renamed, updated.Summary.Name);
        Assert.StartsWith("合成物理", updated.InstructionLines[0], StringComparison.Ordinal);

        var stale = await Assert.ThrowsAsync<ParticipationRuleException>(() => catalog.UpdateSettingsAsync(examId,
            new(Guid.NewGuid().ToString("N"), 1, renamed, Settings()), "operator-1").AsTask());
        Assert.Equal(ParticipationErrorCodes.VersionConflict, stale.Code);

        await ExecuteAsync("UPDATE exam.exam_entry_access SET lifecycle = 'running' WHERE exam_id = @exam;", examId);
        var locked = await Assert.ThrowsAsync<ParticipationRuleException>(() => catalog.UpdateSettingsAsync(examId,
            new(Guid.NewGuid().ToString("N"), 2, renamed, Settings()), "operator-1").AsTask());
        Assert.Equal(ExamCatalogErrorCodes.SettingsLocked, locked.Code);
    }

    [Fact]
    public async Task Visibility_ControlsStudentList_AndCannotBeTurnedOffUntilAllConfirmedAreCollected()
    {
        var catalog = Catalog();
        var name = Unique("显示开关");
        var created = await catalog.CreateAsync(new(Guid.NewGuid().ToString("N"), name, Window(), Settings()), "operator-1");
        var examId = created.Summary.ExamId;
        var seed = await SeedStudentAsync(examId);
        var directory = new PostgresStudentDirectory(fixture.DataSource);
        Assert.Empty(await directory.ListExamsAsync(seed.SessionId, seed.DeviceId));

        await catalog.SetVisibilityAsync(examId, new(Guid.NewGuid().ToString("N"), true), "operator-1");
        var shown = Assert.Single(await directory.ListExamsAsync(seed.SessionId, seed.DeviceId));
        Assert.Equal(name, shown.Name);
        Assert.Null(shown.CategoryName);
        Assert.Equal(["合成数学"], shown.Subjects);

        // 未确认参试时可以随时取消显示。
        await catalog.SetVisibilityAsync(examId, new(Guid.NewGuid().ToString("N"), false), "operator-1");
        Assert.Empty(await directory.ListExamsAsync(seed.SessionId, seed.DeviceId));
        await catalog.SetVisibilityAsync(examId, new(Guid.NewGuid().ToString("N"), true), "operator-1");

        // 考生确认参试后锁定：不能取消显示。
        await ExecuteAsync("""
            INSERT INTO exam.participations
                (participation_id, exam_id, candidate_id, entry_version, business_state,
                 authorized_device_id, authorization_version, version)
            VALUES (@participation, @exam, @candidate, 1, 'waiting', @device, 1, 1);
            """, examId, seed);
        var detail = await catalog.GetAsync(examId);
        Assert.True(detail.Summary.VisibilityLocked);
        Assert.Equal(1, detail.Summary.ActualParticipants);
        var refused = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            catalog.SetVisibilityAsync(examId, new(Guid.NewGuid().ToString("N"), false), "operator-1").AsTask());
        Assert.Equal(ExamCatalogErrorCodes.VisibilityLocked, refused.Code);

        // 即使数据层出现“不显示”，只要仍有未回收的实际参试考生，学生端也继续列出。
        await ExecuteAsync("UPDATE exam.exam_catalog SET visible_to_students = false WHERE exam_id = @exam;", examId);
        Assert.Single(await directory.ListExamsAsync(seed.SessionId, seed.DeviceId));
        await ExecuteAsync("UPDATE exam.exam_catalog SET visible_to_students = true WHERE exam_id = @exam;", examId);

        // 全部实际参试考生的答卷回收后解除锁定。
        await ExecuteAsync("""
            UPDATE exam.participations SET business_state = 'submitted', version = 2 WHERE participation_id = @participation;
            INSERT INTO exam.final_submissions
                (submission_id, exam_id, candidate_id, participation_id, device_id, authorization_version,
                 paper_version, frozen_answers, received_at, receipt_id, grading_version)
            VALUES (@participation || '-submission', @exam, @candidate, @participation, @device, 1, 1,
                    '{}'::jsonb, clock_timestamp(), @participation || '-receipt', 1);
            """, examId, seed);
        var unlocked = await catalog.GetAsync(examId);
        Assert.False(unlocked.Summary.VisibilityLocked);
        Assert.Equal(1, unlocked.Summary.CollectedSubmissions);
        var hidden = await catalog.SetVisibilityAsync(examId, new(Guid.NewGuid().ToString("N"), false), "operator-1");
        Assert.False(hidden.VisibleToStudents);
        Assert.Empty(await directory.ListExamsAsync(seed.SessionId, seed.DeviceId));
    }

    private PostgresExamCatalog Catalog() =>
        new(fixture.DataSource, new PostgresExamVersioningService(fixture.DataSource), TimeProvider.System);

    private static string Unique(string name) => $"{name}-{Guid.NewGuid():N}"[..Math.Min(name.Length + 9, 120)];

    private static ScheduleWindow Window() => new(DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(3));

    private static ExamSettings Settings() => new(
    [
        new ExamSubjectSettings("math", "合成数学", "6",
        [
            new ExamGroupSettings("math-1", "一、单项选择题", "single", 1, 2, "6", false,
                new ManifestOneCorrect("3", "0", "-0.75"), null),
        ]),
    ]);

    private async Task<Seed> SeedStudentAsync(string examId)
    {
        var prefix = $"catalog-{Guid.NewGuid():N}";
        var seed = new Seed($"{prefix}-candidate", $"{prefix}-device", $"{prefix}-session", $"{prefix}-participation");
        var clientNumber = (BitConverter.ToUInt64(Guid.NewGuid().ToByteArray()) % 90_000_000_000UL + 10_000_000_000UL)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var command = fixture.DataSource.CreateCommand("""
            INSERT INTO exam.activation_devices
                (device_id, client_number, activation_date, activation_status,
                 protected_credential, credential_version, created_at, version)
            VALUES (@device, @client, CURRENT_DATE, 'active', 'synthetic-verifier', 1, clock_timestamp(), 1);
            INSERT INTO exam.candidate_roster_entries (exam_id, candidate_id, candidate_name, class_text, admission_no)
            VALUES (@exam, @candidate, '合成考生', '001', '0001');
            INSERT INTO exam.student_sessions
                (session_id, device_id, candidate_id, candidate_name, class_text, admission_no,
                 exam_ids, candidate_ids_by_exam, authenticated_at)
            VALUES (@session, @device, @candidate, '合成考生', '001', '0001', @exams, CAST(@map AS jsonb), clock_timestamp());
            """);
        command.Parameters.AddWithValue("device", seed.DeviceId);
        command.Parameters.AddWithValue("client", clientNumber);
        command.Parameters.AddWithValue("exam", examId);
        command.Parameters.AddWithValue("candidate", seed.CandidateId);
        command.Parameters.AddWithValue("session", seed.SessionId);
        command.Parameters.AddWithValue("exams", new[] { examId });
        command.Parameters.AddWithValue("map", JsonSerializer.Serialize(new Dictionary<string, string> { [examId] = seed.CandidateId }));
        await command.ExecuteNonQueryAsync();
        return seed;
    }

    private async Task ExecuteAsync(string sql, string examId, Seed? seed = null)
    {
        await using var command = fixture.DataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("exam", examId);
        if (seed is not null)
        {
            command.Parameters.AddWithValue("participation", seed.ParticipationId);
            command.Parameters.AddWithValue("candidate", seed.CandidateId);
            command.Parameters.AddWithValue("device", seed.DeviceId);
        }
        await command.ExecuteNonQueryAsync();
    }

    private sealed record Seed(string CandidateId, string DeviceId, string SessionId, string ParticipationId);

    private sealed class JsonEquality<T> : IEqualityComparer<T>
    {
        public bool Equals(T? x, T? y) => JsonSerializer.Serialize(x) == JsonSerializer.Serialize(y);
        public int GetHashCode(T obj) => JsonSerializer.Serialize(obj).GetHashCode(StringComparison.Ordinal);
    }
}
