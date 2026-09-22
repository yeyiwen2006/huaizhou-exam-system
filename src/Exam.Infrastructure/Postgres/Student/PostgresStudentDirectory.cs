using System.Data;
using System.Text.Json;
using Exam.Application.Participation;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Student;
using Npgsql;

namespace Exam.Infrastructure.Postgres.Student;

public sealed class PostgresStudentDirectory(NpgsqlDataSource dataSource)
{
    public async ValueTask<IReadOnlyList<StudentExamOverview>> ListExamsAsync(
        string sessionId,
        string authenticatedDeviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticatedDeviceId);

        // A valid session still produces one row if its roster entries were removed.
        // This distinguishes an empty selector from a session belonging to another device.
        await using var command = dataSource.CreateCommand("""
            SELECT visible.exam_id, visible.display_name, visible.scheduled_start,
                   visible.scheduled_end, visible.subjects, visible.lifecycle,
                   visible.category_name
              FROM exam.student_sessions s
              JOIN exam.activation_devices d ON d.device_id = s.device_id
                   AND d.activation_status = 'active'
              LEFT JOIN LATERAL
              (
                  SELECT e.exam_id, COALESCE(m.display_name, e.exam_id) AS display_name,
                         e.scheduled_start, e.scheduled_end,
                         COALESCE(m.subjects, '[]'::jsonb)::text AS subjects,
                         e.lifecycle, m.category_name
                    FROM exam.candidate_roster_entries r
                    JOIN exam.exam_entry_access e ON e.exam_id = r.exam_id
                    LEFT JOIN exam.student_exam_metadata m ON m.exam_id = e.exam_id
                   WHERE r.exam_id = ANY(s.exam_ids)
                     AND r.candidate_id = s.candidate_ids_by_exam ->> r.exam_id
                     AND r.candidate_name = s.candidate_name
                     AND r.class_text = s.class_text
                     AND r.admission_no = s.admission_no
              ) visible ON true
             WHERE s.session_id = @session_id AND s.device_id = @device_id
             ORDER BY visible.scheduled_start, visible.exam_id;
            """);
        command.Parameters.AddWithValue("session_id", sessionId);
        command.Parameters.AddWithValue("device_id", authenticatedDeviceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var sessionFound = false;
        var result = new List<StudentExamOverview>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sessionFound = true;
            if (reader.IsDBNull(0))
            {
                continue;
            }

            result.Add(new StudentExamOverview(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetFieldValue<DateTimeOffset>(3),
                JsonSerializer.Deserialize<string[]>(reader.GetString(4))
                    ?? throw new DataException("考试科目元数据无效。"),
                ParseLifecycle(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        if (!sessionFound)
        {
            throw new ParticipationRuleException(
                ParticipationErrorCodes.AuthorizationRevoked,
                "登录会话不属于当前已激活设备，请重新登录。");
        }

        return result;
    }

    private static ExamLifecycle ParseLifecycle(string value) => value switch
    {
        "scheduled" => ExamLifecycle.Scheduled,
        "running" => ExamLifecycle.Running,
        "ended" => ExamLifecycle.Ended,
        _ => throw new DataException($"Unknown exam lifecycle '{value}'.")
    };
}
