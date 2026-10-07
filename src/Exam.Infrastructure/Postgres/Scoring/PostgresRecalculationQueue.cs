using Exam.Domain.Scoring;
using Exam.Infrastructure.Postgres.HighAvailability;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Scoring;

/// <summary>缺失/旧成绩的幂等重算入队；不触碰 收卷 原答案、回执和普通提交去重逻辑。</summary>
public sealed class PostgresRecalculationQueue(NpgsqlDataSource dataSource, TimeProvider? timeProvider = null,
    PostgresActiveNodeWriteGuard? writeGuard = null) : IRecalculationQueue
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async ValueTask<bool> EnsureQueuedAsync(string examId, string submissionId, long targetVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        ArgumentException.ThrowIfNullOrWhiteSpace(submissionId);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetVersion, 1);
        const string sql = """
            INSERT INTO exam.grading_outbox AS task (submission_id, grading_version, created_at)
            SELECT submission.submission_id, @target_version, @now
              FROM exam.final_submissions AS submission
             WHERE submission.submission_id = @submission_id AND submission.exam_id = @exam_id
               AND NOT EXISTS (
                   SELECT 1 FROM exam.grading_results AS result
                    WHERE result.submission_id = submission.submission_id
                      AND result.exam_id = submission.exam_id AND result.candidate_id = submission.candidate_id
                      AND result.grading_version >= @target_version
               )
            ON CONFLICT (submission_id, grading_version) DO UPDATE
               SET completed_at = NULL, claimed_at = NULL, lease_expires_at = NULL, last_error = NULL
             WHERE task.completed_at IS NOT NULL;
            """;
        // 一个语句原子判断并入队/重开。保留 attempt_count，不能让领取代次倒退。
        // 正常的未完成/领取中任务冲突时不更新，避免调度器抢走消费者的租约。
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = writeGuard is null ? null : await writeGuard.BeginAsync(connection, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("exam_id", NpgsqlDbType.Text, examId);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Text, submissionId);
        command.Parameters.AddWithValue("target_version", NpgsqlDbType.Bigint, targetVersion);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, _timeProvider.GetUtcNow().ToUniversalTime());
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return changed;
    }
}
