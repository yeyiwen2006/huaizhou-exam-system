using System.Data;
using Exam.Application.Activation;
using Exam.Contracts.Activation;
using Exam.Domain.Activation;
using Npgsql;

namespace Exam.Infrastructure.Postgres.Activation;

/// <summary>
/// PostgreSQL implementation of the activation transaction boundary. The current
/// application callback is synchronous, therefore transaction members use Npgsql's
/// synchronous command APIs while connection open, begin, commit and rollback remain
/// asynchronous. No task is synchronously waited. A future all-async persistence
/// contract can replace these command calls without changing the schema.
/// </summary>
public sealed class PostgresActivationPersistence(NpgsqlDataSource dataSource) : IActivationPersistence
{
    public async ValueTask<T> ExecuteTransactionAsync<T>(
        Func<IActivationTransaction, T> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var result = operation(new PostgresActivationTransaction(connection, transaction));
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private sealed class PostgresActivationTransaction(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) : IActivationTransaction
    {
        public DeviceRegistration? FindDevice(string deviceId)
        {
            using var command = CreateCommand("""
                SELECT device_id, client_number, activation_date, activation_status,
                       room_group_id, protected_credential, credential_version,
                       created_at, last_seen_at, version
                  FROM exam.activation_devices
                 WHERE device_id = @device_id
                   FOR UPDATE;
                """);
            command.Parameters.AddWithValue("device_id", deviceId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadDevice(reader) : null;
        }

        public DeviceActivationRequest? FindActivationRequest(string activationRequestId)
        {
            using var command = CreateCommand("""
                SELECT activation_request_id, device_id, protected_credential,
                       activation_status, requested_at, client_number, credential_version
                  FROM exam.device_activation_requests
                 WHERE activation_request_id = @activation_request_id
                   FOR UPDATE;
                """);
            command.Parameters.AddWithValue("activation_request_id", activationRequestId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadActivationRequest(reader) : null;
        }

        public DeviceActivationRequest? FindLatestActivationRequestForDevice(string deviceId)
        {
            LockLogicalKey($"activation-device:{deviceId}");
            using var command = CreateCommand("""
                SELECT activation_request_id, device_id, protected_credential,
                       activation_status, requested_at, client_number, credential_version
                  FROM exam.device_activation_requests
                 WHERE device_id = @device_id
                 ORDER BY requested_at DESC, activation_request_id DESC
                 LIMIT 1
                   FOR UPDATE;
                """);
            command.Parameters.AddWithValue("device_id", deviceId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadActivationRequest(reader) : null;
        }

        public IReadOnlyList<DeviceActivationRequest> ListPendingRequests()
        {
            using var command = CreateCommand("""
                SELECT activation_request_id, device_id, protected_credential,
                       activation_status, requested_at, client_number, credential_version
                  FROM exam.device_activation_requests
                 WHERE activation_status = 'pending'
                 ORDER BY requested_at ASC, activation_request_id ASC;
                """);
            using var reader = command.ExecuteReader();
            var results = new List<DeviceActivationRequest>();
            while (reader.Read()) results.Add(ReadActivationRequest(reader));
            return results;
        }

        public int GetLastDailySequence(DateOnly date)
        {
            using (var ensure = CreateCommand("""
                INSERT INTO exam.activation_daily_sequences (activation_date, last_sequence)
                VALUES (@activation_date, 0)
                ON CONFLICT (activation_date) DO NOTHING;
                """))
            {
                ensure.Parameters.AddWithValue("activation_date", date);
                ensure.ExecuteNonQuery();
            }

            using var command = CreateCommand("""
                SELECT last_sequence
                  FROM exam.activation_daily_sequences
                 WHERE activation_date = @activation_date
                   FOR UPDATE;
                """);
            command.Parameters.AddWithValue("activation_date", date);
            return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public ActivationIdempotencyRecord? FindIdempotencyRecord(string requestId)
        {
            LockLogicalKey($"activation-request:{requestId}");
            using var command = CreateCommand("""
                SELECT request_id, operation, payload_hash, response_type, response_json::text
                  FROM exam.activation_idempotency
                 WHERE request_id = @request_id
                   FOR UPDATE;
                """);
            command.Parameters.AddWithValue("request_id", requestId);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new ActivationIdempotencyRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4))
                : null;
        }

        public void SaveDevice(DeviceRegistration device)
        {
            if (device.RoomGroupId is not null)
            {
                using var room = CreateCommand("SELECT group_id FROM exam.room_groups WHERE group_id=@room_id FOR KEY SHARE;");
                room.Parameters.AddWithValue("room_id",device.RoomGroupId);
                if (room.ExecuteScalar() is null)
                    throw new Exam.Application.Activation.ActivationRuleException(
                        Exam.Contracts.Activation.ActivationErrorCodes.InvalidRequest,"所选试场已不存在，请刷新分组后重新批准。");
            }
            using var command = CreateCommand("""
                INSERT INTO exam.activation_devices
                    (device_id, client_number, activation_date, activation_status,
                     room_group_id, protected_credential, credential_version,
                     created_at, last_seen_at, version)
                VALUES
                    (@device_id, @client_number, @activation_date, @activation_status,
                     @room_group_id, @protected_credential, @credential_version,
                     @created_at, @last_seen_at, @version)
                ON CONFLICT (device_id) DO UPDATE SET
                    activation_status = EXCLUDED.activation_status,
                    room_group_id = EXCLUDED.room_group_id,
                    protected_credential = EXCLUDED.protected_credential,
                    credential_version = EXCLUDED.credential_version,
                    last_seen_at = EXCLUDED.last_seen_at,
                    version = EXCLUDED.version
                WHERE EXCLUDED.version = exam.activation_devices.version + 1;
                """);
            command.Parameters.AddWithValue("device_id", device.DeviceId);
            command.Parameters.AddWithValue("client_number", device.ClientNumber);
            command.Parameters.AddWithValue("activation_date", device.ActivationDate);
            command.Parameters.AddWithValue("activation_status", ToDatabaseStatus(device.Status));
            command.Parameters.AddWithValue("room_group_id", (object?)device.RoomGroupId ?? DBNull.Value);
            command.Parameters.AddWithValue("protected_credential", device.ProtectedCredential);
            command.Parameters.AddWithValue("credential_version", device.CredentialVersion);
            command.Parameters.AddWithValue("created_at", device.CreatedAt.ToUniversalTime());
            command.Parameters.AddWithValue("last_seen_at", (object?)device.LastSeenAt?.ToUniversalTime() ?? DBNull.Value);
            command.Parameters.AddWithValue("version", device.Version);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new DBConcurrencyException("Activation device version changed before the transaction could save it.");
            }
        }

        public void SaveActivationRequest(DeviceActivationRequest request)
        {
            using var command = CreateCommand("""
                INSERT INTO exam.device_activation_requests
                    (activation_request_id, device_id, protected_credential,
                     activation_status, requested_at, client_number, credential_version)
                VALUES
                    (@activation_request_id, @device_id, @protected_credential,
                     @activation_status, @requested_at, @client_number, @credential_version)
                ON CONFLICT (activation_request_id) DO UPDATE SET
                    activation_status = EXCLUDED.activation_status,
                    client_number = EXCLUDED.client_number,
                    credential_version = EXCLUDED.credential_version;
                """);
            command.Parameters.AddWithValue("activation_request_id", request.ActivationRequestId);
            command.Parameters.AddWithValue("device_id", request.DeviceId);
            command.Parameters.AddWithValue("protected_credential", request.ProtectedCredential);
            command.Parameters.AddWithValue("activation_status", ToDatabaseStatus(request.Status));
            command.Parameters.AddWithValue("requested_at", request.RequestedAt.ToUniversalTime());
            command.Parameters.AddWithValue("client_number", (object?)request.ClientNumber ?? DBNull.Value);
            command.Parameters.AddWithValue("credential_version", request.CredentialVersion);
            command.ExecuteNonQuery();
        }

        public void SetLastDailySequence(DateOnly date, int sequence)
        {
            using var command = CreateCommand("""
                INSERT INTO exam.activation_daily_sequences (activation_date, last_sequence)
                VALUES (@activation_date, @last_sequence)
                ON CONFLICT (activation_date) DO UPDATE
                    SET last_sequence = EXCLUDED.last_sequence
                WHERE EXCLUDED.last_sequence = exam.activation_daily_sequences.last_sequence + 1;
                """);
            command.Parameters.AddWithValue("activation_date", date);
            command.Parameters.AddWithValue("last_sequence", sequence);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new DBConcurrencyException("Daily activation sequence did not advance by exactly one.");
            }
        }

        public void SaveIdempotencyRecord(ActivationIdempotencyRecord record)
        {
            using var command = CreateCommand("""
                INSERT INTO exam.activation_idempotency
                    (request_id, operation, payload_hash, response_type, response_json)
                VALUES
                    (@request_id, @operation, @payload_hash, @response_type,
                     CAST(@response_json AS jsonb));
                """);
            command.Parameters.AddWithValue("request_id", record.RequestId);
            command.Parameters.AddWithValue("operation", record.Operation);
            command.Parameters.AddWithValue("payload_hash", record.PayloadHash);
            command.Parameters.AddWithValue("response_type", record.ResponseType);
            command.Parameters.AddWithValue("response_json", record.ResponseJson);
            command.ExecuteNonQuery();
        }

        private NpgsqlCommand CreateCommand(string commandText) => new(commandText, connection, transaction);

        private void LockLogicalKey(string key)
        {
            using var command = CreateCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));");
            command.Parameters.AddWithValue("key", key);
            command.ExecuteNonQuery();
        }

        private static DeviceRegistration ReadDevice(NpgsqlDataReader reader) => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetFieldValue<DateOnly>(2),
            ParseStatus(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetInt64(9));

        private static DeviceActivationRequest ReadActivationRequest(NpgsqlDataReader reader) => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            ParseStatus(reader.GetString(3)),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt64(6));

        private static string ToDatabaseStatus(ActivationStatus status) => status.ToString().ToLowerInvariant();

        private static ActivationStatus ParseStatus(string value) =>
            Enum.TryParse<ActivationStatus>(value, true, out var status)
                ? status
                : throw new DataException($"Unknown activation status '{value}'.");
    }
}
