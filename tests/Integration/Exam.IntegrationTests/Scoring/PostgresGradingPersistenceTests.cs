using Exam.Domain.Scoring;
using Exam.Infrastructure.Postgres.Scoring;
using Exam.IntegrationTests.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Exam.IntegrationTests.Scoring;

/// <summary>
/// 计分持久化的 PostgreSQL 集成用例：迁移建表、结果按答卷去重、任务租约与失败保留、
/// 以及从 A05 答卷与 A06 发布事件读取计分输入。数据全部是合成的。
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed partial class PostgresGradingPersistenceTests(PostgresDatabaseFixture fixture)
{
    private const string ExamId = "pg-grading-exam";
    private const string CandidateId = "pg-grading-candidate";
    private const string SubmissionId = "pg-grading-submission";
    private const string ParticipationId = "pg-grading-participation";
    private const string DeviceId = "pg-grading-device";

    private const string ManifestJson = """
        {
          "schemaVersion": 1,
          "examId": "pg-grading-exam",
          "examName": "PG合成考试",
          "subjects": [{ "subjectId": "subject-1", "name": "科目甲", "maxScore": "3" }],
          "groups": [
            {
              "groupId": "group-1",
              "subjectId": "subject-1",
              "name": "一、单项选择题",
              "type": "single",
              "order": 1,
              "questionCount": 1,
              "scoring": { "oneCorrect": { "correct": "3", "blank": "0", "wrong": "-0.75" } }
            }
          ],
          "questions": [
            {
              "questionId": "q-001",
              "questionNo": 1,
              "subjectId": "subject-1",
              "groupId": "group-1",
              "source": "questions/PG合成考试_1.tex",
              "options": ["A", "B", "C", "D"]
            }
          ],
          "resourceRoots": []
        }
        """;

    private const string AnswerKeyJson = """
        {
          "schemaVersion": 1,
          "examId": "pg-grading-exam",
          "answers": [{ "questionId": "q-001", "questionNo": 1, "correctOptionIds": ["A"] }]
        }
        """;

    private const string FrozenAnswersJson = """{"q-001":{"kind":"selected","optionIds":["A"]}}""";

    [Fact]
    public async Task ResultStore_RoundTripsAndKeepsOneRowPerSubmission()
    {
        await SeedAsync();
        var store = new PostgresGradingResultStore(fixture.DataSource);
        var gradedAt = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

        await store.SaveAsync(Result(gradingVersion: 1, total: "3", gradedAt));
        await store.SaveAsync(Result(gradingVersion: 2, total: "3", gradedAt.AddMinutes(1)));

        // 换版本重算更新同一行：旧版本不再可见，成绩不会被重复累计。
        var stored = await store.FindAsync(SubmissionId);
        Assert.NotNull(stored);
        Assert.Equal(2, stored!.GradingVersion);
        Assert.Equal("3", stored.Grade.ExactTotalScore);
        Assert.Single(stored.Grade.Questions);
        Assert.Single(stored.Grade.Subjects);
        Assert.Empty(await store.ListAsync(ExamId, 1));
        Assert.Single(await store.ListAsync(ExamId, 2));
    }

    [Fact]
    public async Task TaskSource_LeaseClaimCompleteAndFailureKeepsTaskRetryable()
    {
        await SeedAsync();
        var source = new PostgresGradingTaskSource(fixture.DataSource);
        var now = new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var lease = TimeSpan.FromMinutes(1);

        var claimed = await source.ClaimAsync(10, now, lease);
        var task = Assert.Single(claimed);
        Assert.Equal(SubmissionId, task.SubmissionId);
        Assert.Equal(1, task.AttemptCount);

        // 租约未过期：不能被第二次领取。
        Assert.Empty(await source.ClaimAsync(10, now.AddSeconds(30), lease));

        // 失败只记录错误、保留任务；租约释放后可立即重试。
        Assert.True(await source.FailAsync(task, "规则尚未发布"));
        var retried = Assert.Single(await source.ClaimAsync(10, now.AddSeconds(1), lease));
        Assert.Equal(2, retried.AttemptCount);
        Assert.Equal("规则尚未发布", await ReadLastErrorAsync());

        // 租约过期后可再次领取（消费者崩溃场景）。
        Assert.Empty(await source.ClaimAsync(10, now.AddSeconds(2), lease));
        var reclaimed = Assert.Single(await source.ClaimAsync(10, now.AddMinutes(5), lease));

        // 完成之后不再出现，且完成时间只记一次。
        Assert.True(await source.CompleteAsync(reclaimed));
        Assert.Empty(await source.ClaimAsync(10, now.AddMinutes(30), lease));
        Assert.NotNull(await ReadCompletedAtAsync());
        Assert.Null(await ReadLastErrorAsync());
    }

    [Fact]
    public async Task Inputs_LoadFrozenSubmissionAndPublishedRulesSkippingLegacyEvent()
    {
        await SeedAsync();
        var inputs = new PostgresGradingInputs(fixture.DataSource);

        var submission = await inputs.LoadSubmissionAsync(SubmissionId);
        Assert.NotNull(submission);
        Assert.Equal(ExamId, submission!.ExamId);
        Assert.Equal(CandidateId, submission.CandidateId);
        Assert.Equal("submission-receipt-1", submission.Receipt.ReceiptId);
        var answer = submission.FrozenAnswers["q-001"];
        Assert.Single(answer.OptionIds);
        Assert.Equal("A", answer.OptionIds[0]);

        // 较早的兼容事件没有源数据，必须跳过，取最近一次带源数据的发布。
        var rules = await inputs.LoadRulesAsync(ExamId);
        Assert.NotNull(rules);
        Assert.Equal(3, rules!.GradingVersion);
        Assert.Equal(ExamId, rules.Rules.ExamId);
        Assert.Single(rules.Rules.Questions);

        Assert.Null(await inputs.LoadSubmissionAsync("pg-grading-missing"));
        Assert.Null(await inputs.LoadRulesAsync("pg-grading-other-exam"));
    }

    [Fact]
    public async Task Coordinator_ConsumesOutbox_GradesPersistsAndCompletesEndToEnd()
    {
        // 端到端：A05 的 outbox → 协调器领取 → 读答卷与已发布规则 → 计分 → 落库 → 结项。
        await SeedAsync();
        var coordinator = new Exam.Application.Scoring.GradingCoordinator(
            new PostgresGradingTaskSource(fixture.DataSource),
            new PostgresGradingResultStore(fixture.DataSource),
            new PostgresGradingInputs(fixture.DataSource),
            batchSize: 8,
            lease: TimeSpan.FromMinutes(1),
            timeProvider: new FixedTimeProvider(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero)));

        var report = await coordinator.RunOnceAsync();

        Assert.True(report.Graded == 1, $"应计分 1 份，实际 {report.Graded}；{string.Join(" | ", report.FailureMessages)}");

        var results = new PostgresGradingResultStore(fixture.DataSource);
        var stored = await results.FindAsync(SubmissionId);
        Assert.NotNull(stored);
        Assert.Equal("3", stored!.Grade.ExactTotalScore);
        // 判分采用最新有效规则：任务记录的是版本 1，已发布版本是 3，结果按 3 标记。
        Assert.Equal(3, stored.GradingVersion);

        // 结项后 outbox 不再有待办；重复运行不产生第二行结果。
        await using var pending = fixture.DataSource.CreateCommand(
            "SELECT count(*) FROM exam.grading_outbox WHERE completed_at IS NULL;");
        Assert.Equal(0L, (long)(await pending.ExecuteScalarAsync())!);

        await coordinator.RunOnceAsync();
        Assert.Single(await results.ListAsync(ExamId, 3));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => now;
    }

    private StoredGradingResult Result(long gradingVersion, string total, DateTimeOffset gradedAt) =>
        new(
            new Exam.Contracts.Grading.SubmissionGrade(
                SubmissionId,
                ExamId,
                CandidateId,
                gradingVersion,
                [new Exam.Contracts.Grading.QuestionGrade("q-001", 1, "subject-1",
                    Exam.Contracts.Grading.AnswerOutcome.Correct, total)],
                [new Exam.Contracts.Grading.SubjectGrade("subject-1", total, total, false)],
                total),
            gradedAt);

    private async Task<string?> ReadLastErrorAsync()
    {
        await using var command = fixture.DataSource.CreateCommand(
            "SELECT last_error FROM exam.grading_outbox WHERE submission_id = @submission_id;");
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Text, SubmissionId);
        return await command.ExecuteScalarAsync() as string;
    }

    private async Task<DateTimeOffset?> ReadCompletedAtAsync()
    {
        await using var command = fixture.DataSource.CreateCommand(
            "SELECT completed_at FROM exam.grading_outbox WHERE submission_id = @submission_id;");
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Text, SubmissionId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        // Npgsql 把 timestamptz 映射为 DateTime，用 GetFieldValue 显式取 DateTimeOffset。
        return reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
    }

    /// <summary>合成数据：名册、设备、参试、最终答卷、版本流与一次计分规则发布、一条待计分任务。</summary>
    private async Task SeedAsync()
    {
        await using var command = fixture.DataSource.CreateCommand(
            """
            TRUNCATE TABLE
                exam.grading_results,
                exam.grading_outbox,
                exam.version_events,
                exam.version_state,
                exam.version_streams,
                exam.final_submissions,
                exam.participations,
                exam.candidate_roster_entries,
                exam.activation_devices
            CASCADE;

            INSERT INTO exam.candidate_roster_entries
                (exam_id, candidate_id, candidate_name, class_text, admission_no)
            VALUES (@exam_id, @candidate_id, '合成考生', '001', '0001');

            INSERT INTO exam.activation_devices
                (device_id, client_number, activation_date, activation_status,
                 protected_credential, credential_version, created_at, version)
            VALUES (@device_id, '13800000001', DATE '2026-09-25', 'active',
                    'protected', 1, TIMESTAMPTZ '2026-09-25 01:00:00+00', 1);

            INSERT INTO exam.participations
                (participation_id, exam_id, candidate_id, entry_version, business_state,
                 authorized_device_id, authorization_version, version,
                 current_paper_version, current_grading_version)
            VALUES (@participation_id, @exam_id, @candidate_id, 1, 'submitted',
                    @device_id, 1, 1, 1, 1);

            INSERT INTO exam.final_submissions
                (submission_id, exam_id, candidate_id, participation_id, device_id,
                 authorization_version, paper_version, frozen_answers, received_at,
                 receipt_id, grading_version)
            VALUES (@submission_id, @exam_id, @candidate_id, @participation_id, @device_id,
                    1, 1, @frozen_answers, TIMESTAMPTZ '2026-09-25 02:00:00+00',
                    'submission-receipt-1', 1);

            INSERT INTO exam.grading_outbox (submission_id, grading_version, created_at)
            VALUES (@submission_id, 1, TIMESTAMPTZ '2026-09-25 02:00:01+00');

            INSERT INTO exam.version_streams (exam_id, stream_revision, created_at, updated_at)
            VALUES (@exam_id, 3, TIMESTAMPTZ '2026-09-25 01:00:00+00', TIMESTAMPTZ '2026-09-25 03:00:00+00');

            INSERT INTO exam.version_state
                (exam_id, stream_revision, schedule_version, paper_version, grading_version,
                 starts_at, ends_at, lifecycle, snapshot, updated_at)
            VALUES (@exam_id, 3, 1, 1, 3,
                    TIMESTAMPTZ '2026-09-25 01:00:00+00', TIMESTAMPTZ '2026-09-25 05:00:00+00',
                    'Ended', '{}'::jsonb, TIMESTAMPTZ '2026-09-25 03:00:00+00');

            -- 较早的兼容发布没有源数据，读取时必须跳过它。
            INSERT INTO exam.version_events
                (exam_id, stream_revision, operation_type, payload, recorded_at)
            VALUES (@exam_id, 1, 'grading.publish',
                    '{"expectedVersion":0,"ruleSetId":"legacy"}'::jsonb,
                    TIMESTAMPTZ '2026-09-25 02:30:00+00');

            INSERT INTO exam.version_events
                (exam_id, stream_revision, operation_type, payload, recorded_at)
            VALUES (@exam_id, 2, 'grading.publish',
                    jsonb_build_object('examId', @exam_id, 'expectedVersion', 2,
                                       'ruleSetId', 'pg-rules',
                                       'manifestJson', @manifest_json,
                                       'answerKeyJson', @answer_key_json),
                    TIMESTAMPTZ '2026-09-25 03:00:00+00');
            """);
        command.Parameters.AddWithValue("exam_id", NpgsqlDbType.Text, ExamId);
        command.Parameters.AddWithValue("candidate_id", NpgsqlDbType.Text, CandidateId);
        command.Parameters.AddWithValue("device_id", NpgsqlDbType.Text, DeviceId);
        command.Parameters.AddWithValue("participation_id", NpgsqlDbType.Text, ParticipationId);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Text, SubmissionId);
        command.Parameters.AddWithValue("frozen_answers", NpgsqlDbType.Jsonb, FrozenAnswersJson);
        command.Parameters.AddWithValue("manifest_json", NpgsqlDbType.Text, ManifestJson);
        command.Parameters.AddWithValue("answer_key_json", NpgsqlDbType.Text, AnswerKeyJson);
        await command.ExecuteNonQueryAsync();
    }
}
