using Exam.Contracts.Grading;
using Exam.Domain.Scoring;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Scoring;

/// <summary>
/// 重算范围的 PostgreSQL 读取：A05 的最终答卷 + 计分任务台账。
///
/// 「某答卷已按哪个规则版本计分」取自 A05 的 `exam.grading_outbox`：一条**已完成**的
/// （答卷，规则版本）任务就代表该答卷已按该版本计分落库。这依赖计分协调器的不变量——
/// **先落库再结项**（见 GradingCoordinator）：结项只发生在结果持久化之后，
/// 因此「已完成」可以安全地当作「已产出该版本的成绩」。
///
/// 失败判定只认**最近一次尝试**：只有当存在未完成且有错误的任务、且它的版本**高于**该答卷
/// 已成功完成的最高版本时，才算计分失败。旧版本留下的失败记录一旦被更高版本的成功结果取代，
/// 就不应再阻断发布 —— 否则先失败一次就会永久卡住整场考试的出表。
/// </summary>
public sealed class PostgresGradingScope : IGradingScope
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresGradingScope(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<IReadOnlyList<SubmissionGradingState>> ListStatesAsync(
        string examId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);

        const string sql = """
            WITH graded AS (
                SELECT submission_id, max(grading_version) AS graded_version
                  FROM exam.grading_outbox
                 WHERE completed_at IS NOT NULL
                 GROUP BY submission_id
            ),
            latest_failed AS (
                SELECT o.submission_id
                  FROM exam.grading_outbox o
                  LEFT JOIN graded g ON g.submission_id = o.submission_id
                 WHERE o.completed_at IS NULL
                   AND o.last_error IS NOT NULL
                   AND o.grading_version > COALESCE(g.graded_version, 0)
                 GROUP BY o.submission_id
            )
            SELECT s.submission_id,
                   g.graded_version,
                   (f.submission_id IS NOT NULL) AS grading_failed
              FROM exam.final_submissions s
              LEFT JOIN graded g ON g.submission_id = s.submission_id
              LEFT JOIN latest_failed f ON f.submission_id = s.submission_id
             WHERE s.exam_id = @exam_id
             ORDER BY s.submission_id;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("exam_id", NpgsqlDbType.Text, examId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var states = new List<SubmissionGradingState>();
        while (await reader.ReadAsync(cancellationToken))
        {
            states.Add(new SubmissionGradingState(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetBoolean(2)));
        }

        return states;
    }
}
