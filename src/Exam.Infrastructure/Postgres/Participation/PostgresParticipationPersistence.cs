using System.Data;
using System.Text.Json;
using Exam.Application.Participation;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Domain.Participation;
using Npgsql;

namespace Exam.Infrastructure.Postgres.Participation;

/// <summary>
/// PostgreSQL implementation of the A03 transaction boundary. Logical-key advisory
/// locks cover the absent-row case for (examId, candidateId) and requestId, while row
/// locks serialize updates to existing participation and conflict records.
/// </summary>
public sealed class PostgresParticipationPersistence(NpgsqlDataSource dataSource) : IParticipationPersistence
{
    public async ValueTask<T> ExecuteTransactionAsync<T>(
        Func<IParticipationTransaction, T> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var result = operation(new PostgresParticipationTransaction(connection, transaction));
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private sealed class PostgresParticipationTransaction(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) : IParticipationTransaction
    {
        public bool IsActiveDevice(string deviceId)
        {
            using var command = CreateCommand("""
                SELECT activation_status
                  FROM exam.activation_devices
                 WHERE device_id = @device_id
                   FOR SHARE;
                """);
            command.Parameters.AddWithValue("device_id", deviceId);
            return command.ExecuteScalar() is string status &&
                string.Equals(status, "active", StringComparison.Ordinal);
        }

        public bool IsExamEntryCurrent(ExamEntrySnapshot expected)
        {
            using var command = CreateCommand("""
                SELECT scheduled_start, scheduled_end, lifecycle,
                       package_ready, instructions_ready, encrypted_package_uri,
                       package_length, paper_version, instructions_version,
                       unlock_reference
                  FROM exam.exam_entry_access
                 WHERE exam_id = @exam_id
                   FOR SHARE;
                """);
            command.Parameters.AddWithValue("exam_id", expected.ExamId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            return reader.GetFieldValue<DateTimeOffset>(0) == expected.ScheduledStart.ToUniversalTime() &&
                reader.GetFieldValue<DateTimeOffset>(1) == expected.ScheduledEnd.ToUniversalTime() &&
                string.Equals(reader.GetString(2), ToDatabaseLifecycle(expected.Lifecycle), StringComparison.Ordinal) &&
                reader.GetBoolean(3) == expected.PackageReady &&
                reader.GetBoolean(4) == expected.InstructionsReady &&
                string.Equals(reader.GetString(5), expected.EncryptedPackageUri, StringComparison.Ordinal) &&
                reader.GetInt64(6) == expected.PackageLength &&
                reader.GetInt64(7) == expected.PaperVersion &&
                reader.GetInt64(8) == expected.InstructionsVersion &&
                string.Equals(reader.IsDBNull(9) ? null : reader.GetString(9), expected.UnlockReference, StringComparison.Ordinal);
        }

        public StudentSession? FindSession(string sessionId)
        {
            using var command = CreateCommand("""
                SELECT session_id, device_id, candidate_id, candidate_name,
                       class_text, admission_no, exam_ids,
                       candidate_ids_by_exam::text, authenticated_at
                  FROM exam.student_sessions
                 WHERE session_id = @session_id
                   FOR SHARE;
                """);
            command.Parameters.AddWithValue("session_id", sessionId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadSession(reader) : null;
        }

        public EntryPreparation? FindPreparation(string preparationId)
        {
            using var command = CreateCommand("""
                SELECT preparation_id, session_id, exam_id, candidate_id, device_id,
                       paper_version, instructions_version, prepared_at
                  FROM exam.entry_preparations
                 WHERE preparation_id = @preparation_id;
                """);
            command.Parameters.AddWithValue("preparation_id", preparationId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadPreparation(reader) : null;
        }

        public ParticipationRecord? FindParticipation(string participationId)
        {
            using var command = CreateCommand(ParticipationSelect + """
                 WHERE participation_id = @participation_id
                   FOR UPDATE;
                """);
            command.Parameters.AddWithValue("participation_id", participationId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadParticipation(reader) : null;
        }

        public ParticipationRecord? FindParticipation(string examId, string candidateId)
        {
            LockLogicalKeyShared($"participation-exam:{examId}");
            LockLogicalKey($"participation:{examId.Length}:{examId}{candidateId}");
            using var command = CreateCommand(ParticipationSelect + """
                 WHERE exam_id = @exam_id AND candidate_id = @candidate_id
                   FOR UPDATE;
                """);
            command.Parameters.AddWithValue("exam_id", examId);
            command.Parameters.AddWithValue("candidate_id", candidateId);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadParticipation(reader) : null;
        }

        public IReadOnlyList<ParticipationRecord> ListParticipations(string examId)
        {
            LockLogicalKey($"participation-exam:{examId}");
            using var command = CreateCommand(ParticipationSelect + """
                 WHERE exam_id = @exam_id
                 ORDER BY candidate_id
                   FOR UPDATE;
                """);
            command.Parameters.AddWithValue("exam_id", examId);
            using var reader = command.ExecuteReader();
            var results = new List<ParticipationRecord>();
            while (reader.Read())
            {
                results.Add(ReadParticipation(reader));
            }

            return results;
        }

        public DeviceConflict? FindConflict(string participationId)
        {
            using var command = CreateCommand("""
                SELECT participation_id, original_device_id, pending_device_id, detected_at
                  FROM exam.participation_device_conflicts
                 WHERE participation_id = @participation_id
                   FOR UPDATE;
                """);
            command.Parameters.AddWithValue("participation_id", participationId);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new DeviceConflict(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetFieldValue<DateTimeOffset>(3))
                : null;
        }

        public ParticipationIdempotencyRecord? FindIdempotencyRecord(string requestId)
        {
            LockLogicalKey($"participation-request:{requestId}");
            using var command = CreateCommand("""
                SELECT request_id, operation, payload_hash, response_type, response_json::text
                  FROM exam.participation_idempotency
                 WHERE request_id = @request_id
                   FOR UPDATE;
                """);
            command.Parameters.AddWithValue("request_id", requestId);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new ParticipationIdempotencyRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4))
                : null;
        }

        public void SaveSession(StudentSession session)
        {
            using var command = CreateCommand("""
                INSERT INTO exam.student_sessions
                    (session_id, device_id, candidate_id, candidate_name, class_text,
                     admission_no, exam_ids, candidate_ids_by_exam, authenticated_at)
                VALUES
                    (@session_id, @device_id, @candidate_id, @candidate_name, @class_text,
                     @admission_no, @exam_ids, CAST(@candidate_ids_by_exam AS jsonb), @authenticated_at);
                """);
            command.Parameters.AddWithValue("session_id", session.SessionId);
            command.Parameters.AddWithValue("device_id", session.DeviceId);
            command.Parameters.AddWithValue("candidate_id", session.Candidate.CandidateId);
            command.Parameters.AddWithValue("candidate_name", session.Candidate.Name);
            command.Parameters.AddWithValue("class_text", session.Candidate.ClassText);
            command.Parameters.AddWithValue("admission_no", session.Candidate.AdmissionNo);
            command.Parameters.AddWithValue("exam_ids", session.ExamIds.ToArray());
            command.Parameters.AddWithValue("candidate_ids_by_exam", JsonSerializer.Serialize(session.CandidateIdsByExam));
            command.Parameters.AddWithValue("authenticated_at", session.AuthenticatedAt.ToUniversalTime());
            command.ExecuteNonQuery();
        }

        public void SavePreparation(EntryPreparation preparation)
        {
            using var command = CreateCommand("""
                INSERT INTO exam.entry_preparations
                    (preparation_id, session_id, exam_id, candidate_id, device_id,
                     paper_version, instructions_version, prepared_at)
                VALUES
                    (@preparation_id, @session_id, @exam_id, @candidate_id, @device_id,
                     @paper_version, @instructions_version, @prepared_at);
                """);
            command.Parameters.AddWithValue("preparation_id", preparation.PreparationId);
            command.Parameters.AddWithValue("session_id", preparation.SessionId);
            command.Parameters.AddWithValue("exam_id", preparation.ExamId);
            command.Parameters.AddWithValue("candidate_id", preparation.CandidateId);
            command.Parameters.AddWithValue("device_id", preparation.DeviceId);
            command.Parameters.AddWithValue("paper_version", preparation.PaperVersion);
            command.Parameters.AddWithValue("instructions_version", preparation.InstructionsVersion);
            command.Parameters.AddWithValue("prepared_at", preparation.PreparedAt.ToUniversalTime());
            command.ExecuteNonQuery();
        }

        public void SaveParticipation(ParticipationRecord participation)
        {
            using var command = CreateCommand("""
                INSERT INTO exam.participations
                    (participation_id, exam_id, candidate_id, entry_version, business_state,
                     authorized_device_id, authorization_version, version,
                     current_paper_version, current_grading_version)
                VALUES
                    (@participation_id, @exam_id, @candidate_id, @entry_version, @business_state,
                     @authorized_device_id, @authorization_version, @version, 1, 1)
                ON CONFLICT (participation_id) DO UPDATE SET
                    entry_version = EXCLUDED.entry_version,
                    business_state = EXCLUDED.business_state,
                    authorized_device_id = EXCLUDED.authorized_device_id,
                    authorization_version = EXCLUDED.authorization_version,
                    version = EXCLUDED.version
                WHERE EXCLUDED.version = exam.participations.version + 1;
                """);
            command.Parameters.AddWithValue("participation_id", participation.ParticipationId);
            command.Parameters.AddWithValue("exam_id", participation.ExamId);
            command.Parameters.AddWithValue("candidate_id", participation.CandidateId);
            command.Parameters.AddWithValue("entry_version", participation.EntryVersion);
            command.Parameters.AddWithValue("business_state", ToDatabaseState(participation.BusinessState));
            command.Parameters.AddWithValue("authorized_device_id", participation.AuthorizedDeviceId);
            command.Parameters.AddWithValue("authorization_version", participation.AuthorizationVersion);
            command.Parameters.AddWithValue("version", participation.Version);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new DBConcurrencyException("Participation version changed before the transaction could save it.");
            }
        }

        public void SaveConflict(DeviceConflict conflict)
        {
            using var command = CreateCommand("""
                INSERT INTO exam.participation_device_conflicts
                    (participation_id, original_device_id, pending_device_id, detected_at)
                VALUES
                    (@participation_id, @original_device_id, @pending_device_id, @detected_at)
                ON CONFLICT (participation_id) DO UPDATE SET
                    original_device_id = EXCLUDED.original_device_id,
                    pending_device_id = EXCLUDED.pending_device_id,
                    detected_at = EXCLUDED.detected_at;
                """);
            command.Parameters.AddWithValue("participation_id", conflict.ParticipationId);
            command.Parameters.AddWithValue("original_device_id", conflict.OriginalDeviceId);
            command.Parameters.AddWithValue("pending_device_id", conflict.PendingDeviceId);
            command.Parameters.AddWithValue("detected_at", conflict.DetectedAt.ToUniversalTime());
            command.ExecuteNonQuery();
        }

        public void RemoveConflict(string participationId)
        {
            using var command = CreateCommand("""
                DELETE FROM exam.participation_device_conflicts
                 WHERE participation_id = @participation_id;
                """);
            command.Parameters.AddWithValue("participation_id", participationId);
            command.ExecuteNonQuery();
        }

        public void SaveDecisionAudit(DeviceDecisionAudit audit)
        {
            using var command = CreateCommand("""
                INSERT INTO exam.device_decision_audits
                    (decision_id, participation_id, selected_device_id, revoked_device_id,
                     authorization_version, operator_id, reason, decided_at)
                VALUES
                    (@decision_id, @participation_id, @selected_device_id, @revoked_device_id,
                     @authorization_version, @operator_id, @reason, @decided_at);
                """);
            command.Parameters.AddWithValue("decision_id", audit.DecisionId);
            command.Parameters.AddWithValue("participation_id", audit.ParticipationId);
            command.Parameters.AddWithValue("selected_device_id", audit.SelectedDeviceId);
            command.Parameters.AddWithValue("revoked_device_id", audit.RevokedDeviceId);
            command.Parameters.AddWithValue("authorization_version", audit.AuthorizationVersion);
            command.Parameters.AddWithValue("operator_id", audit.OperatorId);
            command.Parameters.AddWithValue("reason", audit.Reason);
            command.Parameters.AddWithValue("decided_at", audit.DecidedAt.ToUniversalTime());
            command.ExecuteNonQuery();
        }

        public void SaveIdempotencyRecord(ParticipationIdempotencyRecord record)
        {
            using var command = CreateCommand("""
                INSERT INTO exam.participation_idempotency
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

        private const string ParticipationSelect = """
            SELECT participation_id, exam_id, candidate_id, entry_version,
                   business_state, authorized_device_id, authorization_version, version
              FROM exam.participations
            """;

        private NpgsqlCommand CreateCommand(string commandText) => new(commandText, connection, transaction);

        private void LockLogicalKey(string key)
        {
            using var command = CreateCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));");
            command.Parameters.AddWithValue("key", key);
            command.ExecuteNonQuery();
        }

        private void LockLogicalKeyShared(string key)
        {
            using var command = CreateCommand("SELECT pg_advisory_xact_lock_shared(hashtextextended(@key, 0));");
            command.Parameters.AddWithValue("key", key);
            command.ExecuteNonQuery();
        }

        private static StudentSession ReadSession(NpgsqlDataReader reader)
        {
            var candidate = new CandidateIdentity(
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5));
            var candidateIds = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(7))
                ?? throw new DataException("candidate_ids_by_exam is invalid.");
            return new StudentSession(
                reader.GetString(0),
                reader.GetString(1),
                candidate,
                reader.GetFieldValue<string[]>(6),
                reader.GetFieldValue<DateTimeOffset>(8),
                candidateIds);
        }

        private static EntryPreparation ReadPreparation(NpgsqlDataReader reader) => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetFieldValue<DateTimeOffset>(7));

        private static ParticipationRecord ReadParticipation(NpgsqlDataReader reader) => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            ParseBusinessState(reader.GetString(4)),
            reader.GetString(5),
            reader.GetInt64(6),
            reader.GetInt64(7));

        private static string ToDatabaseState(ParticipationBusinessState state) =>
            state switch
            {
                ParticipationBusinessState.NotParticipating => "not_participating",
                ParticipationBusinessState.Waiting => "waiting",
                ParticipationBusinessState.Answering => "answering",
                ParticipationBusinessState.Submitted => "submitted",
                _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
            };

        private static string ToDatabaseLifecycle(ExamLifecycle lifecycle) =>
            lifecycle switch
            {
                ExamLifecycle.Scheduled => "scheduled",
                ExamLifecycle.Running => "running",
                ExamLifecycle.Ended => "ended",
                _ => throw new ArgumentOutOfRangeException(nameof(lifecycle), lifecycle, null)
            };

        private static ParticipationBusinessState ParseBusinessState(string value) =>
            value switch
            {
                "not_participating" => ParticipationBusinessState.NotParticipating,
                "waiting" => ParticipationBusinessState.Waiting,
                "answering" => ParticipationBusinessState.Answering,
                "submitted" => ParticipationBusinessState.Submitted,
                _ => throw new DataException($"Unknown participation state '{value}'.")
            };
    }
}
