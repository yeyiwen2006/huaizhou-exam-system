using Exam.Contracts.Submissions;
using Exam.Domain.Submissions;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Submissions;

public sealed class PostgresCollectionCompletionStore : ICollectionCompletionStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly Func<string> _eventIdFactory;

    public PostgresCollectionCompletionStore(
        NpgsqlDataSource dataSource,
        Func<string>? eventIdFactory = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _eventIdFactory = eventIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    }

    public async ValueTask<CollectionCompletedNotification?> TryRecordAsync(
        string examId,
        long rosterVersion,
        int participantCount,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        if (participantCount <= 0)
        {
            return null;
        }

        const string sql = """
            INSERT INTO exam.collection_completion_events
                (exam_id, roster_version, event_id, participant_count, completed_at)
            VALUES
                (@exam_id, @roster_version, @event_id, @participant_count, @completed_at)
            ON CONFLICT (exam_id, roster_version) DO NOTHING
            RETURNING event_id, exam_id, roster_version, participant_count, completed_at;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("exam_id", NpgsqlDbType.Text, examId);
        command.Parameters.AddWithValue("roster_version", NpgsqlDbType.Bigint, rosterVersion);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Text, _eventIdFactory());
        command.Parameters.AddWithValue("participant_count", NpgsqlDbType.Integer, participantCount);
        command.Parameters.AddWithValue("completed_at", NpgsqlDbType.TimestampTz, completedAt);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CollectionCompletedNotification(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetInt32(3),
            reader.GetFieldValue<DateTimeOffset>(4));
    }
}
