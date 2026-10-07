using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Exam.Application.Participation;
using Exam.Contracts.Participation;
using Exam.Contracts.Student;
using Exam.Infrastructure.Postgres.Resources;
using Npgsql;

namespace Exam.Infrastructure.Postgres.Student;

public sealed class PostgresStudentExamMetadataService(NpgsqlDataSource dataSource)
{
    public async ValueTask<StudentExamMetadataResult> UpdateAsync(
        string examId,
        UpdateStudentExamMetadataRequest request,
        string authenticatedOperatorId,
        CancellationToken cancellationToken = default)
    {
        Validate(examId, request, authenticatedOperatorId);
        var payloadHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            ExamId = examId,
            ActorId = authenticatedOperatorId,
            Request = request
        })));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        // Only this operation uses the metadata request lock; locking the exam next
        // serializes metadata readiness with versioning publication and start/end transitions.
        await using (var requestLock = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));", connection, transaction))
        {
            requestLock.Parameters.AddWithValue("key", $"student-metadata-request:{request.RequestId}");
            await requestLock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var previous = await FindReplayAsync(
            connection, transaction, request.RequestId, payloadHash, cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return previous;
        }

        long currentInstructionsVersion;
        bool instructionsReady;
        await using (var currentExam = new NpgsqlCommand("""
                         SELECT lifecycle, instructions_version, instructions_ready
                           FROM exam.exam_entry_access WHERE exam_id = @exam_id FOR UPDATE;
                         """, connection, transaction))
        {
            currentExam.Parameters.AddWithValue("exam_id", examId);
            await using var reader = await currentExam.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw Rule(ParticipationErrorCodes.ExamNotFound, "找不到考试。");
            }

            if (reader.GetString(0) != "scheduled")
            {
                throw Rule(ParticipationErrorCodes.InvalidBusinessState,
                    "考试已开始或结束，不能通过初始配置接口修改考试说明。");
            }

            currentInstructionsVersion = reader.GetInt64(1);
            instructionsReady = reader.GetBoolean(2);
        }

        long currentVersion;
        await using (var metadata = new NpgsqlCommand(
                         "SELECT version FROM exam.student_exam_metadata WHERE exam_id = @exam_id;",
                         connection, transaction))
        {
            metadata.Parameters.AddWithValue("exam_id", examId);
            currentVersion = await metadata.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long version
                ? version
                : 0;
        }

        if (currentVersion != request.ExpectedVersion)
        {
            throw Rule(ParticipationErrorCodes.VersionConflict, "考试公开配置已变化，请读取最新版本后重试。");
        }

        // shared contract-versioning start with an unavailable instruction version of 1. The first real
        // document fills that version; subsequent publications always advance it.
        var instructionsVersion = instructionsReady
            ? checked(currentInstructionsVersion + 1)
            : currentInstructionsVersion;
        var result = new StudentExamMetadataResult(examId, checked(currentVersion + 1), instructionsVersion);
        await using (var save = new NpgsqlCommand("""
                         INSERT INTO exam.student_exam_metadata
                             (exam_id, display_name, category_name, subjects, version, updated_at, updated_by)
                         VALUES
                             (@exam_id, @name, @category, CAST(@subjects AS jsonb), @version,
                              clock_timestamp(), @actor)
                         ON CONFLICT (exam_id) DO UPDATE SET
                             display_name = EXCLUDED.display_name,
                             category_name = EXCLUDED.category_name,
                             subjects = EXCLUDED.subjects,
                             version = EXCLUDED.version,
                             updated_at = EXCLUDED.updated_at,
                             updated_by = EXCLUDED.updated_by;

                         INSERT INTO exam.student_exam_instructions
                             (exam_id, instructions_version, lines, created_at, created_by)
                         VALUES (@exam_id, @instructions_version, CAST(@lines AS jsonb), clock_timestamp(), @actor);

                         UPDATE exam.exam_entry_access
                            SET instructions_ready = true, instructions_version = @instructions_version
                          WHERE exam_id = @exam_id;

                         INSERT INTO exam.student_metadata_idempotency (request_id, payload_hash, response_json)
                         VALUES (@request_id, @payload_hash, CAST(@response AS jsonb));
                         """, connection, transaction))
        {
            save.Parameters.AddWithValue("exam_id", examId);
            save.Parameters.AddWithValue("name", request.Name.Trim());
            save.Parameters.AddWithValue("category", string.IsNullOrWhiteSpace(request.CategoryName)
                ? DBNull.Value : request.CategoryName.Trim());
            save.Parameters.AddWithValue("subjects", JsonSerializer.Serialize(request.Subjects));
            save.Parameters.AddWithValue("version", result.Version);
            save.Parameters.AddWithValue("actor", authenticatedOperatorId);
            save.Parameters.AddWithValue("instructions_version", instructionsVersion);
            save.Parameters.AddWithValue("lines", JsonSerializer.Serialize(request.InstructionLines));
            save.Parameters.AddWithValue("request_id", request.RequestId);
            save.Parameters.AddWithValue("payload_hash", payloadHash);
            save.Parameters.AddWithValue("response", JsonSerializer.Serialize(result));
            await save.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<InstructionsDocument> GetInstructionsAsync(
        string preparationId,
        string sessionId,
        string authenticatedDeviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(preparationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedDeviceId);
        const string sql = PreparedEntryAccessSql.CurrentPreparationsCte + """
            SELECT instructions.instructions_version, instructions.lines::text
              FROM authorized_preparations preparation
              JOIN exam.student_exam_instructions instructions
                ON instructions.exam_id = preparation.exam_id
               AND instructions.instructions_version = preparation.instructions_version
             WHERE preparation.preparation_id = $1
               AND preparation.session_id = $2
               AND preparation.device_id = $3;
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(preparationId);
        command.Parameters.AddWithValue(sessionId);
        command.Parameters.AddWithValue(authenticatedDeviceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw Rule(ParticipationErrorCodes.ResourcesNotReady, "当前设备的考试说明不可用，请重新获取进入资料。");
        }

        return new InstructionsDocument(reader.GetInt64(0),
            JsonSerializer.Deserialize<string[]>(reader.GetString(1))
                ?? throw new DataException("考试说明数据无效。"));
    }

    private static async Task<StudentExamMetadataResult?> FindReplayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string requestId,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT payload_hash, response_json::text FROM exam.student_metadata_idempotency
             WHERE request_id = @request_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("request_id", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (!string.Equals(reader.GetString(0), payloadHash, StringComparison.Ordinal))
        {
            throw Rule(ParticipationErrorCodes.IdempotencyConflict, "requestId 已用于不同的考试公开配置请求。");
        }

        return JsonSerializer.Deserialize<StudentExamMetadataResult>(reader.GetString(1))
            ?? throw new DataException("无法读取考试公开配置的已保存回执。");
    }

    private static void Validate(
        string examId,
        UpdateStudentExamMetadataRequest request,
        string authenticatedOperatorId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedOperatorId);
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.ExpectedVersion < 0 ||
            string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 256 ||
            request.CategoryName is { Length: > 256 } ||
            request.Subjects is null || request.Subjects.Count is < 1 or > 100 ||
            request.Subjects.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256) ||
            request.InstructionLines is null || request.InstructionLines.Count is < 1 or > 2000 ||
            request.InstructionLines.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 16000))
        {
            throw Rule(ParticipationErrorCodes.InvalidRequest, "考试名称、科目、说明或请求版本无效。");
        }
    }

    private static ParticipationRuleException Rule(string code, string message) => new(code, message);
}
