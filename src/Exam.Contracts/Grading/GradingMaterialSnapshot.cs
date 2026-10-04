namespace Exam.Contracts.Grading;

/// <summary>Operator-only current grading source; never include this in student resources or logs.</summary>
public sealed record GradingMaterialSnapshot(string ExamId, long PaperVersion, long GradingVersion,
    string RuleSetId, string ManifestJson, string AnswerKeyJson);
