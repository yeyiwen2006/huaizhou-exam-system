using Exam.Application.Commands;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Npgsql;
using NpgsqlTypes;

namespace Exam.Infrastructure.Postgres.Commands;

/// <summary>
/// Reads the canonical participation row together with the persisted exam lifecycle used by
/// reliable-command authorization. It does not keep an in-process authorization cache, so a
/// device decision or terminal transition is visible to every server process on the next read.
/// </summary>
public sealed class PostgresParticipationAccessReader(NpgsqlDataSource dataSource)
    : IParticipationAccessReader
{
    public async ValueTask<ParticipationAccessSnapshot?> FindAsync(
        string participationId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT participation.exam_id,
                   participation.candidate_id,
                   participation.participation_id,
                   participation.entry_version,
                   participation.authorized_device_id,
                   participation.authorization_version,
                   COALESCE(version_state.lifecycle, entry_access.lifecycle, 'Scheduled'),
                   participation.business_state,
                   participation.current_paper_version,
                   participation.current_grading_version
              FROM exam.participations AS participation
              LEFT JOIN exam.version_state AS version_state
                ON version_state.exam_id = participation.exam_id
              LEFT JOIN exam.exam_entry_access AS entry_access
                ON entry_access.exam_id = participation.exam_id
             WHERE participation.participation_id = @participation_id;
            """;
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue("participation_id", NpgsqlDbType.Text, participationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ParticipationAccessSnapshot(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetInt64(5),
            ParseLifecycle(reader.GetString(6)),
            ParseBusinessState(reader.GetString(7)),
            reader.GetInt64(8),
            reader.GetInt64(9));
    }

    private static ExamLifecycle ParseLifecycle(string value) =>
        value.ToLowerInvariant() switch
        {
            "scheduled" => ExamLifecycle.Scheduled,
            "running" => ExamLifecycle.Running,
            "ended" => ExamLifecycle.Ended,
            _ => throw new InvalidDataException($"数据库中的考试生命周期无效：{value}")
        };

    private static ParticipationBusinessState ParseBusinessState(string value) =>
        value.ToLowerInvariant() switch
        {
            "not_participating" or "notparticipating" => ParticipationBusinessState.NotParticipating,
            "waiting" => ParticipationBusinessState.Waiting,
            "answering" => ParticipationBusinessState.Answering,
            "submitted" => ParticipationBusinessState.Submitted,
            _ => throw new InvalidDataException($"数据库中的参加考试状态无效：{value}")
        };
}
