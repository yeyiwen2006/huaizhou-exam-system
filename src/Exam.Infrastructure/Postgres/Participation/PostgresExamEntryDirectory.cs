using System.Data;
using Exam.Application.Participation;
using Exam.Contracts.Common;
using Npgsql;

namespace Exam.Infrastructure.Postgres.Participation;

public sealed class PostgresExamEntryDirectory(NpgsqlDataSource dataSource) : IExamEntryDirectory
{
    public async ValueTask<ExamEntrySnapshot?> FindAsync(
        string examId,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT access.exam_id, access.scheduled_start, access.scheduled_end, access.lifecycle,
                   access.package_ready, access.instructions_ready, access.encrypted_package_uri,
                   access.package_length, access.paper_version, access.instructions_version,
                   access.unlock_reference, COALESCE(state.schedule_version, 0),
                   COALESCE((state.snapshot->>'reopenedAtScheduleVersion')::bigint, 0)
              FROM exam.exam_entry_access access
              LEFT JOIN exam.version_state state ON state.exam_id = access.exam_id
             WHERE access.exam_id = $1;
            """);
        command.Parameters.AddWithValue(examId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ExamEntrySnapshot(
            reader.GetString(0),
            reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            ParseLifecycle(reader.GetString(3)),
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.IsDBNull(10) ? null : reader.GetString(10))
        {
            ScheduleVersion = reader.GetInt64(11),
            ReopenedAtScheduleVersion = reader.GetInt64(12)
        };
    }

    private static ExamLifecycle ParseLifecycle(string value) =>
        value switch
        {
            "scheduled" => ExamLifecycle.Scheduled,
            "running" => ExamLifecycle.Running,
            "ended" => ExamLifecycle.Ended,
            _ => throw new DataException($"Unknown exam lifecycle '{value}'.")
        };
}
