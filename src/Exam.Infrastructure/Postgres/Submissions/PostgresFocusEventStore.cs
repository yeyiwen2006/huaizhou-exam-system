using Exam.Contracts.Submissions;
using Exam.Domain.Submissions;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Submissions;

/// <summary>
/// PostgreSQL focus-loss event store. Authorization is validated against the canonical
/// exam.participations row inside the same transaction that inserts the events;
/// An initial zero-duration event can be completed once; retries cannot overwrite a completed interval.
/// </summary>
public sealed class PostgresFocusEventStore(NpgsqlDataSource dataSource) : IFocusEventStore
{
    public async ValueTask<FocusEventPersistenceResult> SaveAsync(
        ReportFocusEventsRequest request,
        DateTimeOffset persistedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted,
            cancellationToken);

        const string authorizationSql = """
            SELECT exam_id, candidate_id, entry_version,
                   authorized_device_id, authorization_version
              FROM exam.participations
             WHERE participation_id = @participation_id
             FOR SHARE;
            """;
        string examId;
        string candidateId;
        long entryVersion;
        string authorizedDeviceId;
        long authorizationVersion;
        await using (var authorizationCommand = new NpgsqlCommand(authorizationSql, connection, transaction))
        {
            authorizationCommand.Parameters.AddWithValue("participation_id", NpgsqlDbType.Text, request.ParticipationId);
            await using var reader = await authorizationCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.CommitAsync(cancellationToken);
                return new FocusEventPersistenceResult(FocusEventPersistenceStatus.ParticipationNotFound, null);
            }

            examId = reader.GetString(0);
            candidateId = reader.GetString(1);
            entryVersion = reader.GetInt64(2);
            authorizedDeviceId = reader.GetString(3);
            authorizationVersion = reader.GetInt64(4);
        }

        if (!string.Equals(request.ExamId, examId, StringComparison.Ordinal) ||
            !string.Equals(request.CandidateId, candidateId, StringComparison.Ordinal) ||
            request.EntryVersion != entryVersion ||
            !string.Equals(request.DeviceId, authorizedDeviceId, StringComparison.Ordinal) ||
            request.AuthorizationVersion != authorizationVersion)
        {
            await transaction.CommitAsync(cancellationToken);
            return new FocusEventPersistenceResult(FocusEventPersistenceStatus.AuthorizationRevoked, null);
        }

        const string insertSql = """
            INSERT INTO exam.focus_loss_events
                (participation_id, event_id, exam_id, candidate_id, entry_version, device_id,
                 authorization_version, paper_version, lost_at, regained_at, duration_ms, received_at)
            VALUES
                (@participation_id, @event_id, @exam_id, @candidate_id, @entry_version, @device_id,
                 @authorization_version, @paper_version, @lost_at, @regained_at, @duration_ms, @received_at)
            ON CONFLICT (participation_id, event_id) DO UPDATE
               SET regained_at = EXCLUDED.regained_at, duration_ms = EXCLUDED.duration_ms
             WHERE exam.focus_loss_events.duration_ms = 0 AND EXCLUDED.duration_ms > 0
               AND exam.focus_loss_events.lost_at = EXCLUDED.lost_at
               AND exam.focus_loss_events.entry_version = EXCLUDED.entry_version
               AND exam.focus_loss_events.device_id = EXCLUDED.device_id
               AND exam.focus_loss_events.authorization_version = EXCLUDED.authorization_version;
            """;
        foreach (var item in request.Events)
        {
            await using var command = new NpgsqlCommand(insertSql, connection, transaction);
            command.Parameters.AddWithValue("participation_id", NpgsqlDbType.Text, request.ParticipationId);
            command.Parameters.AddWithValue("event_id", NpgsqlDbType.Text, item.EventId);
            command.Parameters.AddWithValue("exam_id", NpgsqlDbType.Text, request.ExamId);
            command.Parameters.AddWithValue("candidate_id", NpgsqlDbType.Text, request.CandidateId);
            command.Parameters.AddWithValue("entry_version", NpgsqlDbType.Bigint, request.EntryVersion);
            command.Parameters.AddWithValue("device_id", NpgsqlDbType.Text, request.DeviceId);
            command.Parameters.AddWithValue("authorization_version", NpgsqlDbType.Bigint, request.AuthorizationVersion);
            command.Parameters.AddWithValue("paper_version", NpgsqlDbType.Bigint, request.PaperVersion);
            command.Parameters.AddWithValue("lost_at", NpgsqlDbType.TimestampTz, item.LostAt.ToUniversalTime());
            command.Parameters.AddWithValue("regained_at", NpgsqlDbType.TimestampTz, item.RegainedAt.ToUniversalTime());
            command.Parameters.AddWithValue("duration_ms", NpgsqlDbType.Bigint, item.DurationMilliseconds);
            command.Parameters.AddWithValue("received_at", NpgsqlDbType.TimestampTz, persistedAt.ToUniversalTime());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        var receipt = new FocusEventsReceipt(
            request.ExamId,
            request.CandidateId,
            request.ParticipationId,
            request.PaperVersion,
            request.Events.Select(item => item.EventId).ToArray().AsReadOnly(),
            persistedAt);
        return new FocusEventPersistenceResult(FocusEventPersistenceStatus.Saved, receipt);
    }

    public async ValueTask<IReadOnlyList<FocusLossEventSummary>> ListSummariesAsync(
        string examId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT participation_id, candidate_id,
                   count(*)::int, sum(duration_ms)::bigint, max(lost_at)
              FROM exam.focus_loss_events
             WHERE exam_id = @exam_id
             GROUP BY participation_id, candidate_id
             ORDER BY candidate_id;
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("exam_id", NpgsqlDbType.Text, examId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var summaries = new List<FocusLossEventSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new FocusLossEventSummary(
                examId,
                reader.GetString(1),
                reader.GetString(0),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return summaries.AsReadOnly();
    }
}
