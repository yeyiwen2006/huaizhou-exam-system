using Exam.Contracts.Common;

namespace Exam.Contracts.Student;

/// <summary>Public exam metadata; contains no question content or unlock information.</summary>
public sealed record StudentExamOverview(
    string ExamId,
    string Name,
    DateTimeOffset ScheduledStart,
    DateTimeOffset ScheduledEnd,
    IReadOnlyList<string> Subjects,
    ExamLifecycle Lifecycle,
    string? CategoryName = null);

/// <summary>Public text generated upstream from structured exam rules; never a source of scoring rules.</summary>
public sealed record InstructionsDocument(long Version, IReadOnlyList<string> Lines);

public sealed record UpdateStudentExamMetadataRequest(
    string RequestId,
    long ExpectedVersion,
    string Name,
    string? CategoryName,
    IReadOnlyList<string> Subjects,
    IReadOnlyList<string> InstructionLines);

public sealed record StudentExamMetadataResult(
    string ExamId,
    long Version,
    long InstructionsVersion);
