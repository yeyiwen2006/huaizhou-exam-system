namespace Exam.Contracts.Grading;

public sealed record GradingVersionHistory(
    long GradingVersion, long PaperVersion, DateTimeOffset ChangedAt, string Reason);
