using Exam.Application.Participation;
using Exam.Contracts.Participation;
using Npgsql;

namespace Exam.Infrastructure.Postgres.Participation;

public sealed class PostgresCandidateRosterDirectory(NpgsqlDataSource dataSource) : ICandidateRosterDirectory
{
    public async ValueTask<IReadOnlyList<CandidateRosterEntry>> FindMatchesAsync(
        string name,
        string classText,
        string admissionNo,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT exam_id, candidate_id, candidate_name, class_text, admission_no
              FROM exam.candidate_roster_entries
             WHERE candidate_name = @candidate_name
               AND class_text = @class_text
               AND admission_no = @admission_no
             ORDER BY exam_id;
            """);
        command.Parameters.AddWithValue("candidate_name", name);
        command.Parameters.AddWithValue("class_text", classText);
        command.Parameters.AddWithValue("admission_no", admissionNo);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<CandidateRosterEntry>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new CandidateRosterEntry(
                reader.GetString(0),
                new CandidateIdentity(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4))));
        }

        return entries;
    }

    public async ValueTask<bool> IsRegisteredAsync(
        string examId,
        string candidateId,
        CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT EXISTS (
                SELECT 1
                  FROM exam.candidate_roster_entries
                 WHERE exam_id = @exam_id AND candidate_id = @candidate_id
            );
            """);
        command.Parameters.AddWithValue("exam_id", examId);
        command.Parameters.AddWithValue("candidate_id", candidateId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("PostgreSQL did not return the roster existence result."));
    }
}
