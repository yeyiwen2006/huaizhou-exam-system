using System.Globalization;
using System.Numerics;
using Exam.Domain.Scoring;

namespace Exam.Domain.Reporting;

// Validates existing projections; never repairs, reorders, or replaces their values.
// Shape validation must run first. Authorization and publication remain external.
internal static class ReportResultConsistencyValidator
{
    public static void Validate(ReportCalculationResult result)
    {
        var examNumbers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in result.StudentTotals)
        {
            Require(Digits(row.Participant.ExamNumber) && Digits(row.Participant.ClassNumber) &&
                !string.IsNullOrWhiteSpace(row.Participant.DisplayName) &&
                examNumbers.Add(row.Participant.ExamNumber) && !row.TotalScore.IsNegative &&
                row.Subjects.All(subject => !subject.Score.IsNegative));
            Require(Sum(row.Subjects.Select(subject => subject.Score)) == row.TotalScore);
        }
        CheckRanking(result.StudentTotals, row => row.TotalScore,
            row => row.TotalRank, row => row.Participant.ExamNumber);

        var sample = result.StudentTotals.Where(row => row.Subjects.All(subject => !subject.Score.IsZero))
            .ToArray();
        Require(sample.Length == result.CommonSampleCount);
        CheckMean(result.TotalMean, sample.Select(row => row.TotalScore));

        var previousOrder = 0;
        foreach (var table in result.Subjects)
        {
            Require(table.Subject.Order > previousOrder &&
                !string.IsNullOrWhiteSpace(table.Subject.DisplayName) &&
                !table.Subject.MaximumScore.IsNegative && table.Subject.Questions.Count > 0);
            previousOrder = table.Subject.Order;
            var previousQuestion = 0;
            foreach (var question in table.Subject.Questions)
            {
                Require(question.QuestionNo > previousQuestion && !question.MaximumScore.IsNegative);
                previousQuestion = question.QuestionNo;
            }
            Require(Sum(table.Subject.Questions.Select(question => question.MaximumScore)) ==
                table.Subject.MaximumScore);
            CheckRanking(table.Scores, row => row.Score, row => row.Rank,
                row => row.Participant.ExamNumber);
            var sampleIds = table.Scores.Where(row => !row.Score.IsZero)
                .Select(row => row.Participant.CandidateId).ToHashSet(StringComparer.Ordinal);
            CheckMean(table.SubjectMean, table.Scores.Where(row => sampleIds.Contains(row.Participant.CandidateId))
                .Select(row => row.Score));
            for (var rowIndex = 0; rowIndex < table.Scores.Count; rowIndex++)
            {
                var score = table.Scores[rowIndex];
                var answers = table.Answers[rowIndex];
                var questions = table.QuestionScores[rowIndex];
                Require(answers.Participant == score.Participant && questions.Participant == score.Participant);
                var raw = Sum(questions.Questions.Select(question => question.Score));
                Require((raw.IsNegative ? ExactRational.Zero : raw) == score.Score);
                for (var questionIndex = 0; questionIndex < questions.Questions.Count; questionIndex++)
                {
                    var question = questions.Questions[questionIndex];
                    Require(question.IsFullScore ==
                        (question.Score == table.Subject.Questions[questionIndex].MaximumScore));
                }
            }
            for (var questionIndex = 0; questionIndex < table.QuestionMeans.Count; questionIndex++)
                CheckMean(table.QuestionMeans[questionIndex].Mean,
                    table.QuestionScores.Where(row => sampleIds.Contains(row.Participant.CandidateId))
                        .Select(row => row.Questions[questionIndex].Score));
        }

        string? previousClass = null;
        foreach (var row in result.ClassAverages)
        {
            Require(Digits(row.ClassNumber) &&
                (previousClass is null || CompareNumber(previousClass, row.ClassNumber) < 0));
            previousClass = row.ClassNumber;
            var classSample = sample.Where(student => student.Participant.ClassNumber == row.ClassNumber).ToArray();
            Require(row.SampleCount == classSample.Length);
            CheckMean(row.TotalMean, classSample.Select(student => student.TotalScore));
            CheckClassRank(result.ClassAverages, row, item => item.TotalMean, row.TotalRank);
            for (var index = 0; index < row.Subjects.Count; index++)
            {
                CheckMean(row.Subjects[index].Mean, result.StudentTotals
                    .Where(student => student.Participant.ClassNumber == row.ClassNumber)
                    .Select(student => student.Subjects[index].Score).Where(score => !score.IsZero));
                CheckClassRank(result.ClassAverages, row, item => item.Subjects[index].Mean,
                    row.Subjects[index].Rank);
            }
        }
    }

    private static void CheckRanking<T>(IReadOnlyList<T> rows, Func<T, ExactRational> score,
        Func<T, int> rank, Func<T, string> number)
    {
        var expectedRank = 1;
        for (var index = 0; index < rows.Count; index++)
        {
            if (index > 0)
            {
                var comparison = Compare(score(rows[index - 1]), score(rows[index]));
                Require(comparison >= 0);
                if (comparison > 0) expectedRank = index + 1;
                else Require(CompareNumber(number(rows[index - 1]), number(rows[index])) < 0);
            }
            Require(rank(rows[index]) == expectedRank);
        }
    }

    private static void CheckClassRank(IReadOnlyList<ClassAverageRow> rows, ClassAverageRow row,
        Func<ClassAverageRow, ReportMean?> mean, int? rank)
    {
        var current = mean(row);
        if (current is null) { Require(rank is null); return; }
        Require(rank == 1 + rows.Count(other => mean(other) is { } value &&
            Compare(value.Exact, current.Exact) > 0));
    }

    private static void CheckMean(ReportMean? mean, IEnumerable<ExactRational> values)
    {
        var sample = values.ToArray();
        if (sample.Length == 0) { Require(mean is null); return; }
        Require(mean is not null && mean.SampleCount == sample.Length &&
            mean.Exact == Sum(sample) / ExactRational.FromInteger(sample.Length) &&
            mean.Display == ReportCalculator.FormatTwoPlaces(mean.Exact));
    }

    private static ExactRational Sum(IEnumerable<ExactRational> values) =>
        values.Aggregate(ExactRational.Zero, (sum, value) => sum + value);

    private static bool Digits(string? text) => !string.IsNullOrEmpty(text) && text.All(char.IsAsciiDigit);

    private static int Compare(ExactRational left, ExactRational right) =>
        (left.Numerator * right.Denominator).CompareTo(right.Numerator * left.Denominator);

    private static int CompareNumber(string left, string right)
    {
        var comparison = BigInteger.Parse(left, CultureInfo.InvariantCulture)
            .CompareTo(BigInteger.Parse(right, CultureInfo.InvariantCulture));
        return comparison != 0 ? comparison : string.CompareOrdinal(left, right);
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new ArgumentException("报表结果算术、样本、排序或着色数据不一致。");
    }
}
