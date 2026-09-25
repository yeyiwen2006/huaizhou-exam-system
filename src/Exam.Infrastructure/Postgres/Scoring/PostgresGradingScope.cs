using Exam.Contracts.Grading;
using Exam.Domain.Scoring;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Scoring;

/// <summary>
/// 范围来自 A05 全部最终答卷，实际计分版本来自 D04 成绩表，而不是 outbox 入队目标。
/// 旧任务可以按更新规则产出结果；反之，任务已完成但派生成绩缺失时仍需恢复重算。
/// 不读取答案；当前有效成绩已覆盖的同版本/旧版本错误不会永久阻断出表。
/// </summary>
public sealed class PostgresGradingScope(NpgsqlDataSource dataSource) : IGradingScope
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<IReadOnlyList<SubmissionGradingState>> ListStatesAsync(string examId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        const string sql = """
            SELECT submission.submission_id, result.grading_version,
                   EXISTS (
                       SELECT 1 FROM exam.grading_outbox AS task
                        WHERE task.submission_id = submission.submission_id
                          AND task.completed_at IS NULL AND task.last_error IS NOT NULL
                          AND task.grading_version > COALESCE(result.grading_version, 0)
                   ) AS grading_failed
              FROM exam.final_submissions AS submission
              LEFT JOIN exam.grading_results AS result
                ON result.submission_id = submission.submission_id
               AND result.exam_id = submission.exam_id AND result.candidate_id = submission.candidate_id
             WHERE submission.exam_id = @exam_id
             ORDER BY submission.submission_id;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("exam_id", NpgsqlDbType.Text, examId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var states = new List<SubmissionGradingState>();
        while (await reader.ReadAsync(cancellationToken))
            states.Add(new(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetBoolean(2)));
        return Array.AsReadOnly(states.ToArray());
    }
}
