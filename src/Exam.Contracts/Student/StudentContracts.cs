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
