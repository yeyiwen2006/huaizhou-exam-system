using Exam.Contracts.Grading;
using Npgsql;

namespace Exam.Infrastructure.Postgres.Scoring;

public sealed class PostgresGradingHistory(NpgsqlDataSource dataSource)
{
    public async Task<IReadOnlyList<GradingVersionHistory>> ListAsync(string examId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        // A bundle publishes its paper event before its grading event in the same transaction.
        // Resolve the paper version at that event, rather than attaching today's paper to old rules.
        const string sql = """
            SELECT (grading.payload->>'expectedVersion')::bigint + 1,
                   COALESCE((SELECT (paper.payload->>'expectedVersion')::bigint + 1
                               FROM exam.version_events AS paper
                              WHERE paper.exam_id = grading.exam_id
                                AND paper.operation_type = 'paper.publish'
                                AND paper.stream_revision < grading.stream_revision
                              ORDER BY paper.stream_revision DESC LIMIT 1), 0),
                   grading.recorded_at, COALESCE(grading.payload->>'reason', '')
              FROM exam.version_events AS grading
             WHERE grading.exam_id = $1 AND grading.operation_type = 'grading.publish'
             ORDER BY grading.stream_revision DESC;
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(examId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<GradingVersionHistory>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(reader.GetInt64(0), reader.GetInt64(1),
                reader.GetFieldValue<DateTimeOffset>(2), reader.GetString(3)));
        return rows.AsReadOnly();
    }
}
