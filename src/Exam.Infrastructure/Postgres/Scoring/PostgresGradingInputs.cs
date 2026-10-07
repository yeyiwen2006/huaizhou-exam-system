using System.Text.Json;

using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Domain.Scoring;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Scoring;

/// <summary>
/// 计分输入的 PostgreSQL 读取：收卷 的最终答卷与 版本服务 的计分规则发布事件。
/// 列与序列化设置与 收卷/版本服务 的既有读取保持一致（JsonDefaults 的小驼峰），不另造存储格式。
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

    public async ValueTask<PublishedGradingRules?> LoadRulesAsync(string examId, CancellationToken cancellationToken = default)
    {
        var source = await LoadSourceAsync(examId, cancellationToken);
        return source is null ? null : new(ImportedGradingRules.Create(source.ManifestJson, source.AnswerKeyJson), source.GradingVersion);
    }

    public async ValueTask<Exam.Contracts.Grading.GradingMaterialSnapshot?> LoadSourceAsync(
        string examId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);

        // 一个 SQL 快照读取版本和对应的最后发布事件。不能跳过当前坏事件后把旧规则标成新版本。
        const string sql = """
            SELECT state.grading_version, published.payload::text, state.paper_version
              FROM exam.version_state AS state
              LEFT JOIN LATERAL (
                  SELECT payload FROM exam.version_events
                   WHERE exam_id = state.exam_id AND operation_type = @operation
                     AND stream_revision <= state.stream_revision
                   ORDER BY stream_revision DESC LIMIT 1
              ) AS published ON true
             WHERE state.exam_id = @exam_id;
            """;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("exam_id", NpgsqlDbType.Text, examId);
        command.Parameters.AddWithValue("operation", NpgsqlDbType.Text, GradingOperation);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var gradingVersion = reader.GetInt64(0);
        if (gradingVersion == 0) return null;
        if (gradingVersion < 0 || reader.IsDBNull(1))
            throw new InvalidDataException("当前计分版本缺少可重放的发布事件。");
        var request = JsonSerializer.Deserialize<PublishGradingRulesRequest>(reader.GetString(1), JsonOptions);
        if (request is null || request.ExamId != examId || request.ExpectedVersion != gradingVersion - 1 ||
            string.IsNullOrWhiteSpace(request.ManifestJson) || string.IsNullOrWhiteSpace(request.AnswerKeyJson))
            throw new InvalidDataException("当前计分规则源数据缺失或版本、考试不一致；禁止使用旧规则替代。");
        var rules = ImportedGradingRules.Create(request.ManifestJson, request.AnswerKeyJson);
        if (rules.ExamId != examId)
            throw new InvalidDataException("计分规则源数据属于另一场考试。");
        return new(examId, reader.GetInt64(2), gradingVersion, request.RuleSetId, request.ManifestJson, request.AnswerKeyJson);
    }

    private static FinalSubmission ReadSubmission(NpgsqlDataReader reader)
    {
        var answers = JsonSerializer.Deserialize<Dictionary<string, AnswerValue>>(
                          reader.GetString(7),
                          JsonOptions) ?? throw new InvalidDataException("最终答卷源数据损坏。");
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
