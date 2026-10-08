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

/// <summary>考务端考试目录：名称唯一、设置持久化、学生端显示开关与考试期间锁定。全部为合成数据。</summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed partial class PostgresExamCatalogTests(PostgresDatabaseFixture fixture)
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
    public async Task Visibility_OnlyRunningExamBlocksHiding_EndedUncollectedExamCanBeHidden()
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

        // 开考前即使已有确认参加考试的学生，也允许取消显示。
        await ExecuteAsync("""
            INSERT INTO exam.participations
                (participation_id, exam_id, candidate_id, entry_version, business_state,
                 authorized_device_id, authorization_version, version)
            VALUES (@participation, @exam, @candidate, 1, 'waiting', @device, 1, 1);
            """, examId, seed);
        Assert.False((await catalog.GetAsync(examId)).Summary.VisibilityLocked);
        await catalog.SetVisibilityAsync(examId, new(Guid.NewGuid().ToString("N"), false), "operator-1");
        Assert.Empty(await directory.ListExamsAsync(seed.SessionId, seed.DeviceId));
        await catalog.SetVisibilityAsync(examId, new(Guid.NewGuid().ToString("N"), true), "operator-1");
        await ExecuteAsync("UPDATE exam.exam_entry_access SET lifecycle='running' WHERE exam_id=@exam", examId);
        Assert.True((await catalog.GetAsync(examId)).Summary.VisibilityLocked);
        var refused = await Assert.ThrowsAsync<ParticipationRuleException>(() =>
            catalog.SetVisibilityAsync(examId, new(Guid.NewGuid().ToString("N"), false), "operator-1").AsTask());
        Assert.Equal(ExamCatalogErrorCodes.VisibilityLocked, refused.Code);
        await ExecuteAsync("UPDATE exam.exam_entry_access SET lifecycle='ended' WHERE exam_id=@exam", examId);
        var ended = await catalog.GetAsync(examId);
        Assert.False(ended.Summary.VisibilityLocked);
        Assert.Equal(1, ended.Summary.ActualParticipants);
        Assert.Equal(0, ended.Summary.CollectedSubmissions);
        var hidden = await catalog.SetVisibilityAsync(examId, new(Guid.NewGuid().ToString("N"), false), "operator-1");
        Assert.False(hidden.VisibleToStudents);
        Assert.Empty(await directory.ListExamsAsync(seed.SessionId, seed.DeviceId));
    }

    [Theory]
    [InlineData("scheduled")]
    [InlineData("running")]
    [InlineData("ended")]
    public async Task RenamePreservesPublishedPaperIdentityBytesAndInstructions(string lifecycle)
    {
        var catalog = Catalog();
        var created = await catalog.CreateAsync(new(Guid.NewGuid().ToString("N"), Unique("更名保留试卷"), Window(), Settings()), "operator");
        var examId = created.Summary.ExamId;
        var seed = await SeedStudentAsync(examId);
        var resourceId = "paper-" + Guid.NewGuid().ToString("N");
        byte[] publishedBytes = [1, 2, 3, 4];
        await using (var resource = fixture.DataSource.CreateCommand("""
            INSERT INTO exam.versioned_resources VALUES ($1,$2,'paper-package',1,$3,'{}'::jsonb,now(),'synthetic-operator');
            """))
        {
            resource.Parameters.AddWithValue(resourceId);
            resource.Parameters.AddWithValue(examId);
            resource.Parameters.AddWithValue(publishedBytes);
            await resource.ExecuteNonQueryAsync();
        }
        var versioning = new PostgresExamVersioningService(fixture.DataSource);
        var paper = versioning.PublishPaper(new(examId, 0, resourceId,
            new PaperStructure([new("q1",1,"math","math-1","single",["A","B"]),new("q2",2,"math","math-1","single",["A","B"])]),
            [], new(true,true,true,true), "operator", "合成改名检查", DateTimeOffset.UtcNow, "publish-"+resourceId));
        Assert.True(paper.Succeeded, paper.Error);
        await using (var state = fixture.DataSource.CreateCommand("""
            UPDATE exam.exam_entry_access SET lifecycle=@lifecycle WHERE exam_id=@exam;
            UPDATE exam.student_exam_instructions SET lines='["正式发布的说明，改名后仍保留。"]'::jsonb WHERE exam_id=@exam;
            """))
        {
            state.Parameters.AddWithValue("lifecycle",lifecycle);state.Parameters.AddWithValue("exam",examId);
            await state.ExecuteNonQueryAsync();
        }
        var before = await catalog.GetAsync(examId);
        var renamed = await catalog.UpdateSettingsAsync(examId,
            new(Guid.NewGuid().ToString("N"), before.Summary.SettingsVersion, Unique("新显示名称"), before.Settings), "operator");
        Assert.Equal(examId, renamed.Summary.ExamId);
        Assert.Equal(1, renamed.Summary.PaperVersion);
        Assert.Equal(before.ScheduleVersion, renamed.ScheduleVersion);
        Assert.Equal(before.InstructionLines, renamed.InstructionLines);
        Assert.Equal(JsonSerializer.Serialize(paper.Value), JsonSerializer.Serialize(versioning.GetExam(examId).Value));
        await using (var resource = fixture.DataSource.CreateCommand("""
            SELECT r.resource_id,r.encrypted_content,a.unlock_reference,a.instructions_version
              FROM exam.versioned_resources r JOIN exam.exam_entry_access a USING(exam_id)
             WHERE r.exam_id=$1;
            """))
        {
            resource.Parameters.AddWithValue(examId);
            await using var rows=await resource.ExecuteReaderAsync();
            Assert.True(await rows.ReadAsync());
            Assert.Equal(resourceId,rows.GetString(0));
            Assert.Equal(publishedBytes,rows.GetFieldValue<byte[]>(1));
            Assert.Equal("resource-envelope:"+resourceId,rows.GetString(2));
            Assert.Equal(1L,rows.GetInt64(3));
        }
        await catalog.SetVisibilityAsync(examId,new(Guid.NewGuid().ToString("N"),true),"operator");
        Assert.Equal(renamed.Summary.Name,Assert.Single(await new PostgresStudentDirectory(fixture.DataSource)
            .ListExamsAsync(seed.SessionId,seed.DeviceId)).Name);
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
