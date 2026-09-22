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
            SELECT exam_id, scheduled_start, scheduled_end, lifecycle,
                   package_ready, instructions_ready, encrypted_package_uri,
                   package_length, paper_version, instructions_version,
                   unlock_reference
              FROM exam.exam_entry_access
             WHERE exam_id = @exam_id;
            """);
        command.Parameters.AddWithValue("exam_id", examId);
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
            reader.IsDBNull(10) ? null : reader.GetString(10));
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
