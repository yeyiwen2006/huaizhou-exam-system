using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.Resources;
using Exam.Contracts.ServiceManagement;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Domain.Submissions;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;
using Npgsql;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>Read-only recovery checks against one consistent database snapshot. Never repairs or migrates data.</summary>
public sealed class PostgresRecoveryReadinessProbe(NpgsqlDataSource source, IPackageKeyUnwrapper unwrapper) : IStartupReadinessProbe
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();
    private static readonly string[] Names = ["activation-sequences", "version-projections", "published-resources", "submission-receipts", "reliable-events"];

    public async Task<IReadOnlyList<StartupCheckResult>> RunAsync(ServiceConfiguration configuration, CancellationToken token)
    {
        var checks = new List<StartupCheckResult>();
        try
        {
            await using var connection = await source.OpenConnectionAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            // A filtered read can hide corrupt rows. This does not bypass RLS: PostgreSQL rejects
            // a query whose results would be affected by a policy, keeping the write gate closed.
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL row_security=off", connection, transaction))
                await readOnly.ExecuteNonQueryAsync(token);
            await AddAsync(Names[0], async () => await NoRowsAsync("""
                SELECT 1 FROM exam.activation_devices d
                LEFT JOIN exam.activation_daily_sequences s ON s.activation_date = d.activation_date
                WHERE left(d.client_number, 8) <> to_char(d.activation_date, 'YYYYMMDD')
                   OR right(d.client_number, 3)::integer < 1
                   OR s.last_sequence IS NULL OR s.last_sequence < right(d.client_number, 3)::integer
                """, connection, transaction, token));
            await AddAsync(Names[1], () => CheckVersionsAsync(connection, transaction, token));
            await AddAsync(Names[2], () => CheckResourcesAsync(connection, transaction, token));
            await AddAsync(Names[3], () => CheckReceiptsAsync(connection, transaction, token));
            await AddAsync(Names[4], async () => await NoRowsAsync("""
                SELECT 1 FROM exam.command_events e
                LEFT JOIN exam.participations p ON p.participation_id = e.participation_id
                WHERE (e.event_type <> 'CollectionCompleted' AND (p.participation_id IS NULL OR p.exam_id <> e.exam_id))
                   OR (e.event_type = 'Start' AND NOT EXISTS (
                     SELECT 1 FROM exam.versioned_resources r WHERE r.exam_id = e.exam_id
                       AND r.resource_kind = 'paper-package' AND r.resource_version = e.resource_version
                       AND e.protected_unlock_reference = 'resource-envelope:' || r.resource_id))
                   OR (e.acknowledgement_request_id IS NOT NULL AND NOT EXISTS (
                     SELECT 1 FROM exam.command_acknowledgement_requests a
                      WHERE a.event_id = e.event_id AND a.result_request_id = e.acknowledgement_request_id
                        AND a.processing_result = e.acknowledgement_result AND a.acknowledged_at = e.acknowledged_at))
                """, connection, transaction, token));
            await transaction.CommitAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // An SQL error may abort the snapshot transaction. Every uncompleted check must remain blocked.
            foreach (var name in Names.Except(checks.Select(c => c.Name))) checks.Add(new(name, false, "恢复核对依赖不可用或持久数据无法读取。", true));
            if (checks.All(c => c.Passed)) checks.Add(new("recovery-snapshot", false, "恢复核对的数据库观察未能完成。", true));
        }
        return checks;

        async Task AddAsync(string name, Func<Task<bool>> inspect)
        {
            var passed = await inspect();
            checks.Add(new(name, passed, passed ? "持久数据与恢复依赖核对通过。" : "持久数据或恢复依赖不一致，业务写入保持关闭。", true));
        }
    }

    private static async Task<bool> CheckVersionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        if (!await NoRowsAsync("""
            SELECT 1 FROM exam.version_streams s LEFT JOIN exam.version_state v USING (exam_id)
             WHERE v.exam_id IS NULL OR s.stream_revision <> v.stream_revision
                OR (SELECT count(*) FROM exam.version_events e WHERE e.exam_id = s.exam_id) <> s.stream_revision
                OR (SELECT max(stream_revision) FROM exam.version_events e WHERE e.exam_id = s.exam_id) <> s.stream_revision
            UNION ALL SELECT 1 FROM exam.version_events e
             WHERE e.payload->>'examId' IS DISTINCT FROM e.exam_id
                OR e.operation_type NOT IN ('exam.create', 'schedule.update', 'schedule.trigger', 'paper.publish', 'grading.publish',
                     'participant.register', 'participant.change-state', 'paper.report-transfer', 'paper.confirm-review')
            UNION ALL SELECT 1 FROM exam.participations p
             WHERE p.business_state IN ('waiting', 'answering') AND NOT EXISTS (SELECT 1 FROM exam.version_state v WHERE v.exam_id = p.exam_id)
            UNION ALL SELECT 1 FROM exam.version_state v LEFT JOIN exam.exam_entry_access a USING (exam_id)
             WHERE a.exam_id IS NULL OR a.scheduled_start <> v.starts_at OR a.scheduled_end <> v.ends_at
                OR a.lifecycle <> lower(v.lifecycle)
            """, connection, transaction, token)) return false;
        await using var command = new NpgsqlCommand("""
            SELECT exam_id, schedule_version, paper_version, grading_version, starts_at, ends_at, lifecycle, snapshot::text
              FROM exam.version_state
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var snapshot = JsonSerializer.Deserialize<ExamVersionSnapshot>(reader.GetString(7), Json);
            if (snapshot is null || snapshot.Versions is null || snapshot.Schedule is null || snapshot.Participants is null ||
                snapshot.ExamId != reader.GetString(0) || snapshot.Versions != new VersionVector(reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)) ||
                PostgreSqlTimestampTicks(snapshot.Schedule.StartsAt) != reader.GetFieldValue<DateTimeOffset>(4).UtcTicks ||
                PostgreSqlTimestampTicks(snapshot.Schedule.EndsAt) != reader.GetFieldValue<DateTimeOffset>(5).UtcTicks ||
                snapshot.LifecycleState.ToString() != reader.GetString(6) ||
                (snapshot.Versions.PaperVersion > 0 && (snapshot.Paper is null || snapshot.Paper.Version != snapshot.Versions.PaperVersion)) ||
                (snapshot.Versions.GradingVersion > 0 && (snapshot.Grading is null || snapshot.Grading.Version != snapshot.Versions.GradingVersion))) return false;
        }
        return true;
    }

    private static long PostgreSqlTimestampTicks(DateTimeOffset value) => value.UtcTicks / 10 * 10;

    private async Task<bool> CheckResourcesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        if (!await NoRowsAsync("""
            SELECT 1 FROM exam.version_state v
             WHERE v.paper_version > 0 AND NOT EXISTS (
               SELECT 1 FROM exam.versioned_resources r WHERE r.exam_id = v.exam_id AND r.resource_kind = 'paper-package'
                AND r.resource_version = v.paper_version AND r.resource_id = v.snapshot->'paper'->>'artifactId')
            UNION ALL SELECT 1 FROM exam.exam_entry_access a
             WHERE a.package_ready AND NOT EXISTS (SELECT 1 FROM exam.versioned_resources r
               WHERE r.exam_id = a.exam_id AND r.resource_kind = 'paper-package' AND r.resource_version = a.paper_version
                 AND a.unlock_reference = 'resource-envelope:' || r.resource_id AND a.package_length = octet_length(r.encrypted_content))
            """, connection, transaction, token)) return false;
        var published = new Dictionary<(string ExamId, long Version), PaperStructure>();
        await using (var projections = new NpgsqlCommand("SELECT snapshot::text FROM exam.version_state WHERE paper_version > 0", connection, transaction))
        await using (var states = await projections.ExecuteReaderAsync(token))
            while (await states.ReadAsync(token))
            {
                var snapshot = JsonSerializer.Deserialize<ExamVersionSnapshot>(states.GetString(0), Json);
                if (snapshot?.Paper?.Structure is null) return false;
                published.Add((snapshot.ExamId, snapshot.Paper.Version), snapshot.Paper.Structure);
            }
        await using var command = new NpgsqlCommand("""
            SELECT resource_id, exam_id, resource_version, encryption_envelope::text, encrypted_content
              FROM exam.versioned_resources WHERE resource_kind = 'paper-package'
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        while (await reader.ReadAsync(token))
        {
            var id = reader.GetString(0);
            var examId = reader.GetString(1);
            var version = reader.GetInt64(2);
            var envelope = JsonSerializer.Deserialize<EncryptedPackageEnvelope>(reader.GetString(3), Json);
            if (envelope is null || envelope.PackageId != id || envelope.PaperVersion != version) return false;
            using var material = new PackageUnlockMaterial(id, version, envelope.ChunkSize, envelope.NoncePrefix, unwrapper.Unwrap(envelope));
            await using var ciphertext = reader.GetStream(4);
            // Use the student reader's existing ZIP/manifest/display checks. Plaintext stays bounded in memory.
            using var plaintext = new BoundedPackageBuffer();
            await PackageCiphertext.DecryptAsync(ciphertext, plaintext, material, token);
            var bytes = plaintext.ToArray();
            try
            {
                var manifest = StudentPaperPackage.ReadManifest(bytes, examId, version, token);
                if (published.TryGetValue((examId, version), out var expected) &&
                    !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(manifest.Structure, Json), JsonSerializer.SerializeToNode(expected, Json)))
                    return false;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        return true;
    }

    private sealed class BoundedPackageBuffer : MemoryStream
    {
        private void RequireCapacity(long length)
        {
            if (length < 0 || length > StudentPaperPackage.MaxPackageBytes)
                throw new InvalidDataException("恢复题包超出共享容量限制。");
            if (length > Capacity)
            {
                TryGetBuffer(out var previous);
                Capacity = (int)Math.Min(StudentPaperPackage.MaxPackageBytes, Math.Max(length, Math.Max(4096L, Capacity * 2L)));
                // MemoryStream growth copies the buffer; erase the retired plaintext allocation as well.
                if (previous.Array is not null) CryptographicOperations.ZeroMemory(previous.Array);
            }
        }
        public override void SetLength(long value) { RequireCapacity(value); base.SetLength(value); }
        public override void Write(byte[] buffer, int offset, int count) { RequireCapacity(Position + count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { RequireCapacity(Position + buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { RequireCapacity(Position + 1); base.WriteByte(value); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        { RequireCapacity(Position + count); return base.WriteAsync(buffer, offset, count, token); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { RequireCapacity(Position + buffer.Length); return base.WriteAsync(buffer, token); }
        protected override void Dispose(bool disposing)
        {
            if (disposing && TryGetBuffer(out var buffer)) CryptographicOperations.ZeroMemory(buffer.Array!);
            base.Dispose(disposing);
        }
    }

    private static async Task<bool> CheckReceiptsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        if (!await NoRowsAsync("""
            SELECT 1 FROM exam.participations p LEFT JOIN exam.final_submissions f USING (participation_id)
             WHERE (p.business_state = 'submitted') <> (f.submission_id IS NOT NULL)
                OR (f.submission_id IS NOT NULL AND (p.exam_id <> f.exam_id OR p.candidate_id <> f.candidate_id))
            UNION ALL SELECT 1 FROM exam.final_submissions f
             WHERE NOT EXISTS (SELECT 1 FROM exam.grading_outbox g WHERE g.submission_id = f.submission_id AND g.grading_version = f.grading_version)
            """, connection, transaction, token)) return false;
        var submissions = new Dictionary<string, FinalSubmission>(StringComparer.Ordinal);
        await using (var command = new NpgsqlCommand("""
            SELECT submission_id, exam_id, candidate_id, participation_id, device_id, authorization_version, paper_version,
                   frozen_answers::text, received_at, receipt_id, grading_version FROM exam.final_submissions
            """, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                var answers = JsonSerializer.Deserialize<Dictionary<string, AnswerValue>>(reader.GetString(7), Json);
                if (answers is null) return false;
                var receipt = new SubmissionReceipt(reader.GetString(9), reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(8), reader.GetInt64(6), reader.GetInt64(10));
                submissions.Add(reader.GetString(0), new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6), answers, receipt.ReceivedAt, receipt));
            }
        var evidenced = new HashSet<string>(StringComparer.Ordinal);
        await using var replayCommand = new NpgsqlCommand("SELECT exam_id, candidate_id, participation_id, result_payload::text FROM exam.submission_idempotency", connection, transaction);
        await using var replayReader = await replayCommand.ExecuteReaderAsync(token);
        while (await replayReader.ReadAsync(token))
        {
            var replay = JsonSerializer.Deserialize<SubmissionPersistenceResult>(replayReader.GetString(3), Json);
            if (replay is null || !Enum.IsDefined(replay.Status)) return false;
            if (replay.Status is not (SubmissionPersistenceStatus.Saved or SubmissionPersistenceStatus.RequestReplay or SubmissionPersistenceStatus.ExistingSubmission)) continue;
            var saved = replay.Submission;
            if (saved is null || saved.ExamId != replayReader.GetString(0) || saved.CandidateId != replayReader.GetString(1) ||
                saved.ParticipationId != replayReader.GetString(2) || !submissions.TryGetValue(saved.SubmissionId, out var canonical) ||
                !JsonNode.DeepEquals(JsonSerializer.SerializeToNode(saved, Json), JsonSerializer.SerializeToNode(canonical, Json))) return false;
            evidenced.Add(saved.SubmissionId);
        }
        return submissions.Keys.All(evidenced.Contains);
    }

    private static async Task<bool> NoRowsAsync(string sql, NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SELECT NOT EXISTS ({sql})", connection, transaction);
        return (bool)(await command.ExecuteScalarAsync(token) ?? false);
    }
}
