using Exam.Domain.Scoring;
using Exam.Infrastructure.Postgres.HighAvailability;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Scoring;

/// <summary>A05 outbox 的租约消费者；领取代次用于拒绝已经过期并被重领的消费者回写。</summary>
public sealed class PostgresGradingTaskSource(NpgsqlDataSource dataSource,
    PostgresActiveNodeWriteGuard? writeGuard = null) : IGradingTaskSource
{
    public const int MaxErrorLength = PendingGradingTask.MaxErrorLength;
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<IReadOnlyList<PendingGradingTask>> ClaimAsync(int maxCount, DateTimeOffset now,
        TimeSpan lease, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        if (lease <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lease));
        const string sql = """
            WITH candidates AS (
                SELECT submission_id, grading_version
                  FROM exam.grading_outbox
                 WHERE completed_at IS NULL
                   AND (claimed_at IS NULL OR lease_expires_at <= @now OR
                        (lease_expires_at IS NULL AND claimed_at <= @lease_cutoff))
                 ORDER BY attempt_count, created_at, submission_id, grading_version
                 LIMIT @max_count
                   FOR UPDATE SKIP LOCKED
            )
            UPDATE exam.grading_outbox AS target
               SET claimed_at = @now, lease_expires_at = @expires_at, attempt_count = target.attempt_count + 1
              FROM candidates
             WHERE target.submission_id = candidates.submission_id
               AND target.grading_version = candidates.grading_version
            RETURNING target.submission_id, target.grading_version, target.attempt_count;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = writeGuard is null ? null : await writeGuard.BeginAsync(connection, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("lease_cutoff", NpgsqlDbType.TimestampTz, (now - lease).ToUniversalTime());
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now.ToUniversalTime());
        command.Parameters.AddWithValue("max_count", NpgsqlDbType.Integer, maxCount);
        command.Parameters.AddWithValue("expires_at", NpgsqlDbType.TimestampTz, (now + lease).ToUniversalTime());
        var tasks = new List<PendingGradingTask>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                tasks.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2)));
        }
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return tasks;
    }

    public ValueTask<bool> CompleteAsync(PendingGradingTask task, CancellationToken cancellationToken = default) =>
        UpdateAsync(task, null, complete: true, cancellationToken);

    public ValueTask<bool> FailAsync(PendingGradingTask task, string error, CancellationToken cancellationToken = default) =>
        UpdateAsync(task, PendingGradingTask.LimitError(error), complete: false, cancellationToken);

    private async ValueTask<bool> UpdateAsync(PendingGradingTask task, string? error, bool complete,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        task.ValidateClaim();
        const string sql = """
            UPDATE exam.grading_outbox
               SET completed_at = CASE WHEN @complete THEN clock_timestamp() ELSE NULL END,
                   claimed_at = NULL, lease_expires_at = NULL, last_error = @error
             WHERE submission_id = @submission_id AND grading_version = @grading_version
               AND attempt_count = @attempt_count AND claimed_at IS NOT NULL AND completed_at IS NULL;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = writeGuard is null ? null : await writeGuard.BeginAsync(connection, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Text, task.SubmissionId);
        command.Parameters.AddWithValue("grading_version", NpgsqlDbType.Bigint, task.GradingVersion);
        command.Parameters.AddWithValue("attempt_count", NpgsqlDbType.Integer, task.AttemptCount);
        command.Parameters.AddWithValue("complete", NpgsqlDbType.Boolean, complete);
        command.Parameters.AddWithValue("error", NpgsqlDbType.Text, error ?? (object)DBNull.Value);
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
        return changed;
    }
}
