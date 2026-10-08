using System.Data;
using Exam.Contracts.Exams;
using Exam.Contracts.Participation;
using Npgsql;

namespace Exam.Infrastructure.Postgres.Exams;

public sealed partial class PostgresExamCatalog
{
    private const string HidingSql = """
        EXISTS (SELECT 1 FROM exam.exam_catalog_hiding h WHERE h.exam_id=e.exam_id
            AND h.operator_key=$1 AND $1<>'' AND h.hidden) AS hidden_for_current,
        EXISTS (SELECT 1 FROM exam.exam_catalog_hiding h WHERE h.exam_id=e.exam_id
            AND h.operator_key='' AND h.hidden) AS hidden_for_all
        """;

    private static string OperatorKey(string? actor) => actor?.Trim().ToUpperInvariant() ?? string.Empty;

    /// <summary>仅整理考务列表。个人隐藏与所有账号隐藏分别保存，取消一种范围不会改动另一种。</summary>
    public async ValueTask<ExamHidingState> SetHidingAsync(string examId, SetExamHidingRequest request, string actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Length > 128 || !Enum.IsDefined(request.Scope))
            throw Rule(ParticipationErrorCodes.InvalidRequest, "请求标识或隐藏范围无效。");
        var hash = Hash(new { Operation = "hiding", ExamId = examId, Request = request, Actor = OperatorKey(actor) });
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await LockAsync(connection, transaction, "exam-catalog-request:" + request.RequestId, cancellationToken).ConfigureAwait(false);
        if (!await FindReplayAsync(connection, transaction, request.RequestId, hash, cancellationToken).ConfigureAwait(false))
        {
            // Also support historical exams which have student metadata but no catalog row.
            await using (var require = new NpgsqlCommand("""
                SELECT e.exam_id FROM exam.exam_entry_access e WHERE e.exam_id=$1
                  AND (EXISTS (SELECT 1 FROM exam.exam_catalog c WHERE c.exam_id=e.exam_id)
                    OR EXISTS (SELECT 1 FROM exam.student_exam_metadata m WHERE m.exam_id=e.exam_id))
                FOR UPDATE OF e;
                """, connection, transaction))
            {
                require.Parameters.AddWithValue(examId);
                if (await require.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string)
                    throw Rule(ParticipationErrorCodes.ExamNotFound, "找不到这场考试。");
            }
            await using (var save = new NpgsqlCommand("""
                INSERT INTO exam.exam_catalog_hiding (exam_id, operator_key, hidden, updated_at, updated_by)
                VALUES ($1, $2, $3, clock_timestamp(), $4)
                ON CONFLICT (exam_id, operator_key) DO UPDATE
                  SET hidden=EXCLUDED.hidden, updated_at=EXCLUDED.updated_at, updated_by=EXCLUDED.updated_by;
                """, connection, transaction))
            {
                save.Parameters.AddWithValue(examId);
                save.Parameters.AddWithValue(request.Scope == ExamHidingScope.AllOperators ? string.Empty : OperatorKey(actor));
                save.Parameters.AddWithValue(request.Hidden);
                save.Parameters.AddWithValue(actor);
                await save.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await SaveReplayAsync(connection, transaction, request.RequestId, hash, examId, cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var summary = (await ReadDetailAsync(connection, null, examId, cancellationToken, actor).ConfigureAwait(false)).Summary;
        return new(examId, summary.HiddenForCurrentOperator, summary.HiddenForAllOperators);
    }
}
