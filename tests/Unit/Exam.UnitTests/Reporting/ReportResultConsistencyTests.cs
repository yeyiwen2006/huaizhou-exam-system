using System.Globalization;
using ClosedXML.Excel;
using Exam.Contracts.Grading;
using Exam.Domain.Reporting;
using Exam.Infrastructure.Reporting;

namespace Exam.UnitTests.Reporting;

/// <summary>All participants, scores and answers are synthetic examples.</summary>
public sealed partial class ReportWorkbookWriterTests
{
    [Fact]
    public void Workbook_ValidatesAndExportsThreeHundredSyntheticParticipantsWithTiesAndZeroScores()
    {
        ReportSubject[] subjects =
        [
            new("synthetic-first", "Synthetic first", 1, R(10), [new("synthetic-q1", 1, R(10))]),
            new("synthetic-second", "Synthetic second", 2, R(10), [new("synthetic-q2", 2, R(10))])
        ];
        var participants = Enumerable.Range(1, 300).Select(index => new ReportParticipant(
            "synthetic-" + index, index.ToString("D4", CultureInfo.InvariantCulture),
            "Synthetic candidate", (index % 20 + 1).ToString("D2", CultureInfo.InvariantCulture))).ToArray();
        var submissions = participants.Select((participant, index) =>
        {
            var first = R(index % 11);
            var second = R(index * 3 % 11);
            return new ReportSubmission("synthetic-submission-" + index, "synthetic-exam", participant.CandidateId,
                1,
                [
                    new(subjects[0].SubjectId, first,
                        [new(subjects[0].Questions[0].QuestionId, "A", AnswerOutcome.PartialCorrect, first)]),
                    new(subjects[1].SubjectId, second,
                        [new(subjects[1].Questions[0].QuestionId, "B", AnswerOutcome.PartialCorrect, second)])
                ], first + second);
        }).ToArray();
        var report = ReportCalculator.Calculate(new("synthetic-exam", 1, subjects, participants, submissions));

        using var workbook = new XLWorkbook(new MemoryStream(new ReportWorkbookWriter().Write(report)));

        Assert.Equal(8, workbook.Worksheets.Count);
        Assert.Equal(300, report.StudentTotals.Count);
        Assert.Equal(272, report.CommonSampleCount);
        Assert.Equal(20, report.ClassAverages.Count);
        Assert.Equal("平均分", workbook.Worksheet(1).Cell(302, 1).GetString());
        for (var index = 0; index < report.StudentTotals.Count; index++)
        {
            var row = report.StudentTotals[index];
            Assert.Equal(row.Participant.ExamNumber, workbook.Worksheet(1).Cell(index + 2, 2).GetString());
            Assert.Equal(row.TotalRank, workbook.Worksheet(1).Cell(index + 2, 1).GetDouble());
        }
    }

    [Theory]
    [InlineData("total-sum")]
    [InlineData("question-sum")]
    [InlineData("full-score-color")]
    [InlineData("maximum-sum")]
    [InlineData("total-mean")]
    [InlineData("mean-display")]
    [InlineData("subject-mean")]
    [InlineData("question-mean")]
    [InlineData("class-mean")]
    [InlineData("class-sample")]
    [InlineData("total-order")]
    [InlineData("answer-order")]
    [InlineData("question-score-order")]
    [InlineData("class-order")]
    [InlineData("total-rank")]
    [InlineData("subject-rank")]
    [InlineData("class-rank")]
    public void Workbook_RejectsInternallyContradictoryResults(string corruption)
    {
        var report = ReportCalculator.Calculate(TwoParticipantInput());
        var subject = report.Subjects[0];
        var firstClass = report.ClassAverages[0];
        var secondClass = report.ClassAverages[1];
        var badSubject = subject;
        var corrupted = corruption switch
        {
            "total-sum" => report with
            {
                StudentTotals = [report.StudentTotals[0] with { TotalScore = R(9) }, report.StudentTotals[1]]
            },
            "total-mean" => report with { TotalMean = report.TotalMean! with { Exact = R(9) } },
            "mean-display" => report with { TotalMean = report.TotalMean! with { Display = "999.00" } },
            "class-mean" => report with
            {
                ClassAverages = [firstClass with { TotalMean = firstClass.TotalMean! with { Exact = R(9) } },
                    secondClass]
            },
            "class-sample" => report with
            {
                ClassAverages = [firstClass with { ClassNumber = secondClass.ClassNumber },
                    secondClass with { ClassNumber = firstClass.ClassNumber }]
            },
            "total-order" => report with { StudentTotals = report.StudentTotals.Reverse().ToArray() },
            "class-order" => report with { ClassAverages = report.ClassAverages.Reverse().ToArray() },
            "total-rank" => report with
            {
                StudentTotals = [report.StudentTotals[0] with { TotalRank = 2 }, report.StudentTotals[1]]
            },
            "class-rank" => report with
            {
                ClassAverages = [firstClass with { TotalRank = 2 }, secondClass]
            },
            _ => report
        };
        switch (corruption)
        {
            case "question-sum":
                badSubject = subject with
                {
                    QuestionScores = [subject.QuestionScores[0] with
                    {
                        Questions = [subject.QuestionScores[0].Questions[0] with { Score = R(9), IsFullScore = false }]
                    }, subject.QuestionScores[1]]
                };
                break;
            case "full-score-color":
                badSubject = subject with
                {
                    QuestionScores = [subject.QuestionScores[0] with
                    {
                        Questions = [subject.QuestionScores[0].Questions[0] with { IsFullScore = false }]
                    }, subject.QuestionScores[1]]
                };
                break;
            case "maximum-sum":
                badSubject = subject with
                {
                    Subject = subject.Subject with
                    {
                        Questions = [subject.Subject.Questions[0] with { MaximumScore = R(9) }]
                    }
                };
                break;
            case "subject-mean":
                badSubject = subject with { SubjectMean = subject.SubjectMean! with { Exact = R(9) } };
                break;
            case "question-mean":
                badSubject = subject with
                {
                    QuestionMeans = [subject.QuestionMeans[0] with
                    {
                        Mean = subject.QuestionMeans[0].Mean! with { Exact = R(9) }
                    }]
                };
                break;
            case "answer-order":
                badSubject = subject with { Answers = subject.Answers.Reverse().ToArray() };
                break;
            case "question-score-order":
                badSubject = subject with { QuestionScores = subject.QuestionScores.Reverse().ToArray() };
                break;
            case "subject-rank":
                badSubject = subject with
                {
                    Scores = [subject.Scores[0] with { Rank = 2 }, subject.Scores[1]],
                    Answers = [subject.Answers[0] with { Rank = 2 }, subject.Answers[1]],
                    QuestionScores = [subject.QuestionScores[0] with { Rank = 2 }, subject.QuestionScores[1]]
                };
                corrupted = report with
                {
                    StudentTotals = [report.StudentTotals[0] with
                    {
                        Subjects = [report.StudentTotals[0].Subjects[0] with { Rank = 2 }]
                    }, report.StudentTotals[1]]
                };
                break;
        }
        if (!ReferenceEquals(badSubject, subject)) corrupted = corrupted with { Subjects = [badSubject] };

        Assert.Throws<ArgumentException>(() => new ReportWorkbookWriter().Write(corrupted));
    }

}
