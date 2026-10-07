using System.Text.Json;

using Exam.Contracts.Common;
using Exam.Contracts.Grading;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Postgres.HighAvailability;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Scoring;

/// <summary>
/// 计分结果的 PostgreSQL 存储。主键是 submission_id：换规则版本重算时**更新同一行**，
/// 因此重复消费计分任务或重复重算都不会让成绩累计两次（精确计分 审查里「重复题目可重复累计」的同类防护）。
/// 逐题与单科精确值按既有字符串形式存 jsonb，报表读取方不必反解分数格式。
/// </summary>
public sealed class PostgresGradingResultStore : IGradingResultStore
{
    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Create();

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresActiveNodeWriteGuard? _writeGuard;

    public PostgresGradingResultStore(NpgsqlDataSource dataSource, PostgresActiveNodeWriteGuard? writeGuard = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _writeGuard = writeGuard;
    }

    public async ValueTask SaveAsync(StoredGradingResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        result = result.Snapshot();

        const string sql = """
            INSERT INTO exam.grading_results
                (submission_id, exam_id, candidate_id, grading_version,
                 question_scores, subject_scores, total_score, graded_at)
            SELECT @submission_id, @exam_id, @candidate_id, @grading_version,
                   @question_scores, @subject_scores, @total_score, @graded_at
              FROM exam.final_submissions AS submission
             WHERE submission.submission_id = @submission_id
               AND submission.exam_id = @exam_id AND submission.candidate_id = @candidate_id
            ON CONFLICT (submission_id) DO UPDATE SET
                exam_id         = EXCLUDED.exam_id,
                candidate_id    = EXCLUDED.candidate_id,
                grading_version = EXCLUDED.grading_version,
                question_scores = EXCLUDED.question_scores,
                subject_scores  = EXCLUDED.subject_scores,
                total_score     = EXCLUDED.total_score,
                graded_at       = CASE WHEN grading_results.grading_version = EXCLUDED.grading_version
                                       THEN grading_results.graded_at ELSE EXCLUDED.graded_at END
            WHERE grading_results.exam_id = EXCLUDED.exam_id
              AND grading_results.candidate_id = EXCLUDED.candidate_id
              AND (grading_results.grading_version < EXCLUDED.grading_version OR
                   (grading_results.grading_version = EXCLUDED.grading_version
                    AND grading_results.question_scores = EXCLUDED.question_scores
                    AND grading_results.subject_scores = EXCLUDED.subject_scores
                    AND grading_results.total_score = EXCLUDED.total_score));
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = _writeGuard is null ? null : await _writeGuard.BeginAsync(connection, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Text, result.Grade.SubmissionId);
        command.Parameters.AddWithValue("exam_id", NpgsqlDbType.Text, result.Grade.ExamId);
        command.Parameters.AddWithValue("candidate_id", NpgsqlDbType.Text, result.Grade.CandidateId);
        command.Parameters.AddWithValue("grading_version", NpgsqlDbType.Bigint, result.Grade.GradingVersion);
        command.Parameters.AddWithValue(
            "question_scores",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(result.Grade.Questions, JsonOptions));
        command.Parameters.AddWithValue(
            "subject_scores",
            NpgsqlDbType.Jsonb,
            JsonSerializer.Serialize(result.Grade.Subjects, JsonOptions));
        command.Parameters.AddWithValue("total_score", NpgsqlDbType.Text, result.Grade.ExactTotalScore);
        command.Parameters.AddWithValue("graded_at", NpgsqlDbType.TimestampTz, result.GradedAt);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("计分结果归属、版本或同版本内容冲突；原结果未被覆盖。");
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask<StoredGradingResult?> FindAsync(
        string submissionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(submissionId);

        const string sql = """
            SELECT submission_id, exam_id, candidate_id, grading_version,
                   question_scores, subject_scores, total_score, graded_at
              FROM exam.grading_results
             WHERE submission_id = @submission_id;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Text, submissionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadResult(reader) : null;
    }

    public async ValueTask<IReadOnlyList<StoredGradingResult>> ListAsync(
        string examId,
        long gradingVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        ArgumentOutOfRangeException.ThrowIfLessThan(gradingVersion, 1);

        const string sql = """
            SELECT submission_id, exam_id, candidate_id, grading_version,
                   question_scores, subject_scores, total_score, graded_at
              FROM exam.grading_results
             WHERE exam_id = @exam_id AND grading_version = @grading_version
             ORDER BY candidate_id, submission_id;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("exam_id", NpgsqlDbType.Text, examId);
        command.Parameters.AddWithValue("grading_version", NpgsqlDbType.Bigint, gradingVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var results = new List<StoredGradingResult>();
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadResult(reader));
        }

        return results;
    }

    private static StoredGradingResult ReadResult(NpgsqlDataReader reader)
    {
        var submissionId = reader.GetString(0);
        var examId = reader.GetString(1);
        var candidateId = reader.GetString(2);
        var gradingVersion = reader.GetInt64(3);
        var questions = JsonSerializer.Deserialize<List<QuestionGrade>>(reader.GetString(4), JsonOptions)
            ?? throw new InvalidDataException("持久化逐题成绩损坏。");
        var subjects = JsonSerializer.Deserialize<List<SubjectGrade>>(reader.GetString(5), JsonOptions)
            ?? throw new InvalidDataException("持久化科目成绩损坏。");
        var total = reader.GetString(6);
        var gradedAt = reader.GetFieldValue<DateTimeOffset>(7);

        return new StoredGradingResult(
            new SubmissionGrade(submissionId, examId, candidateId, gradingVersion, questions, subjects, total),
            gradedAt).Snapshot();
    }
}
