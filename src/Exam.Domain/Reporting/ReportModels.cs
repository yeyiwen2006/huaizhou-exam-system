using Exam.Contracts.Grading;
using Exam.Domain.Scoring;

namespace Exam.Domain.Reporting;

// G01 calculation input. The production adapter must obtain and validate the
// authoritative participation, collection, rule, and display versions first.
public sealed record ReportQuestion(string QuestionId, int QuestionNo, ExactRational MaximumScore);

public sealed record ReportSubject(
    string SubjectId,
    string DisplayName,
    int Order,
    ExactRational MaximumScore,
    IReadOnlyList<ReportQuestion> Questions);

public sealed record ReportParticipant(
    string CandidateId,
    string ExamNumber,
    string DisplayName,
    string ClassNumber);

public sealed record ReportQuestionGrade(
    string QuestionId,
    string AnswerText,
    AnswerOutcome Outcome,
    ExactRational Score);

public sealed record ReportSubjectGrade(
    string SubjectId,
    ExactRational FinalScore,
    IReadOnlyList<ReportQuestionGrade> Questions);

public sealed record ReportSubmission(
    string SubmissionId,
    string ExamId,
    string CandidateId,
    long GradingVersion,
    IReadOnlyList<ReportSubjectGrade> Subjects,
    ExactRational TotalScore);

public sealed record ReportCalculationInput(
    string ExamId,
    long GradingVersion,
    IReadOnlyList<ReportSubject> Subjects,
    IReadOnlyList<ReportParticipant> Participants,
    IReadOnlyList<ReportSubmission> Submissions);

public sealed record ReportMean(ExactRational Exact, int SampleCount, string Display);

public sealed record StudentSubjectResult(string SubjectId, ExactRational Score, int Rank);

public sealed record StudentTotalRow(
    ReportParticipant Participant,
    ExactRational TotalScore,
    int TotalRank,
    IReadOnlyList<StudentSubjectResult> Subjects);

public sealed record SubjectScoreRow(ReportParticipant Participant, ExactRational Score, int Rank);

public sealed record AnswerCell(string QuestionId, string Text, bool IsCompletelyCorrect);

public sealed record SubjectAnswerRow(
    ReportParticipant Participant,
    int Rank,
    IReadOnlyList<AnswerCell> Answers,
    ExactRational Score);

public sealed record QuestionScoreCell(string QuestionId, ExactRational Score, bool IsFullScore);

public sealed record SubjectQuestionScoreRow(
    ReportParticipant Participant,
    int Rank,
    IReadOnlyList<QuestionScoreCell> Questions,
    ExactRational Score);

public sealed record QuestionMean(string QuestionId, ReportMean? Mean);

public sealed record SubjectTables(
    ReportSubject Subject,
    IReadOnlyList<SubjectScoreRow> Scores,
    IReadOnlyList<SubjectAnswerRow> Answers,
    IReadOnlyList<SubjectQuestionScoreRow> QuestionScores,
    ReportMean? SubjectMean,
    IReadOnlyList<QuestionMean> QuestionMeans);

public sealed record ClassSubjectMean(string SubjectId, ReportMean? Mean, int? Rank);

public sealed record ClassAverageRow(
    string ClassNumber,
    int SampleCount,
    IReadOnlyList<ClassSubjectMean> Subjects,
    ReportMean? TotalMean,
    int? TotalRank);

// The five table projections all come from one calculation call. A publication
// layer will bind this value to its authoritative input-version token.
public sealed record ReportCalculationResult(
    IReadOnlyList<StudentTotalRow> StudentTotals,
    IReadOnlyList<SubjectTables> Subjects,
    IReadOnlyList<ClassAverageRow> ClassAverages,
    ExactRational TotalMaximumScore,
    ReportMean? TotalMean,
    int CommonSampleCount);
