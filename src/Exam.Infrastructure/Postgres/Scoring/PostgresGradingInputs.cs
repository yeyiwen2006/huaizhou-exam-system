using System.Text.Json;

using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Domain.Scoring;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Scoring;

/// <summary>
/// 计分输入的 PostgreSQL 读取：A05 的最终答卷与 A06 的计分规则发布事件。
/// 列与序列化设置与 A05/A06 的既有读取保持一致（JsonDefaults 的小驼峰），不另造存储格式。
/// </summary>
public sealed class PostgresGradingInputs : IGradingInputs
{
    private const string GradingOperation = "grading.publish";

    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Create();

    private readonly NpgsqlDataSource _dataSource;

    public PostgresGradingInputs(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async ValueTask<FinalSubmission?> LoadSubmissionAsync(
        string submissionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(submissionId);

        const string sql = """
            SELECT submission_id, exam_id, candidate_id, participation_id, device_id,
                   authorization_version, paper_version, frozen_answers, received_at,
                   receipt_id, grading_version
              FROM exam.final_submissions
             WHERE submission_id = @submission_id;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("submission_id", NpgsqlDbType.Text, submissionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSubmission(reader) : null;
    }

    public async ValueTask<PublishedGradingRules?> LoadRulesAsync(
        string examId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);

        // 只认最近一次**带源数据**的计分规则发布：内部兼容路径允许省略源数据，遇到则继续向前找，
        // 否则会拿一份无法重放的规则去计分。
        const string rulesSql = """
            SELECT payload::text
              FROM exam.version_events
             WHERE exam_id = @exam_id
               AND operation_type = @operation
               AND payload ->> 'manifestJson' IS NOT NULL
               AND payload ->> 'answerKeyJson' IS NOT NULL
             ORDER BY stream_revision DESC
             LIMIT 1;
            """;

        const string versionSql = """
            SELECT grading_version FROM exam.version_state WHERE exam_id = @exam_id;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);

        await using var versionCommand = new NpgsqlCommand(versionSql, connection);
        versionCommand.Parameters.AddWithValue("exam_id", NpgsqlDbType.Text, examId);
        var versionResult = await versionCommand.ExecuteScalarAsync(cancellationToken);
        if (versionResult is null or DBNull)
        {
            return null;
        }

        var gradingVersion = Convert.ToInt64(versionResult);
        if (gradingVersion < 1)
        {
            return null;
        }

        await using var rulesCommand = new NpgsqlCommand(rulesSql, connection);
        rulesCommand.Parameters.AddWithValue("exam_id", NpgsqlDbType.Text, examId);
        rulesCommand.Parameters.AddWithValue("operation", NpgsqlDbType.Text, GradingOperation);
        var payload = await rulesCommand.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        var request = JsonSerializer.Deserialize<PublishGradingRulesRequest>(payload, JsonOptions);
        if (request is null
            || string.IsNullOrWhiteSpace(request.ManifestJson)
            || string.IsNullOrWhiteSpace(request.AnswerKeyJson))
        {
            return null;
        }

        // 规则源数据不合法时由 Create 抛出，交给调用方按失败重试处理，不静默降级成别的规则。
        return new PublishedGradingRules(
            ImportedGradingRules.Create(request.ManifestJson, request.AnswerKeyJson),
            gradingVersion);
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
            answers,
            receivedAt,
            receipt);
    }
}
