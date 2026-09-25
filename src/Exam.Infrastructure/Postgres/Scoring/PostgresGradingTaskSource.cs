using Exam.Domain.Scoring;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Scoring;

/// <summary>
/// 计分任务的领取与结项，读取 A05 的 exam.grading_outbox。
///
/// 领取以**租约**表达：把 claimed_at 置为当前时间并累加尝试次数，租约过期后任务可被再次领取，
/// 因此消费者崩溃不会让任务永久卡住；重复领取不会让成绩重复累计，因为结果表按答卷去重
/// （见 PostgresGradingResultStore）。
/// **失败只记录错误并释放租约，不删除任务** —— 需求明确要求持久待发任务不得因重试次数被删除。
/// </summary>
public sealed class PostgresGradingTaskSource : IGradingTaskSource
{
    /// <summary>最近错误的最大保存长度，避免异常文本无限增长。</summary>
    public const int MaxErrorLength = 2000;

    private readonly NpgsqlDataSource _dataSource;

    public PostgresGradingTaskSource(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<IReadOnlyList<PendingGradingTask>> ClaimAsync(
        int maxCount,
        DateTimeOffset now,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        if (lease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lease));
        }

        const string sql = """
            WITH candidates AS (
                SELECT submission_id, grading_version
                  FROM exam.grading_outbox
                 WHERE completed_at IS NULL
                   AND (claimed_at IS NULL OR claimed_at < @lease_cutoff)
                 ORDER BY created_at, submission_id, grading_version
                 LIMIT @max_count
                   FOR UPDATE SKIP LOCKED
            )
            UPDATE exam.grading_outbox AS target
               SET claimed_at = @now,
                   attempt_count = target.attempt_count + 1
              FROM candidates
             WHERE target.submission_id = candidates.submission_id
               AND target.grading_version = candidates.grading_version
            RETURNING target.submission_id, target.grading_version, target.attempt_count;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("lease_cutoff", NpgsqlDbType.TimestampTz, now - lease);
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        command.Parameters.AddWithValue("max_count", NpgsqlDbType.Integer, maxCount);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var tasks = new List<PendingGradingTask>();
        while (await reader.ReadAsync(cancellationToken))
        {
            tasks.Add(new PendingGradingTask(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2)));
        }

        return tasks;
    }

    public async ValueTask CompleteAsync(
        string submissionId,
        long gradingVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(submissionId);

        // 重复完成是幂等的：保留首次完成时间，不清空它。
        const string sql = """
            UPDATE exam.grading_outbox
               SET completed_at = COALESCE(completed_at, clock_timestamp()),
                   claimed_at = NULL,
                   last_error = NULL
             WHERE submission_id = @submission_id AND grading_version = @grading_version;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Text, submissionId);
        command.Parameters.AddWithValue("grading_version", NpgsqlDbType.Bigint, gradingVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask FailAsync(
        string submissionId,
        long gradingVersion,
        string error,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(submissionId);

        const string sql = """
            UPDATE exam.grading_outbox
               SET last_error = @error,
                   claimed_at = NULL
             WHERE submission_id = @submission_id AND grading_version = @grading_version;
            """;

        var text = error ?? string.Empty;
        if (text.Length > MaxErrorLength)
        {
            text = text[..MaxErrorLength];
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Text, submissionId);
        command.Parameters.AddWithValue("grading_version", NpgsqlDbType.Bigint, gradingVersion);
        command.Parameters.AddWithValue("error", NpgsqlDbType.Text, text);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
