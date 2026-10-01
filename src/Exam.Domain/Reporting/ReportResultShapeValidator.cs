using System.Diagnostics.CodeAnalysis;
using Exam.Domain.Scoring;

namespace Exam.Domain.Reporting;

/// <summary>
/// Rejects incomplete or cross-wired projections before they reach a table or
/// workbook. Ranking and arithmetic remain owned by ReportCalculator.
/// </summary>
public static class ReportResultShapeValidator
{
    public static void Validate(ReportCalculationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Require(result.Subjects is { Count: > 0 } && result.StudentTotals is { Count: > 0 } &&
            result.ClassAverages is not null && result.TotalMaximumScore is not null &&
            result.CommonSampleCount >= 0 && result.CommonSampleCount <= result.StudentTotals.Count);

        var subjectIds = new HashSet<string>(StringComparer.Ordinal);
        var questionIds = new HashSet<string>(StringComparer.Ordinal);
        var maximum = ExactRational.Zero;
        foreach (var table in result.Subjects)
        {
            Require(table?.Subject is not null && !string.IsNullOrWhiteSpace(table.Subject.SubjectId) &&
                table.Subject.MaximumScore is not null && table.Subject.Questions is not null &&
                subjectIds.Add(table.Subject.SubjectId));
            maximum += table.Subject.MaximumScore;
            foreach (var question in table.Subject.Questions)
                Require(question is not null && !string.IsNullOrWhiteSpace(question.QuestionId) &&
                    question.MaximumScore is not null && questionIds.Add(question.QuestionId));
        }
        Require(maximum == result.TotalMaximumScore);
        CheckMean(result.TotalMean, result.CommonSampleCount);

        var totals = new Dictionary<string, StudentTotalRow>(StringComparer.Ordinal);
        var classes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in result.StudentTotals)
        {
            Require(row?.Participant is not null && !string.IsNullOrWhiteSpace(row.Participant.CandidateId) &&
                row.TotalScore is not null && row.TotalRank > 0 && row.Subjects is not null &&
                row.Subjects.Count == result.Subjects.Count &&
                totals.TryAdd(row.Participant.CandidateId, row));
            classes.Add(row.Participant.ClassNumber);
            for (var index = 0; index < result.Subjects.Count; index++)
            {
                var subject = row.Subjects[index];
                Require(subject is not null &&
                    subject.SubjectId == result.Subjects[index].Subject.SubjectId &&
                    subject.Score is not null && subject.Rank > 0);
            }
        }

        for (var index = 0; index < result.Subjects.Count; index++)
            CheckSubject(result.Subjects[index], index, totals, result.CommonSampleCount);

        Require(result.ClassAverages.Count == classes.Count);
        var seenClasses = new HashSet<string>(StringComparer.Ordinal);
        var countedSamples = 0;
        foreach (var row in result.ClassAverages)
        {
            Require(row is not null && classes.Contains(row.ClassNumber) &&
                seenClasses.Add(row.ClassNumber) && row.Subjects is not null &&
                row.Subjects.Count == result.Subjects.Count && row.SampleCount >= 0 &&
                row.SampleCount <= result.CommonSampleCount);
            countedSamples = checked(countedSamples + row.SampleCount);
            CheckMean(row.TotalMean, row.SampleCount);
            Require((row.TotalRank is null) == (row.TotalMean is null));
            for (var index = 0; index < row.Subjects.Count; index++)
            {
                var subject = row.Subjects[index];
                Require(subject is not null &&
                    subject.SubjectId == result.Subjects[index].Subject.SubjectId);
                CheckMean(subject.Mean, row.SampleCount);
                Require((subject.Rank is null) == (subject.Mean is null));
            }
        }
        Require(countedSamples == result.CommonSampleCount);
        ReportResultConsistencyValidator.Validate(result);
    }

    private static void CheckSubject(SubjectTables table, int subjectIndex,
        IReadOnlyDictionary<string, StudentTotalRow> totals, int commonSampleCount)
    {
        Require(table.Scores is not null && table.Answers is not null &&
            table.QuestionScores is not null && table.QuestionMeans is not null &&
            table.Scores.Count == totals.Count && table.Answers.Count == totals.Count &&
            table.QuestionScores.Count == totals.Count &&
            table.QuestionMeans.Count == table.Subject.Questions.Count);
        CheckMean(table.SubjectMean, commonSampleCount);
        for (var index = 0; index < table.QuestionMeans.Count; index++)
        {
            var mean = table.QuestionMeans[index];
            Require(mean is not null &&
                mean.QuestionId == table.Subject.Questions[index].QuestionId);
            CheckMean(mean.Mean, commonSampleCount);
        }

        var scores = new Dictionary<string, SubjectScoreRow>(StringComparer.Ordinal);
        foreach (var row in table.Scores)
        {
            Require(row?.Participant is not null && row.Score is not null &&
                totals.TryGetValue(row.Participant.CandidateId, out var total) &&
                row.Participant == total.Participant &&
                row.Rank == total.Subjects[subjectIndex].Rank &&
                row.Score == total.Subjects[subjectIndex].Score &&
                scores.TryAdd(row.Participant.CandidateId, row));
        }

        var answerIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in table.Answers)
        {
            Require(row?.Participant is not null && row.Answers is not null &&
                scores.TryGetValue(row.Participant.CandidateId, out var score) &&
                row.Participant == score.Participant && row.Rank == score.Rank &&
                row.Score == score.Score && row.Answers.Count == table.Subject.Questions.Count &&
                answerIds.Add(row.Participant.CandidateId));
            for (var index = 0; index < row.Answers.Count; index++)
                Require(row.Answers[index] is { Text: not null } answer &&
                    answer.QuestionId == table.Subject.Questions[index].QuestionId);
        }

        var questionScoreIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in table.QuestionScores)
        {
            Require(row?.Participant is not null && row.Questions is not null &&
                scores.TryGetValue(row.Participant.CandidateId, out var score) &&
                row.Participant == score.Participant && row.Rank == score.Rank &&
                row.Score == score.Score && row.Questions.Count == table.Subject.Questions.Count &&
                questionScoreIds.Add(row.Participant.CandidateId));
            for (var index = 0; index < row.Questions.Count; index++)
                Require(row.Questions[index] is { Score: not null } question &&
                    question.QuestionId == table.Subject.Questions[index].QuestionId);
        }
    }

    private static void CheckMean(ReportMean? mean, int count) =>
        Require(count == 0 ? mean is null :
            mean is { Exact: not null, Display: not null } && mean.SampleCount == count);

    private static void Require([DoesNotReturnIf(false)] bool condition)
    {
        if (!condition)
            throw new ArgumentException("报表结果结构或跨表数据不一致。");
    }
}
