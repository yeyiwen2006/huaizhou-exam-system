using System.Data;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Domain.Submissions;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Submissions;

/// <summary>
/// PostgreSQL submission store. Authorization checks are performed while holding a row lock on
/// the canonical exam.participations row. Final answers, receipt, terminal participation state,
/// idempotency response and grading outbox entry commit in one transaction.
/// </summary>
public sealed class PostgresSubmissionStore : ISubmissionStore
{
    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Create();
    private readonly NpgsqlDataSource _dataSource;

    public PostgresSubmissionStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    private static DateTimeOffset CanonicalizeTimestamp(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        return new DateTimeOffset(
            utc.Ticks - utc.Ticks % TimeSpan.TicksPerMicrosecond,
            TimeSpan.Zero);
    }

    public async ValueTask SetAuthorizationStateAsync(
        SubmissionAuthorizationState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        const string sql = """
            INSERT INTO exam.participations
                (participation_id, exam_id, candidate_id, entry_version, business_state,
                 authorized_device_id, authorization_version, version,
                 current_paper_version, current_grading_version)
            VALUES
                (@participation_id, @exam_id, @candidate_id, @entry_version, @business_state,
                 @authorized_device_id, @authorization_version, 1,
                 @current_paper_version, @current_grading_version)
            ON CONFLICT (participation_id) DO UPDATE
                SET exam_id = EXCLUDED.exam_id,
                    candidate_id = EXCLUDED.candidate_id,
                    entry_version = EXCLUDED.entry_version,
                    authorized_device_id = EXCLUDED.authorized_device_id,
                    authorization_version = EXCLUDED.authorization_version,
                    business_state = CASE
                        WHEN exam.participations.business_state = 'submitted' THEN 'submitted'
                        ELSE EXCLUDED.business_state
                    END,
                    current_paper_version = EXCLUDED.current_paper_version,
                    current_grading_version = EXCLUDED.current_grading_version,
                    version = exam.participations.version + 1;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        AddAuthorizationParameters(command, state);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async ValueTask<ProgressPersistenceResult> SaveProgressAsync(
        SyncProgressRequest request,
        DateTimeOffset persistedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        persistedAt = CanonicalizeTimestamp(persistedAt);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await AcquireAdvisoryLockAsync(
            connection,
            transaction,
            $"progress-request:{request.RequestId}",
            cancellationToken);

        var replay = await FindIdempotencyAsync<ProgressPersistenceResult>(
            connection,
            transaction,
            "exam.progress_idempotency",
            request.RequestId,
            cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            if (!Matches(replay.Value.Identity, request))
            {
                return new ProgressPersistenceResult(ProgressPersistenceStatus.AuthorizationRevoked, null);
            }

            return replay.Value.Result.Status is ProgressPersistenceStatus.Saved or
                ProgressPersistenceStatus.RequestReplay
                ? replay.Value.Result with { Status = ProgressPersistenceStatus.RequestReplay }
                : replay.Value.Result;
        }

        var authorization = await FindAuthorizationAsync(
            connection,
            transaction,
            request.ParticipationId,
            cancellationToken);
        ProgressPersistenceResult result;
        if (authorization is null)
        {
            result = new ProgressPersistenceResult(ProgressPersistenceStatus.ParticipationNotFound, null);
        }
        else if (!IsAuthorized(request, authorization))
        {
            result = new ProgressPersistenceResult(ProgressPersistenceStatus.AuthorizationRevoked, null);
        }
        else if (authorization.BusinessState == "submitted" ||
                 await SubmissionExistsAsync(
                     connection,
                     transaction,
                     request.ParticipationId,
                     cancellationToken))
        {
            var current = await FindProgressAsync(
                connection,
                transaction,
                request.ParticipationId,
                true,
                cancellationToken);
            result = new ProgressPersistenceResult(ProgressPersistenceStatus.AlreadySubmitted, current);
        }
        else if (authorization.BusinessState != "answering")
        {
            result = new ProgressPersistenceResult(ProgressPersistenceStatus.InvalidState, null);
        }
        else if (request.PaperVersion > authorization.CurrentPaperVersion)
        {
            var current = await FindProgressAsync(
                connection,
                transaction,
                request.ParticipationId,
                true,
                cancellationToken);
            result = new ProgressPersistenceResult(ProgressPersistenceStatus.VersionConflict, current);
        }
        else
        {
            var current = await FindProgressAsync(
                connection,
                transaction,
                request.ParticipationId,
                true,
                cancellationToken);
            if (current is not null && request.AnswerVersion <= current.AnswerVersion)
            {
                result = new ProgressPersistenceResult(ProgressPersistenceStatus.VersionConflict, current);
            }
            else
            {
                var progress = new PersistedProgress(
                    request.ExamId,
                    request.CandidateId,
                    request.ParticipationId,
                    request.EntryVersion,
                    request.DeviceId,
                    request.AuthorizationVersion,
                    request.AnswerVersion,
                    request.PaperVersion,
                    PersistedProgress.FreezeAnswers(request.Answers),
                    request.CurrentQuestionId,
                    persistedAt);
                await UpsertProgressAsync(connection, transaction, progress, cancellationToken);
                result = new ProgressPersistenceResult(ProgressPersistenceStatus.Saved, progress);
            }
        }

        await InsertIdempotencyAsync(
            connection,
            transaction,
            "exam.progress_idempotency",
            request.RequestId,
            IdentityOf(request),
            result,
            persistedAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async ValueTask<SubmissionPersistenceResult> SaveSubmissionAsync(
        SubmitFinalAnswersRequest request,
        DateTimeOffset persistedAt,
        string receiptId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        persistedAt = CanonicalizeTimestamp(persistedAt);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await AcquireAdvisoryLockAsync(
            connection,
            transaction,
            $"submission-request:{request.RequestId}",
            cancellationToken);

        var replay = await FindIdempotencyAsync<SubmissionPersistenceResult>(
            connection,
            transaction,
            "exam.submission_idempotency",
            request.RequestId,
            cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            if (!Matches(replay.Value.Identity, request))
            {
                return new SubmissionPersistenceResult(
                    SubmissionPersistenceStatus.AuthorizationRevoked,
                    null);
            }

            return replay.Value.Result.Status is SubmissionPersistenceStatus.Saved or
                SubmissionPersistenceStatus.ExistingSubmission or
                SubmissionPersistenceStatus.RequestReplay
                ? replay.Value.Result with { Status = SubmissionPersistenceStatus.RequestReplay }
                : replay.Value.Result;
        }

        // These locks cover races where different request ids target the same immutable
        // submission id or the same exam/candidate unique key.
        await AcquireAdvisoryLockAsync(
            connection,
            transaction,
            $"submission-id:{request.SubmissionId}",
            cancellationToken);
        await AcquireAdvisoryLockAsync(
            connection,
            transaction,
            $"submission-candidate:{request.ExamId}\u001f{request.CandidateId}",
            cancellationToken);

        var authorization = await FindAuthorizationAsync(
            connection,
            transaction,
            request.ParticipationId,
            cancellationToken);
        SubmissionPersistenceResult result;
        if (authorization is null)
        {
            result = new SubmissionPersistenceResult(
                SubmissionPersistenceStatus.ParticipationNotFound,
                null);
        }
        else if (!IsAuthorized(request, authorization))
        {
            result = new SubmissionPersistenceResult(
                SubmissionPersistenceStatus.AuthorizationRevoked,
                null);
        }
        else
        {
            var sameId = await FindSubmissionByIdAsync(
                connection,
                transaction,
                request.SubmissionId,
                cancellationToken);
            if (sameId is not null &&
                (!string.Equals(sameId.ExamId, request.ExamId, StringComparison.Ordinal) ||
                 !string.Equals(sameId.CandidateId, request.CandidateId, StringComparison.Ordinal)))
            {
                result = new SubmissionPersistenceResult(
                    SubmissionPersistenceStatus.SubmissionIdConflict,
                    null);
            }
            else
            {
                var existing = await FindSubmissionAsync(
                    connection,
                    transaction,
                    request.ExamId,
                    request.CandidateId,
                    cancellationToken);
                if (existing is not null)
                {
                    result = new SubmissionPersistenceResult(
                        SubmissionPersistenceStatus.ExistingSubmission,
                        existing);
                }
                else if (authorization.BusinessState != "answering")
                {
                    result = new SubmissionPersistenceResult(
                        SubmissionPersistenceStatus.InvalidState,
                        null);
                }
                else if (request.PaperVersion > authorization.CurrentPaperVersion)
                {
                    result = new SubmissionPersistenceResult(
                        SubmissionPersistenceStatus.VersionConflict,
                        null);
                }
                else
                {
                    var receipt = new SubmissionReceipt(
                        receiptId,
                        request.SubmissionId,
                        request.ExamId,
                        request.CandidateId,
                        request.ParticipationId,
                        persistedAt,
                        request.PaperVersion,
                        authorization.CurrentGradingVersion);
                    var submission = new FinalSubmission(
                        request.SubmissionId,
                        request.ExamId,
                        request.CandidateId,
                        request.ParticipationId,
                        request.DeviceId,
                        request.AuthorizationVersion,
                        request.PaperVersion,
                        PersistedProgress.FreezeAnswers(request.FrozenAnswers),
                        persistedAt,
                        receipt);

                    await InsertSubmissionAsync(connection, transaction, submission, cancellationToken);
                    await MarkParticipationSubmittedAsync(
                        connection,
                        transaction,
                        request.ParticipationId,
                        cancellationToken);
                    await InsertGradingOutboxAsync(
                        connection,
                        transaction,
                        submission.SubmissionId,
                        receipt.GradingVersion,
                        persistedAt,
                        cancellationToken);
                    result = new SubmissionPersistenceResult(SubmissionPersistenceStatus.Saved, submission);
                }
            }
        }

        await InsertIdempotencyAsync(
            connection,
            transaction,
            "exam.submission_idempotency",
            request.RequestId,
            IdentityOf(request),
            result,
            persistedAt,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async ValueTask<PersistedProgress?> FindProgressAsync(
        string participationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await FindProgressAsync(connection, null, participationId, false, cancellationToken);
    }

    public async ValueTask<FinalSubmission?> FindSubmissionAsync(
        string examId,
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        return await FindSubmissionAsync(connection, null, examId, candidateId, cancellationToken);
    }

    public async ValueTask<FinalSubmission?> FindSubmissionByParticipationAsync(
        string participationId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT submission_id, exam_id, candidate_id, participation_id, device_id,
                   authorization_version, paper_version, frozen_answers, received_at,
                   receipt_id, grading_version
              FROM exam.final_submissions
             WHERE participation_id = @participation_id;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("participation_id", NpgsqlDbType.Text, participationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSubmission(reader) : null;
    }

    private static async ValueTask<AuthorizationRow?> FindAuthorizationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string participationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT exam_id, candidate_id, participation_id, entry_version,
                   authorized_device_id, authorization_version, business_state,
                   current_paper_version, current_grading_version
              FROM exam.participations
             WHERE participation_id = @participation_id
             FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("participation_id", NpgsqlDbType.Text, participationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AuthorizationRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetInt64(8));
    }

    private static async ValueTask<PersistedProgress?> FindProgressAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string participationId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var sql = """
            SELECT exam_id, candidate_id, participation_id, entry_version, device_id,
                   authorization_version, answer_version, paper_version, answers,
                   current_question_id, last_synced_at
              FROM exam.participation_progress
             WHERE participation_id = @participation_id
            """ + (forUpdate ? " FOR UPDATE;" : ";");
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("participation_id", NpgsqlDbType.Text, participationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProgress(reader) : null;
    }

    private static async ValueTask<FinalSubmission?> FindSubmissionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string examId,
        string candidateId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT submission_id, exam_id, candidate_id, participation_id, device_id,
                   authorization_version, paper_version, frozen_answers, received_at,
                   receipt_id, grading_version
              FROM exam.final_submissions
             WHERE exam_id = @exam_id AND candidate_id = @candidate_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("exam_id", NpgsqlDbType.Text, examId);
        command.Parameters.AddWithValue("candidate_id", NpgsqlDbType.Text, candidateId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSubmission(reader) : null;
    }

    private static async ValueTask<FinalSubmission?> FindSubmissionByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string submissionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT submission_id, exam_id, candidate_id, participation_id, device_id,
                   authorization_version, paper_version, frozen_answers, received_at,
                   receipt_id, grading_version
              FROM exam.final_submissions
             WHERE submission_id = @submission_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Text, submissionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSubmission(reader) : null;
    }

    private static async ValueTask<bool> SubmissionExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string participationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EXISTS
            (
                SELECT 1 FROM exam.final_submissions WHERE participation_id = @participation_id
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("participation_id", NpgsqlDbType.Text, participationId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    private static async ValueTask UpsertProgressAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PersistedProgress progress,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO exam.participation_progress
                (participation_id, exam_id, candidate_id, entry_version, device_id,
                 authorization_version, answer_version, paper_version, answers,
                 current_question_id, last_synced_at)
            VALUES
                (@participation_id, @exam_id, @candidate_id, @entry_version, @device_id,
                 @authorization_version, @answer_version, @paper_version, @answers,
                 @current_question_id, @last_synced_at)
            ON CONFLICT (participation_id) DO UPDATE
                SET exam_id = EXCLUDED.exam_id,
                    candidate_id = EXCLUDED.candidate_id,
                    entry_version = EXCLUDED.entry_version,
                    device_id = EXCLUDED.device_id,
                    authorization_version = EXCLUDED.authorization_version,
                    answer_version = EXCLUDED.answer_version,
                    paper_version = EXCLUDED.paper_version,
                    answers = EXCLUDED.answers,
                    current_question_id = EXCLUDED.current_question_id,
                    last_synced_at = EXCLUDED.last_synced_at
             WHERE EXCLUDED.answer_version > exam.participation_progress.answer_version;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("participation_id", NpgsqlDbType.Text, progress.ParticipationId);
        command.Parameters.AddWithValue("exam_id", NpgsqlDbType.Text, progress.ExamId);
        command.Parameters.AddWithValue("candidate_id", NpgsqlDbType.Text, progress.CandidateId);
        command.Parameters.AddWithValue("entry_version", NpgsqlDbType.Bigint, progress.EntryVersion);
        command.Parameters.AddWithValue("device_id", NpgsqlDbType.Text, progress.DeviceId);
        command.Parameters.AddWithValue(
            "authorization_version",
            NpgsqlDbType.Bigint,
            progress.AuthorizationVersion);
        command.Parameters.AddWithValue("answer_version", NpgsqlDbType.Bigint, progress.AnswerVersion);
        command.Parameters.AddWithValue("paper_version", NpgsqlDbType.Bigint, progress.PaperVersion);
        command.Parameters.AddWithValue(
            "answers",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(progress.Answers, JsonOptions));
        AddNullable(command, "current_question_id", NpgsqlDbType.Text, progress.CurrentQuestionId);
        command.Parameters.AddWithValue(
            "last_synced_at",
            NpgsqlDbType.TimestampTz,
            progress.LastSyncedAt);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new DBConcurrencyException("答题版本在事务内发生了意外变化。");
        }
    }

    private static async ValueTask InsertSubmissionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        FinalSubmission submission,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO exam.final_submissions
                (submission_id, exam_id, candidate_id, participation_id, device_id,
                 authorization_version, paper_version, frozen_answers, received_at,
                 receipt_id, grading_version)
            VALUES
                (@submission_id, @exam_id, @candidate_id, @participation_id, @device_id,
                 @authorization_version, @paper_version, @frozen_answers, @received_at,
                 @receipt_id, @grading_version);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Text, submission.SubmissionId);
        command.Parameters.AddWithValue("exam_id", NpgsqlDbType.Text, submission.ExamId);
        command.Parameters.AddWithValue("candidate_id", NpgsqlDbType.Text, submission.CandidateId);
        command.Parameters.AddWithValue("participation_id", NpgsqlDbType.Text, submission.ParticipationId);
        command.Parameters.AddWithValue("device_id", NpgsqlDbType.Text, submission.DeviceId);
        command.Parameters.AddWithValue(
            "authorization_version",
            NpgsqlDbType.Bigint,
            submission.AuthorizationVersion);
        command.Parameters.AddWithValue("paper_version", NpgsqlDbType.Bigint, submission.PaperVersion);
        command.Parameters.AddWithValue(
            "frozen_answers",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(submission.FrozenAnswers, JsonOptions));
        command.Parameters.AddWithValue("received_at", NpgsqlDbType.TimestampTz, submission.ReceivedAt);
        command.Parameters.AddWithValue("receipt_id", NpgsqlDbType.Text, submission.Receipt.ReceiptId);
        command.Parameters.AddWithValue(
            "grading_version",
            NpgsqlDbType.Bigint,
            submission.Receipt.GradingVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask MarkParticipationSubmittedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string participationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE exam.participations
               SET business_state = 'submitted', version = version + 1
             WHERE participation_id = @participation_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("participation_id", NpgsqlDbType.Text, participationId);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new DBConcurrencyException("参试记录在交卷事务内消失。");
        }
    }

    private static async ValueTask InsertGradingOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string submissionId,
        long gradingVersion,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO exam.grading_outbox (submission_id, grading_version, created_at)
            VALUES (@submission_id, @grading_version, @created_at)
            ON CONFLICT (submission_id, grading_version) DO NOTHING;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Text, submissionId);
        command.Parameters.AddWithValue("grading_version", NpgsqlDbType.Bigint, gradingVersion);
        command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<(RequestIdentity Identity, TResult Result)?> FindIdempotencyAsync<TResult>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        string requestId,
        CancellationToken cancellationToken)
        where TResult : class
    {
        var sql = $"""
            SELECT exam_id, candidate_id, participation_id, device_id, result_payload
              FROM {tableName}
             WHERE request_id = @request_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("request_id", NpgsqlDbType.Text, requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var identity = new RequestIdentity(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3));
        var result = JsonSerializer.Deserialize<TResult>(reader.GetString(4), JsonOptions)
                     ?? throw new InvalidDataException("幂等结果无法反序列化。");
        return (identity, result);
    }

    private static async ValueTask InsertIdempotencyAsync<TResult>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        string requestId,
        RequestIdentity identity,
        TResult result,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
        where TResult : class
    {
        var sql = $"""
            INSERT INTO {tableName}
                (request_id, exam_id, candidate_id, participation_id, device_id,
                 result_payload, created_at)
            VALUES
                (@request_id, @exam_id, @candidate_id, @participation_id, @device_id,
                 @result_payload, @created_at);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("request_id", NpgsqlDbType.Text, requestId);
        command.Parameters.AddWithValue("exam_id", NpgsqlDbType.Text, identity.ExamId);
        command.Parameters.AddWithValue("candidate_id", NpgsqlDbType.Text, identity.CandidateId);
        command.Parameters.AddWithValue("participation_id", NpgsqlDbType.Text, identity.ParticipationId);
        command.Parameters.AddWithValue("device_id", NpgsqlDbType.Text, identity.DeviceId);
        command.Parameters.AddWithValue(
            "result_payload",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(result, JsonOptions));
        command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, createdAt);
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

    private static void AddAuthorizationParameters(
        NpgsqlCommand command,
        SubmissionAuthorizationState state)
    {
        command.Parameters.AddWithValue("participation_id", NpgsqlDbType.Text, state.ParticipationId);
        command.Parameters.AddWithValue("exam_id", NpgsqlDbType.Text, state.ExamId);
        command.Parameters.AddWithValue("candidate_id", NpgsqlDbType.Text, state.CandidateId);
        command.Parameters.AddWithValue("entry_version", NpgsqlDbType.Bigint, state.EntryVersion);
        command.Parameters.AddWithValue(
            "business_state",
            NpgsqlDbType.Text,
            ToDatabaseState(state.BusinessState));
        command.Parameters.AddWithValue(
            "authorized_device_id",
            NpgsqlDbType.Text,
            state.AuthorizedDeviceId);
        command.Parameters.AddWithValue(
            "authorization_version",
            NpgsqlDbType.Bigint,
            state.AuthorizationVersion);
        command.Parameters.AddWithValue(
            "current_paper_version",
            NpgsqlDbType.Bigint,
            state.CurrentPaperVersion);
        command.Parameters.AddWithValue(
            "current_grading_version",
            NpgsqlDbType.Bigint,
            state.CurrentGradingVersion);
    }

    private static PersistedProgress ReadProgress(NpgsqlDataReader reader)
    {
        var answers = JsonSerializer.Deserialize<Dictionary<string, AnswerValue>>(
                          reader.GetString(8),
                          JsonOptions) ?? [];
        return new PersistedProgress(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            PersistedProgress.FreezeAnswers(answers),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetFieldValue<DateTimeOffset>(10));
    }

    private static FinalSubmission ReadSubmission(NpgsqlDataReader reader)
    {
        var answers = JsonSerializer.Deserialize<Dictionary<string, AnswerValue>>(
                          reader.GetString(7),
                          JsonOptions) ?? [];
        var receivedAt = reader.GetFieldValue<DateTimeOffset>(8);
        var receipt = new SubmissionReceipt(
            reader.GetString(9),
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            receivedAt,
            reader.GetInt64(6),
            reader.GetInt64(10));
        return new FinalSubmission(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            PersistedProgress.FreezeAnswers(answers),
            receivedAt,
            receipt);
    }

    private static bool IsAuthorized(SyncProgressRequest request, AuthorizationRow authorization) =>
        string.Equals(request.ExamId, authorization.ExamId, StringComparison.Ordinal) &&
        string.Equals(request.CandidateId, authorization.CandidateId, StringComparison.Ordinal) &&
        request.EntryVersion == authorization.EntryVersion &&
        string.Equals(request.DeviceId, authorization.AuthorizedDeviceId, StringComparison.Ordinal) &&
        request.AuthorizationVersion == authorization.AuthorizationVersion;

    private static bool IsAuthorized(SubmitFinalAnswersRequest request, AuthorizationRow authorization) =>
        string.Equals(request.ExamId, authorization.ExamId, StringComparison.Ordinal) &&
        string.Equals(request.CandidateId, authorization.CandidateId, StringComparison.Ordinal) &&
        request.EntryVersion == authorization.EntryVersion &&
        string.Equals(request.DeviceId, authorization.AuthorizedDeviceId, StringComparison.Ordinal) &&
        request.AuthorizationVersion == authorization.AuthorizationVersion;

    private static bool Matches(RequestIdentity identity, SyncProgressRequest request) =>
        identity == IdentityOf(request);

    private static bool Matches(RequestIdentity identity, SubmitFinalAnswersRequest request) =>
        identity == IdentityOf(request);

    private static RequestIdentity IdentityOf(SyncProgressRequest request) =>
        new(request.ExamId, request.CandidateId, request.ParticipationId, request.DeviceId);

    private static RequestIdentity IdentityOf(SubmitFinalAnswersRequest request) =>
        new(request.ExamId, request.CandidateId, request.ParticipationId, request.DeviceId);

    private static string ToDatabaseState(ParticipationBusinessState state) => state switch
    {
        ParticipationBusinessState.NotParticipating => "not_participating",
        ParticipationBusinessState.Waiting => "waiting",
        ParticipationBusinessState.Answering => "answering",
        ParticipationBusinessState.Submitted => "submitted",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static void AddNullable(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType type,
        object? value) =>
        command.Parameters.AddWithValue(name, type, value ?? DBNull.Value);

    private readonly record struct RequestIdentity(
        string ExamId,
        string CandidateId,
        string ParticipationId,
        string DeviceId);

    private sealed record AuthorizationRow(
        string ExamId,
        string CandidateId,
        string ParticipationId,
        long EntryVersion,
        string AuthorizedDeviceId,
        long AuthorizationVersion,
        string BusinessState,
        long CurrentPaperVersion,
        long CurrentGradingVersion);
}
