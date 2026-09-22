namespace Exam.Infrastructure.Postgres.Resources;

/// <summary>Shared read authorization for encrypted packages and public instructions.</summary>
internal static class PreparedEntryAccessSql
{
    // Existing actual participants may recover after the first-entry deadline, but only
    // the currently authorized device can do so. Submitted/withdrawn entries never gain
    // this exception. Joining current roster identity also invalidates stale sessions.
    public const string CurrentPreparationsCte = """
        WITH authorized_preparations AS
        (
            SELECT preparation.*
              FROM exam.entry_preparations preparation
              JOIN exam.student_sessions session
                ON session.session_id = preparation.session_id
               AND session.device_id = preparation.device_id
              JOIN exam.activation_devices device
                ON device.device_id = preparation.device_id
               AND device.activation_status = 'active'
              JOIN exam.candidate_roster_entries roster
                ON roster.exam_id = preparation.exam_id
               AND roster.candidate_id = preparation.candidate_id
               AND roster.candidate_name = session.candidate_name
               AND roster.class_text = session.class_text
               AND roster.admission_no = session.admission_no
              JOIN exam.exam_entry_access entry_access
                ON entry_access.exam_id = preparation.exam_id
              LEFT JOIN exam.participations participation
                ON participation.exam_id = preparation.exam_id
               AND participation.candidate_id = preparation.candidate_id
             WHERE preparation.exam_id = ANY(session.exam_ids)
               AND preparation.candidate_id = (session.candidate_ids_by_exam ->> preparation.exam_id)
               AND entry_access.paper_version = preparation.paper_version
               AND entry_access.instructions_version = preparation.instructions_version
               AND entry_access.package_ready AND entry_access.instructions_ready
               AND (participation.participation_id IS NULL OR
                    (participation.authorized_device_id = preparation.device_id AND
                     participation.business_state <> 'submitted'))
               AND
               (
                   participation.business_state IN ('waiting', 'answering')
                   OR
                   (entry_access.lifecycle <> 'ended' AND
                    CURRENT_TIMESTAMP >= entry_access.scheduled_start - INTERVAL '20 minutes' AND
                    CURRENT_TIMESTAMP < entry_access.scheduled_end)
               )
        )

        """;
}
