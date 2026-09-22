using Exam.Domain.Submissions;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Submissions;

/// <summary>
/// Idempotent grading outbox publisher. PostgresSubmissionStore inserts the same key in the final
/// submission transaction; this operation also repairs older/imported submissions safely.
/// </summary>
public sealed class PostgresGradingTaskQueue : IGradingTaskQueue
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _timeProvider;

    public PostgresGradingTaskQueue(NpgsqlDataSource dataSource, TimeProvider? timeProvider = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<bool> EnqueueOnceAsync(
        string submissionId,
        long gradingVersion,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO exam.grading_outbox (submission_id, grading_version, created_at)
            VALUES (@submission_id, @grading_version, @created_at)
            ON CONFLICT (submission_id, grading_version) DO NOTHING;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Text, submissionId);
        command.Parameters.AddWithValue("grading_version", NpgsqlDbType.Bigint, gradingVersion);
        command.Parameters.AddWithValue(
            "created_at",
            NpgsqlDbType.TimestampTz,
            _timeProvider.GetUtcNow());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
