using Exam.Domain.Reporting;
using Exam.Infrastructure.Reporting;

namespace Exam.UnitTests.Reporting;

/// <summary>Only synthetic participants and answers are used here.</summary>
public sealed partial class ReportWorkbookWriterTests
{
    [Fact]
    public void Workbook_RejectsSubjectScorePlacedUnderAnotherHeader()
    {
        var report = ReportCalculator.Calculate(TwoParticipantInput());
        var total = report.StudentTotals[0];
        var corrupted = report with
        {
            StudentTotals = [total with
            {
                Subjects = [total.Subjects[0] with { SubjectId = "other-subject" }]
            }, report.StudentTotals[1]]
        };

        Assert.Throws<ArgumentException>(() => new ReportWorkbookWriter().Write(corrupted));
    }

    [Fact]
    public void Workbook_RejectsAnswerPlacedUnderAnotherQuestion()
    {
        var report = ReportCalculator.Calculate(TwoParticipantInput());
        var subject = report.Subjects[0];
        var answerRow = subject.Answers[0];
        var corrupted = report with
        {
            Subjects = [subject with
            {
                Answers = [answerRow with
                {
                    Answers = [answerRow.Answers[0] with { QuestionId = "other-question" }]
                }, subject.Answers[1]]
            }]
        };

        Assert.Throws<ArgumentException>(() => new ReportWorkbookWriter().Write(corrupted));
    }

    [Fact]
    public void Workbook_RejectsScoreThatDiffersBetweenTotalsAndSubjectTable()
    {
        var report = ReportCalculator.Calculate(TwoParticipantInput());
        var subject = report.Subjects[0];
        var corrupted = report with
        {
            Subjects = [subject with
            {
                Scores = [subject.Scores[0] with { Score = R(5) }, subject.Scores[1]]
            }]
        };

        Assert.Throws<ArgumentException>(() => new ReportWorkbookWriter().Write(corrupted));
    }

    [Fact]
    public void Workbook_RejectsClassMeanFromDifferentSample()
    {
        var report = ReportCalculator.Calculate(TwoParticipantInput());
        var first = report.ClassAverages[0];
        var corrupted = report with
        {
            ClassAverages = [first with
            {
                TotalMean = first.TotalMean! with { SampleCount = first.SampleCount + 1 }
            }, report.ClassAverages[1]]
        };

        Assert.Throws<ArgumentException>(() => new ReportWorkbookWriter().Write(corrupted));
    }
}
