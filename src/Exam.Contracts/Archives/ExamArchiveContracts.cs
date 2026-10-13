namespace Exam.Contracts.Archives;

/// <summary>Portable single-exam archive. The local directory contains this one transport file.</summary>
public static class ExamArchiveFormat
{
    public const int Version = 1;
    public const string FileName = "exam.archive";
    public const long MaximumArchiveBytes = 512L * 1024 * 1024;
    public const long MaximumExpandedBytes = 1024L * 1024 * 1024;
}

public sealed record ExamArchiveConflict(string ExamId, string ExamName, long Version);
public sealed record ExamArchivePreview(int FormatVersion, string SourceExamId, string ExamName,
    DateTimeOffset ExportedAt, IReadOnlyList<ExamArchiveConflict> Conflicts);
public sealed record ExamArchiveImportRequest(string RequestId, string Mode,
    string? ExpectedTargetExamId = null, long? ExpectedTargetVersion = null);
public sealed record ExamArchiveImportResult(string ExamId, string ExamName, bool Imported, bool Skipped);
