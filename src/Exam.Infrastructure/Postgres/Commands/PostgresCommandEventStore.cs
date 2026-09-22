using System.Collections.ObjectModel;
using System.Data;
using System.Text.Json;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Domain.Commands;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Commands;

/// <summary>
/// PostgreSQL implementation of the reliable command event log. Due-event reads acquire a short
/// database lease with FOR UPDATE SKIP LOCKED so multiple service processes do not normally claim
/// the same delivery. Clients must still deduplicate by event id because a lease can expire after a
/// worker failure.
/// </summary>
public sealed class PostgresCommandEventStore : ICommandEventStore
{
    private const string EventColumns = """
        event_id, exam_id, participation_id, entry_version, event_type, state_version,
        target_device_id, target_authorization_version, resource_version,
        protected_unlock_reference, metadata, created_at, next_delivery_at,
        delivery_attempts, acknowledgement_request_id, acknowledgement_result,
        acknowledgement_paper_version, acknowledgement_failure_reason, acknowledged_at
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeSpan _claimLease;
    private readonly int _claimBatchSize;

    public PostgresCommandEventStore(
        NpgsqlDataSource dataSource,
        TimeSpan? claimLease = null,
        int claimBatchSize = 256)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _claimLease = claimLease ?? TimeSpan.FromSeconds(30);
        if (_claimLease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(claimLease));
        }

        if (claimBatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(claimBatchSize));
        }

        _claimBatchSize = claimBatchSize;
    }

    public async ValueTask<PersistentCommandEvent?> FindByCreateRequestAsync(
        string requestId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT event_id, exam_id, participation_id, entry_version, event_type, state_version,
                   target_device_id, target_authorization_version, resource_version,
                   protected_unlock_reference, metadata, created_at, next_delivery_at,
                   delivery_attempts, acknowledgement_request_id, acknowledgement_result,
                   acknowledgement_paper_version, acknowledgement_failure_reason, acknowledged_at
              FROM exam.command_events
             WHERE create_request_id = @request_id;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("request_id", NpgsqlDbType.Text, requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEvent(reader) : null;
    }

    public async ValueTask<CommandAcknowledgement?> FindAcknowledgementByRequestAsync(
        string requestId,
        string eventId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT result_request_id, processing_result, actual_paper_version,
                   failure_reason, acknowledged_at
              FROM exam.command_acknowledgement_requests
             WHERE lookup_request_id = @request_id
               AND event_id = @event_id;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("request_id", NpgsqlDbType.Text, requestId);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Text, eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadAcknowledgement(reader, eventId)
            : null;
    }

    public async ValueTask<CommandCreatePersistenceResult> CreateOrGetAsync(
        string requestId,
        PersistentCommandEvent commandEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commandEvent);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await AcquireAdvisoryLockAsync(
            connection,
            transaction,
            $"command-create:{requestId}",
            cancellationToken);

        var replay = await FindByCreateRequestAsync(connection, transaction, requestId, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new CommandCreatePersistenceResult(CommandCreatePersistenceStatus.RequestReplay, replay);
        }

        await AcquireAdvisoryLockAsync(
            connection,
            transaction,
            $"command-event:{commandEvent.EventId}",
            cancellationToken);

        var conflict = await FindAsync(connection, transaction, commandEvent.EventId, false, cancellationToken);
        if (conflict is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new CommandCreatePersistenceResult(CommandCreatePersistenceStatus.EventIdConflict, conflict);
        }

        const string insertSql = """
            INSERT INTO exam.command_events
                (event_id, create_request_id, exam_id, participation_id, entry_version,
                 event_type, state_version, target_device_id, target_authorization_version,
                 resource_version, protected_unlock_reference, metadata, created_at,
                 next_delivery_at, delivery_attempts,
                 acknowledgement_request_id, acknowledgement_result,
                 acknowledgement_paper_version, acknowledgement_failure_reason, acknowledged_at)
            VALUES
                (@event_id, @create_request_id, @exam_id, @participation_id, @entry_version,
                 @event_type, @state_version, @target_device_id, @target_authorization_version,
                 @resource_version, @protected_unlock_reference, @metadata, @created_at,
                 @next_delivery_at, @delivery_attempts,
                 @ack_request_id, @ack_result, @ack_paper_version, @ack_failure_reason, @acknowledged_at);
            """;
        await using (var insert = new NpgsqlCommand(insertSql, connection, transaction))
        {
            AddEventParameters(insert, requestId, commandEvent);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new CommandCreatePersistenceResult(CommandCreatePersistenceStatus.Created, commandEvent);
    }

    public async ValueTask<PersistentCommandEvent?> FindAsync(
        string eventId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await FindAsync(connection, null, eventId, false, cancellationToken);
    }

    public async ValueTask<IReadOnlyList<PersistentCommandEvent>> ClaimDueForDeviceAsync(
        string participationId,
        long entryVersion,
        string deviceId,
        long authorizationVersion,
        DateTimeOffset dueAtOrBefore,
        CancellationToken cancellationToken = default)
    {
        var sql = $$"""
            WITH due AS
            (
                SELECT event_id
                 FROM exam.command_events
                 WHERE next_delivery_at <= @due_at
                   AND participation_id = @participation_id
                   AND entry_version = @entry_version
                   AND target_device_id = @device_id
                   AND target_authorization_version = @authorization_version
                   AND (acknowledgement_result IS NULL OR
                        acknowledgement_result NOT IN ('Applied', 'RejectedAsStale'))
                 ORDER BY next_delivery_at, created_at, event_id
                 FOR UPDATE SKIP LOCKED
                 LIMIT @batch_size
            )
            UPDATE exam.command_events AS event
               SET next_delivery_at = @lease_until
              FROM due
             WHERE event.event_id = due.event_id
            RETURNING {{QualifyColumns("event")}};
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        var events = new List<PersistentCommandEvent>();
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            command.Parameters.AddWithValue("participation_id", NpgsqlDbType.Text, participationId);
            command.Parameters.AddWithValue("entry_version", NpgsqlDbType.Bigint, entryVersion);
            command.Parameters.AddWithValue("device_id", NpgsqlDbType.Text, deviceId);
            command.Parameters.AddWithValue(
                "authorization_version",
                NpgsqlDbType.Bigint,
                authorizationVersion);
            command.Parameters.AddWithValue("due_at", NpgsqlDbType.TimestampTz, dueAtOrBefore);
            command.Parameters.AddWithValue(
                "lease_until",
                NpgsqlDbType.TimestampTz,
                dueAtOrBefore + _claimLease);
            command.Parameters.AddWithValue("batch_size", NpgsqlDbType.Integer, _claimBatchSize);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                events.Add(ReadEvent(reader));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return events;
    }

    public async ValueTask<PersistentCommandEvent?> RecordDeliveryAttemptAsync(
        string eventId,
        DateTimeOffset attemptedAt,
        DateTimeOffset nextDeliveryAt,
        CancellationToken cancellationToken = default)
    {
        var sql = $$"""
            UPDATE exam.command_events AS event
               SET delivery_attempts = delivery_attempts + 1,
                   last_delivery_attempt_at = @attempted_at,
                   next_delivery_at = @next_delivery_at
             WHERE event_id = @event_id
               AND (acknowledgement_result IS NULL OR
                    acknowledgement_result NOT IN ('Applied', 'RejectedAsStale'))
            RETURNING {{QualifyColumns("event")}};
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Text, eventId);
        command.Parameters.AddWithValue("attempted_at", NpgsqlDbType.TimestampTz, attemptedAt);
        command.Parameters.AddWithValue("next_delivery_at", NpgsqlDbType.TimestampTz, nextDeliveryAt);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEvent(reader) : null;
    }

    public async ValueTask<CommandAckPersistenceResult> AcknowledgeAsync(
        string requestId,
        string eventId,
        CommandAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await AcquireAdvisoryLockAsync(
            connection,
            transaction,
            $"command-ack:{requestId}",
            cancellationToken);

        var replay = await FindAckMappingAsync(connection, transaction, requestId, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return string.Equals(replay.Value.EventId, eventId, StringComparison.Ordinal)
                ? new CommandAckPersistenceResult(
                    CommandAckPersistenceStatus.RequestReplay,
                    replay.Value.Acknowledgement)
                : new CommandAckPersistenceResult(CommandAckPersistenceStatus.RequestIdConflict, null);
        }

        var commandEvent = await FindAsync(connection, transaction, eventId, true, cancellationToken);
        if (commandEvent is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new CommandAckPersistenceResult(CommandAckPersistenceStatus.EventNotFound, null);
        }

        if (IsTerminal(commandEvent.Acknowledgement))
        {
            await InsertAckMappingAsync(
                connection,
                transaction,
                requestId,
                eventId,
                commandEvent.Acknowledgement!,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CommandAckPersistenceResult(
                CommandAckPersistenceStatus.RequestReplay,
                commandEvent.Acknowledgement);
        }

        const string updateSql = """
            UPDATE exam.command_events
               SET acknowledgement_request_id = @ack_request_id,
                   acknowledgement_result = @ack_result,
                   acknowledgement_paper_version = @ack_paper_version,
                   acknowledgement_failure_reason = @ack_failure_reason,
                   acknowledged_at = @acknowledged_at
             WHERE event_id = @event_id
               AND (acknowledgement_result IS NULL OR
                    acknowledgement_result NOT IN ('Applied', 'RejectedAsStale'));
            """;
        await using (var update = new NpgsqlCommand(updateSql, connection, transaction))
        {
            AddAckParameters(update, eventId, acknowledgement);
            var affected = await update.ExecuteNonQueryAsync(cancellationToken);
            if (affected != 1)
            {
                throw new DBConcurrencyException("指令确认状态在事务内发生了意外变化。");
            }
        }

        await InsertAckMappingAsync(
            connection,
            transaction,
            requestId,
            eventId,
            acknowledgement,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CommandAckPersistenceResult(
            CommandAckPersistenceStatus.Acknowledged,
            acknowledgement);
    }

    private static async ValueTask<PersistentCommandEvent?> FindByCreateRequestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string requestId,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT {EventColumns} FROM exam.command_events WHERE create_request_id = @request_id;";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("request_id", NpgsqlDbType.Text, requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEvent(reader) : null;
    }

    private static async ValueTask<PersistentCommandEvent?> FindAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string eventId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var sql = $"SELECT {EventColumns} FROM exam.command_events WHERE event_id = @event_id" +
                  (forUpdate ? " FOR UPDATE;" : ";");
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Text, eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEvent(reader) : null;
    }

    private static async ValueTask<(string EventId, CommandAcknowledgement Acknowledgement)?> FindAckMappingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string requestId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT event_id, result_request_id, processing_result, actual_paper_version,
                   failure_reason, acknowledged_at
              FROM exam.command_acknowledgement_requests
             WHERE lookup_request_id = @request_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("request_id", NpgsqlDbType.Text, requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var mappedEventId = reader.GetString(0);
        return (mappedEventId, ReadAcknowledgement(reader, mappedEventId, 1));
    }

    private static async ValueTask InsertAckMappingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string lookupRequestId,
        string eventId,
        CommandAcknowledgement acknowledgement,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO exam.command_acknowledgement_requests
                (lookup_request_id, event_id, result_request_id, processing_result,
                 actual_paper_version, failure_reason, acknowledged_at)
            VALUES
                (@lookup_request_id, @event_id, @result_request_id, @processing_result,
                 @actual_paper_version, @failure_reason, @acknowledged_at);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("lookup_request_id", NpgsqlDbType.Text, lookupRequestId);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Text, eventId);
        command.Parameters.AddWithValue(
            "result_request_id",
            NpgsqlDbType.Text,
            acknowledgement.RequestId);
        command.Parameters.AddWithValue(
            "processing_result",
            NpgsqlDbType.Text,
            acknowledgement.ProcessingResult.ToString());
        AddNullable(command, "actual_paper_version", NpgsqlDbType.Bigint, acknowledgement.ActualPaperVersion);
        AddNullable(command, "failure_reason", NpgsqlDbType.Text, acknowledgement.FailureReason);
        command.Parameters.AddWithValue(
            "acknowledged_at",
            NpgsqlDbType.TimestampTz,
            acknowledgement.AcknowledgedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask AcquireAdvisoryLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string lockKey,
        CancellationToken cancellationToken)
    {
        const string sql = "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key, 0));";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("lock_key", NpgsqlDbType.Text, lockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddEventParameters(
        NpgsqlCommand command,
        string createRequestId,
        PersistentCommandEvent commandEvent)
    {
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Text, commandEvent.EventId);
        command.Parameters.AddWithValue("create_request_id", NpgsqlDbType.Text, createRequestId);
        command.Parameters.AddWithValue("exam_id", NpgsqlDbType.Text, commandEvent.ExamId);
        AddNullable(command, "participation_id", NpgsqlDbType.Text, commandEvent.ParticipationId);
        command.Parameters.AddWithValue("entry_version", NpgsqlDbType.Bigint, commandEvent.EntryVersion);
        command.Parameters.AddWithValue("event_type", NpgsqlDbType.Text, commandEvent.EventType.ToString());
        command.Parameters.AddWithValue("state_version", NpgsqlDbType.Bigint, commandEvent.StateVersion);
        AddNullable(command, "target_device_id", NpgsqlDbType.Text, commandEvent.TargetDeviceId);
        AddNullable(
            command,
            "target_authorization_version",
            NpgsqlDbType.Bigint,
            commandEvent.TargetAuthorizationVersion);
        AddNullable(command, "resource_version", NpgsqlDbType.Bigint, commandEvent.ResourceVersion);
        AddNullable(
            command,
            "protected_unlock_reference",
            NpgsqlDbType.Text,
            commandEvent.ProtectedUnlockReference);
        command.Parameters.AddWithValue(
            "metadata",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(commandEvent.Metadata, JsonOptions));
        command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, commandEvent.CreatedAt);
        command.Parameters.AddWithValue(
            "next_delivery_at",
            NpgsqlDbType.TimestampTz,
            commandEvent.NextDeliveryAt);
        command.Parameters.AddWithValue(
            "delivery_attempts",
            NpgsqlDbType.Integer,
            commandEvent.DeliveryAttempts);
        AddNullable(
            command,
            "ack_request_id",
            NpgsqlDbType.Text,
            commandEvent.Acknowledgement?.RequestId);
        AddNullable(
            command,
            "ack_result",
            NpgsqlDbType.Text,
            commandEvent.Acknowledgement?.ProcessingResult.ToString());
        AddNullable(
            command,
            "ack_paper_version",
            NpgsqlDbType.Bigint,
            commandEvent.Acknowledgement?.ActualPaperVersion);
        AddNullable(
            command,
            "ack_failure_reason",
            NpgsqlDbType.Text,
            commandEvent.Acknowledgement?.FailureReason);
        AddNullable(
            command,
            "acknowledged_at",
            NpgsqlDbType.TimestampTz,
            commandEvent.Acknowledgement?.AcknowledgedAt);
    }

    private static void AddAckParameters(
        NpgsqlCommand command,
        string eventId,
        CommandAcknowledgement acknowledgement)
    {
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Text, eventId);
        command.Parameters.AddWithValue(
            "ack_request_id",
            NpgsqlDbType.Text,
            acknowledgement.RequestId);
        command.Parameters.AddWithValue(
            "ack_result",
            NpgsqlDbType.Text,
            acknowledgement.ProcessingResult.ToString());
        AddNullable(
            command,
            "ack_paper_version",
            NpgsqlDbType.Bigint,
            acknowledgement.ActualPaperVersion);
        AddNullable(
            command,
            "ack_failure_reason",
            NpgsqlDbType.Text,
            acknowledgement.FailureReason);
        command.Parameters.AddWithValue(
            "acknowledged_at",
            NpgsqlDbType.TimestampTz,
            acknowledgement.AcknowledgedAt);
    }

    private static void AddNullable(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType type,
        object? value) =>
        command.Parameters.AddWithValue(name, type, value ?? DBNull.Value);

    private static PersistentCommandEvent ReadEvent(NpgsqlDataReader reader)
    {
        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(
                           reader.GetString(10),
                           JsonOptions) ?? [];
        CommandAcknowledgement? acknowledgement = null;
        if (!reader.IsDBNull(15))
        {
            acknowledgement = new CommandAcknowledgement(
                reader.GetString(0),
                reader.GetString(14),
                Enum.Parse<CommandProcessingResult>(reader.GetString(15), false),
                reader.IsDBNull(16) ? null : reader.GetInt64(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.GetFieldValue<DateTimeOffset>(18));
        }

        return new PersistentCommandEvent(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetInt64(3),
            Enum.Parse<CommandEventType>(reader.GetString(4), false),
            reader.GetInt64(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.IsDBNull(8) ? null : reader.GetInt64(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            new ReadOnlyDictionary<string, string>(metadata),
            reader.GetFieldValue<DateTimeOffset>(11),
            reader.GetFieldValue<DateTimeOffset>(12),
            reader.GetInt32(13),
            acknowledgement);
    }

    private static CommandAcknowledgement ReadAcknowledgement(
        NpgsqlDataReader reader,
        string eventId,
        int offset = 0) =>
        new(
            eventId,
            reader.GetString(offset),
            Enum.Parse<CommandProcessingResult>(reader.GetString(offset + 1), false),
            reader.IsDBNull(offset + 2) ? null : reader.GetInt64(offset + 2),
            reader.IsDBNull(offset + 3) ? null : reader.GetString(offset + 3),
            reader.GetFieldValue<DateTimeOffset>(offset + 4));

    private static bool IsTerminal(CommandAcknowledgement? acknowledgement) =>
        acknowledgement?.ProcessingResult is
            CommandProcessingResult.Applied or CommandProcessingResult.RejectedAsStale;

    private static string QualifyColumns(string alias) =>
        string.Join(
            ", ",
            EventColumns.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(column => $"{alias}.{column}"));
}
