namespace Exam.Infrastructure.Archives;

internal sealed record ArchiveTable(string Name, string Predicate);

/// <summary>Only these application tables can be read or written by an archive.</summary>
internal static class ExamArchiveTables
{
    private const string Exam = "t.exam_id=@exam";
    private const string Participation = "t.participation_id IN (SELECT participation_id FROM exam.participations WHERE exam_id=@exam)";
    private const string Submission = "t.submission_id IN (SELECT submission_id FROM exam.final_submissions WHERE exam_id=@exam)";
    private const string Receipt = """
        (jsonb_path_exists(t.response_json, '$.**.examId ? (@ == $id)', jsonb_build_object('id', @exam::text))
         OR jsonb_path_exists(t.response_json, '$.**.ExamId ? (@ == $id)', jsonb_build_object('id', @exam::text))
         OR EXISTS(SELECT 1 FROM exam.participations p WHERE p.exam_id=@exam AND
             (jsonb_path_exists(t.response_json, '$.**.participationId ? (@ == $id)', jsonb_build_object('id',p.participation_id))
              OR jsonb_path_exists(t.response_json, '$.**.ParticipationId ? (@ == $id)', jsonb_build_object('id',p.participation_id)))))
        """;
    // Parents precede children. Trigger-maintained collection rows are applied last.
    internal static readonly ArchiveTable[] All =
    [
        new("exam_entry_access", Exam), new("candidate_roster_entries", Exam), new("exam_archive_device_history", Exam),
        new("student_sessions", "@exam=ANY(t.exam_ids)"), new("entry_preparations", Exam),
        new("participations", Exam), new("participation_device_conflicts", Participation),
        new("exam_archive_pending_device_conflicts", Exam),
        new("device_decision_audits", Participation), new("participation_progress", Exam),
        new("final_submissions", Exam), new("grading_outbox", Submission), new("grading_results", Exam),
        new("focus_loss_events", Exam), new("version_streams", Exam), new("version_events", Exam),
        new("version_state", Exam), new("student_exam_metadata", Exam), new("student_exam_instructions", Exam),
        new("exam_catalog", Exam), new("exam_catalog_hiding", Exam), new("roster_heads", Exam),
        new("command_events", Exam),
        new("command_acknowledgement_requests", "t.event_id IN (SELECT event_id FROM exam.command_events WHERE exam_id=@exam)"),
        new("progress_idempotency", Exam), new("submission_idempotency", Exam),
        new("participation_idempotency", Receipt), new("version_idempotency", Receipt),
        new("student_metadata_idempotency", Receipt), new("exam_catalog_idempotency", Receipt),
        new("administration_requests", "t.scope='roster:'||@exam"),
        new("paper_materials", Exam),
        new("collection_completion_events", Exam), new("actual_roster_versions", Exam), new("collection_pending_checks", Exam)
    ];
    internal static readonly HashSet<string> Names = All.Select(table => table.Name).Append("activation_devices").ToHashSet(StringComparer.Ordinal);
}
