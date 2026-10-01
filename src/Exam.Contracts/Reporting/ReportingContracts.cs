using System.Text.Json;

namespace Exam.Contracts.Reporting;

public enum ReportState { ExamNotEnded, NoParticipants, Collecting, Grading, InvalidSource, Ready }
public sealed record ReportMissingParticipant(string CandidateId, string ExamNumber, string DisplayName, string ClassNumber);
public sealed record ReportStatus(string ExamId, ReportState State, string Reason,
    int ParticipantCount, int CollectedCount, int GradedCount, long RosterVersion,
    long PaperVersion, long GradingVersion, string? SnapshotId,
    IReadOnlyList<ReportMissingParticipant> MissingParticipants);

// Schema 1: Result contains the five ReportCalculationResult projections, in
// camelCase. Every exact value is a string (finite decimal or n/d).
// Raw rule publications, answer keys, resources and credentials are excluded.
public sealed record PublishedReport(int SchemaVersion, ReportStatus Status, JsonElement Result);
