using Exam.Application.Scoring;
using Exam.Infrastructure.Postgres.Scoring;
using Exam.Infrastructure.Postgres.Submissions;
using Exam.IntegrationTests.Postgres;
using Npgsql;

namespace Exam.IntegrationTests.Scoring;

/// <summary>
/// 重算范围的 PostgreSQL 用例：从 A05 的最终答卷与计分任务台账读出每份答卷的计分状态，
/// 并让重算调度器跑在真实范围上。数据全部是合成的。
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed partial class PostgresGradingScopeTests(PostgresDatabaseFixture fixture)
{
    private const string ExamId = "pg-scope-exam";
    private const string OtherExamId = "pg-scope-other-exam";

    [Fact]
    public async Task ListStatesAsync_ReportsGradedVersionFailuresAndOtherExamsSeparately()
    {
        await SeedAsync();
        var scope = new PostgresGradingScope(fixture.DataSource);

        var states = await scope.ListStatesAsync(ExamId);

        Assert.Equal(4, states.Count);
        Assert.Equal(2, states.Single(item => item.SubmissionId == "pg-scope-current").GradedVersion);
        Assert.Null(states.Single(item => item.SubmissionId == "pg-scope-never").GradedVersion);
        Assert.True(states.Single(item => item.SubmissionId == "pg-scope-failed").GradingFailed);
        Assert.DoesNotContain(states, item => item.SubmissionId.StartsWith("pg-scope-other", StringComparison.Ordinal));

        // 旧版本的失败记录已被更高版本的成功结果取代：不再算失败，也不应阻断发布。
        var superseded = states.Single(item => item.SubmissionId == "pg-scope-superseded");
        Assert.Equal(3, superseded.GradedVersion);
        Assert.False(superseded.GradingFailed);

        // 另一场考试的范围互不混淆。
        Assert.Single(await scope.ListStatesAsync(OtherExamId));
    }

    [Fact]
    public async Task Scheduler_OnRealScope_EnqueuesStaleWorkAndIsNotReadyUntilAligned()
    {
        await SeedAsync();
        var scope = new PostgresGradingScope(fixture.DataSource);
        var scheduler = new RecalculationScheduler(
            scope,
            new PostgresRecalculationQueue(fixture.DataSource));

        var run = await scheduler.RunOnceAsync(ExamId, 3);

        // 已按版本 2 计分的按新版本重排；从未计分的按目标版本计分；失败的重试；
        // 已对齐目标版本（版本 3）的不动。
        Assert.True(run.Enqueued == 3,
            $"应入队 3 条，实际 {run.Enqueued}；pending=" +
            string.Join(", ", run.Plan.Pending.Select(item => $"{item.SubmissionId}:{item.Reason}")));
        Assert.Equal(4, run.SubmissionCount);
        Assert.False(run.Readiness.RecalculationReady);
        Assert.DoesNotContain(run.Plan.Pending, item => item.SubmissionId == "pg-scope-superseded");

        // seed 里已有一条版本 3 的完成记录（旧失败被取代的那份），因此只数**新入队的未完成任务**。
        await using (var command = fixture.DataSource.CreateCommand(
            "SELECT count(*) FROM exam.grading_outbox WHERE grading_version = 3 AND completed_at IS NULL;"))
        {
            Assert.Equal(3L, (long)(await command.ExecuteScalarAsync())!);
        }

        // 重复调度不会产生重复任务（按答卷与规则版本去重）。
        var second = await scheduler.RunOnceAsync(ExamId, 3);
        Assert.Equal(0, second.Enqueued);
    }

    /// <summary>合成数据：四份本场答卷（已按版本 2 计分 / 从未计分 / 计分失败）与一份他场答卷。</summary>
    private async Task SeedAsync()
    {
        await using var command = fixture.DataSource.CreateCommand(
            """
            TRUNCATE TABLE
                exam.grading_outbox,
                exam.final_submissions,
                exam.participations,
                exam.candidate_roster_entries,
                exam.activation_devices
            CASCADE;

            INSERT INTO exam.activation_devices
                (device_id, client_number, activation_date, activation_status,
                 protected_credential, credential_version, created_at, version)
            VALUES ('pg-scope-device', '13800000002', DATE '2026-09-25', 'active',
                    'protected', 1, TIMESTAMPTZ '2026-09-25 01:00:00+00', 1);

            INSERT INTO exam.candidate_roster_entries
                (exam_id, candidate_id, candidate_name, class_text, admission_no)
            VALUES
                ('pg-scope-exam', 'pg-candidate-current', '合成考生甲', '001', '0001'),
                ('pg-scope-exam', 'pg-candidate-never', '合成考生乙', '001', '0002'),
                ('pg-scope-exam', 'pg-candidate-failed', '合成考生丙', '001', '0003'),
                ('pg-scope-exam', 'pg-candidate-superseded', '合成考生戊', '001', '0005'),
                ('pg-scope-other-exam', 'pg-candidate-other', '合成考生丁', '002', '0004');

            INSERT INTO exam.participations
                (participation_id, exam_id, candidate_id, entry_version, business_state,
                 authorized_device_id, authorization_version, version)
            VALUES
                ('pg-p-current', 'pg-scope-exam', 'pg-candidate-current', 1, 'submitted', 'pg-scope-device', 1, 1),
                ('pg-p-never', 'pg-scope-exam', 'pg-candidate-never', 1, 'submitted', 'pg-scope-device', 1, 1),
                ('pg-p-failed', 'pg-scope-exam', 'pg-candidate-failed', 1, 'submitted', 'pg-scope-device', 1, 1),
                ('pg-p-superseded', 'pg-scope-exam', 'pg-candidate-superseded', 1, 'submitted', 'pg-scope-device', 1, 1),
                ('pg-p-other', 'pg-scope-other-exam', 'pg-candidate-other', 1, 'submitted', 'pg-scope-device', 1, 1);

            INSERT INTO exam.final_submissions
                (submission_id, exam_id, candidate_id, participation_id, device_id,
                 authorization_version, paper_version, frozen_answers, received_at,
                 receipt_id, grading_version)
            VALUES
                ('pg-scope-current', 'pg-scope-exam', 'pg-candidate-current', 'pg-p-current',
                 'pg-scope-device', 1, 1, '{}'::jsonb, TIMESTAMPTZ '2026-09-25 02:00:00+00',
                 'pg-scope-receipt-current', 1),
                ('pg-scope-never', 'pg-scope-exam', 'pg-candidate-never', 'pg-p-never',
                 'pg-scope-device', 1, 1, '{}'::jsonb, TIMESTAMPTZ '2026-09-25 02:00:01+00',
                 'pg-scope-receipt-never', 1),
                ('pg-scope-failed', 'pg-scope-exam', 'pg-candidate-failed', 'pg-p-failed',
                 'pg-scope-device', 1, 1, '{}'::jsonb, TIMESTAMPTZ '2026-09-25 02:00:02+00',
                 'pg-scope-receipt-failed', 1),
                ('pg-scope-superseded', 'pg-scope-exam', 'pg-candidate-superseded', 'pg-p-superseded',
                 'pg-scope-device', 1, 1, '{}'::jsonb, TIMESTAMPTZ '2026-09-25 02:00:04+00',
                 'pg-scope-receipt-superseded', 1),
                ('pg-scope-other', 'pg-scope-other-exam', 'pg-candidate-other', 'pg-p-other',
                 'pg-scope-device', 1, 1, '{}'::jsonb, TIMESTAMPTZ '2026-09-25 02:00:03+00',
                 'pg-scope-receipt-other', 1);

            -- 已按版本 2 计分完成。
            INSERT INTO exam.grading_outbox (submission_id, grading_version, created_at, completed_at)
            VALUES ('pg-scope-current', 2, TIMESTAMPTZ '2026-09-25 02:30:00+00',
                    TIMESTAMPTZ '2026-09-25 02:30:05+00');

            -- 计分失败：未完成且留有错误，必须计入阻断原因。
            INSERT INTO exam.grading_outbox (submission_id, grading_version, created_at, last_error)
            VALUES ('pg-scope-failed', 1, TIMESTAMPTZ '2026-09-25 02:31:00+00', '规则尚未发布');

            -- 旧版本失败过一次，之后按更高版本成功计分：失败记录已被取代，不应再阻断发布。
            INSERT INTO exam.grading_outbox (submission_id, grading_version, created_at, last_error)
            VALUES ('pg-scope-superseded', 1, TIMESTAMPTZ '2026-09-25 02:32:00+00', '规则尚未发布');
            INSERT INTO exam.grading_outbox (submission_id, grading_version, created_at, completed_at)
            VALUES ('pg-scope-superseded', 3, TIMESTAMPTZ '2026-09-25 02:33:00+00',
                    TIMESTAMPTZ '2026-09-25 02:33:05+00');
            """);
        await command.ExecuteNonQueryAsync();
        // 完成标记不是成绩：范围判定必须有 D04 的真实持久化结果。
        await SaveScopeGradeAsync("pg-scope-current", "pg-candidate-current", 2);
        await SaveScopeGradeAsync("pg-scope-superseded", "pg-candidate-superseded", 3);
    }
}
