using Exam.Contracts.Submissions;
using Exam.Domain.Submissions;
using Npgsql;

namespace Exam.Infrastructure.Postgres.Submissions;

/// <summary>
/// Transactional collection outbox. The pending row is the serialization point; business
/// mutations queue their check in the same transaction and cannot lose a wake-up to a worker.
/// </summary>
public sealed class PostgresCollectionCompletionStore(
    NpgsqlDataSource dataSource,
    Func<string>? eventIdFactory = null) : ICollectionCompletionStore, IAuthoritativeCollectionCompletionStore
{
    private readonly Func<string> _eventIdFactory = eventIdFactory ?? (() => Guid.NewGuid().ToString("N"));

    // Kept for source compatibility. Caller counts/versions cannot manufacture completion events.
    public ValueTask<CollectionCompletedNotification?> TryRecordAsync(
        string examId, long rosterVersion, int participantCount, DateTimeOffset completedAt,
        CancellationToken cancellationToken = default) => EvaluateAsync(examId, cancellationToken);

    public async ValueTask<CollectionCompletedNotification?> EvaluateAsync(
        string examId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var claim = new NpgsqlCommand("""
            INSERT INTO exam.collection_pending_checks (exam_id) VALUES (@exam_id)
            ON CONFLICT (exam_id) DO UPDATE SET requested_at = clock_timestamp();
            """, connection, transaction))
        {
            claim.Parameters.AddWithValue("exam_id", examId);
            await claim.ExecuteNonQueryAsync(cancellationToken);
        }

        var notification = await EvaluateLockedAsync(connection, transaction, examId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return notification;
    }

    public async ValueTask<int> ProcessPendingAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var processed = 0;
        while (processed < limit)
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            string? examId;
            await using (var claim = new NpgsqlCommand("""
                SELECT exam_id FROM exam.collection_pending_checks
                ORDER BY requested_at, exam_id LIMIT 1 FOR UPDATE SKIP LOCKED;
                """, connection, transaction))
            {
                examId = await claim.ExecuteScalarAsync(cancellationToken) as string;
            }
            if (examId is null) return processed;
            await EvaluateLockedAsync(connection, transaction, examId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            processed++;
        }
        return processed;
    }

    private async Task<CollectionCompletedNotification?> EvaluateLockedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string examId,
        CancellationToken cancellationToken)
    {
        // One statement snapshot binds the authoritative version to the actual members. Do not
        // lock participation rows here: writers lock them before queuing and would deadlock.
        const string sql = """
            WITH members AS (
                SELECT p.participation_id,
                       EXISTS (SELECT 1 FROM exam.final_submissions s
                               WHERE s.participation_id = p.participation_id
                                 AND s.exam_id = p.exam_id AND s.candidate_id = p.candidate_id) AS collected
                  FROM exam.participations p
                 WHERE p.exam_id = @exam_id AND p.business_state <> 'not_participating'
            ), counts AS (
                SELECT count(*)::integer AS total, count(*) FILTER (WHERE NOT collected) AS missing FROM members
            )
            INSERT INTO exam.collection_completion_events
                (exam_id, roster_version, event_id, participant_count, completed_at, is_authoritative)
            SELECT v.exam_id, v.roster_version, @event_id, c.total, clock_timestamp(), true
              FROM exam.actual_roster_versions v CROSS JOIN counts c
             WHERE v.exam_id = @exam_id AND c.total > 0 AND c.missing = 0
            ON CONFLICT (exam_id, roster_version) DO NOTHING
            RETURNING event_id, exam_id, roster_version, participant_count, completed_at;
            """;
        CollectionCompletedNotification? notification = null;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("exam_id", examId);
            command.Parameters.AddWithValue("event_id", _eventIdFactory());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) notification = ReadNotification(reader);
        }
        await using var remove = new NpgsqlCommand(
            "DELETE FROM exam.collection_pending_checks WHERE exam_id = @exam_id;", connection, transaction);
        remove.Parameters.AddWithValue("exam_id", examId);
        await remove.ExecuteNonQueryAsync(cancellationToken);
        return notification;
    }

    public async ValueTask<IReadOnlyList<CollectionCompletedNotification>> ReadEventsAsync(
        string examId, long afterRosterVersion, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        if (afterRosterVersion < 0) throw new ArgumentOutOfRangeException(nameof(afterRosterVersion));
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // Visibility does not prove an earlier synchronous COMMIT returned. This WAL-producing
        // update and COMMIT fence earlier WAL under the configured business durability policy.
        await using var command = new NpgsqlCommand("""
            UPDATE exam.collection_completion_events e SET delivery_fence_at = clock_timestamp()
             WHERE e.exam_id = @exam_id AND e.roster_version IN (
                 SELECT roster_version FROM exam.collection_completion_events
                  WHERE exam_id = @exam_id AND roster_version > @after_version AND is_authoritative
                  ORDER BY roster_version LIMIT @limit)
            RETURNING event_id, exam_id, roster_version, participant_count, completed_at;
            """, connection, transaction);
        command.Parameters.AddWithValue("exam_id", examId);
        command.Parameters.AddWithValue("after_version", afterRosterVersion);
        command.Parameters.AddWithValue("limit", limit);
        var notifications = new List<CollectionCompletedNotification>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) notifications.Add(ReadNotification(reader));
        }
        await transaction.CommitAsync(cancellationToken);
        return notifications.OrderBy(notification => notification.RosterVersion).ToArray();
    }

    private static CollectionCompletedNotification ReadNotification(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt32(3),
        reader.GetFieldValue<DateTimeOffset>(4));
}
