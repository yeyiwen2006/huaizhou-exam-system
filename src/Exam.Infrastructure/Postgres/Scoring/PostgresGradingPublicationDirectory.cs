using Exam.Domain.Scoring;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Scoring;

/// <summary>
/// 从版本状态表枚举已发布计分规则的考试。只读单表、不读规则源数据，
/// 供重算对账按实际已发布版本决定范围。
/// </summary>
public sealed class PostgresGradingPublicationDirectory(NpgsqlDataSource dataSource) : IPublishedGradingDirectory
{
    private readonly NpgsqlDataSource _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async ValueTask<IReadOnlyList<PublishedGradingVersion>> ListPublishedAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT exam_id, grading_version
              FROM exam.version_state
             WHERE grading_version > 0
             ORDER BY exam_id;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var published = new List<PublishedGradingVersion>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var examId = reader.GetString(0);
            var version = reader.GetInt64(1);
            if (string.IsNullOrWhiteSpace(examId) || version <= 0)
                throw new InvalidDataException("已发布计分版本记录不完整。");
            published.Add(new PublishedGradingVersion(examId, version));
        }

        return published;
    }
}
