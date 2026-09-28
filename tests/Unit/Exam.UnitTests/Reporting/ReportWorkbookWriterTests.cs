using System.Numerics;
using ClosedXML.Excel;
using Exam.Contracts.Grading;
using Exam.Domain.Reporting;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Reporting;

namespace Exam.UnitTests.Reporting;

/// <summary>Workbook readback uses synthetic examples, never operational records.</summary>
public sealed partial class ReportWorkbookWriterTests
{
    [Fact]
    public void Workbook_HasFiveSheetsAndPreservesRowsTypesFooterAndIndependentColors()
    {
        var report = ReportCalculator.Calculate(TwoParticipantInput());
        var bytes = new ReportWorkbookWriter().Write(report);

        Assert.True(bytes.Length > 0);
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        Assert.Equal(5, workbook.Worksheets.Count);
        Assert.Equal(["学生成绩", "Synthetic-成绩", "Synthetic-答案", "Synthetic-小题得分", "班级平均分"],
            workbook.Worksheets.Select(sheet => sheet.Name));
        Assert.Equal(string.Empty, workbook.Properties.Author);
        Assert.Equal(string.Empty, workbook.Properties.LastModifiedBy);

        var totals = workbook.Worksheet(1);
        Assert.Contains("满分10", totals.Cell(1, 5).GetString());
        Assert.Equal("0002", totals.Cell(2, 2).GetString());
        Assert.Equal(XLDataType.Text, totals.Cell(2, 2).DataType);
        Assert.Equal("01", totals.Cell(2, 4).GetString());
        Assert.Equal(XLDataType.Text, totals.Cell(2, 4).DataType);
        Assert.Equal(10d, totals.Cell(2, 5).GetDouble());
        Assert.Equal(XLDataType.Number, totals.Cell(2, 5).DataType);
        Assert.Equal(0d, totals.Cell(3, 5).GetDouble());
        Assert.Equal("平均分", totals.Cell(4, 1).GetString());
        Assert.Single(totals.MergedRanges, range => range.RangeAddress.FirstAddress.RowNumber == 4);
        Assert.Equal("10.00", totals.Cell(4, 5).GetFormattedString());
        Assert.True(totals.Cell(4, 6).IsEmpty());

        var scores = workbook.Worksheet(2);
        Assert.Equal("平均分", scores.Cell(4, 1).GetString());
        Assert.Equal("10.00", scores.Cell(4, 5).GetFormattedString());

        var answers = workbook.Worksheet(3);
        Assert.False(answers.Cell(2, 5).HasFormula);
        Assert.Equal("=1+1", answers.Cell(2, 5).GetString());
        Assert.Equal(XLDataType.Text, answers.Cell(2, 5).DataType);
        Assert.Equal(XLColor.Red, answers.Cell(2, 5).Style.Font.FontColor);
        Assert.Equal("https://example.invalid", answers.Cell(3, 5).GetString());
        Assert.False(answers.Cell(3, 5).HasHyperlink);
        Assert.True(answers.Cell(4, 1).IsEmpty());

        var questionScores = workbook.Worksheet(4);
        Assert.Equal(XLColor.Black, questionScores.Cell(2, 5).Style.Font.FontColor);
        Assert.Equal(XLColor.Red, questionScores.Cell(3, 5).Style.Font.FontColor);
        Assert.Equal("10.00", questionScores.Cell(4, 5).GetFormattedString());

        var classes = workbook.Worksheet(5);
        Assert.Equal("01", classes.Cell(2, 1).GetString());
        Assert.Equal(XLDataType.Text, classes.Cell(2, 1).DataType);
        Assert.Equal("10", classes.Cell(3, 1).GetString());
        Assert.Equal("无有效样本", classes.Cell(3, 2).GetString());
        Assert.True(classes.Cell(3, 3).IsEmpty());
    }

    [Fact]
    public void LongExactScore_IsTextAndRetainsAllDigits()
    {
        var veryLongScore = ExactRational.FromInteger(BigInteger.Parse("123456789012345678901234567890"));
        var input = TwoParticipantInput();
        var subject = input.Subjects[0] with
        {
            MaximumScore = veryLongScore,
            Questions = [input.Subjects[0].Questions[0] with { MaximumScore = veryLongScore }]
        };
        var first = input.Submissions[0] with
        {
            Subjects = [input.Submissions[0].Subjects[0] with
            {
                FinalScore = veryLongScore,
                Questions = [input.Submissions[0].Subjects[0].Questions[0] with { Score = veryLongScore }]
            }],
            TotalScore = veryLongScore
        };
        input = input with { Subjects = [subject], Submissions = [first, input.Submissions[1]] };

        using var workbook = new XLWorkbook(new MemoryStream(
            new ReportWorkbookWriter().Write(ReportCalculator.Calculate(input))));
        Assert.Equal(XLDataType.Text, workbook.Worksheet(1).Cell(2, 5).DataType);
        Assert.Equal("123456789012345678901234567890", workbook.Worksheet(1).Cell(2, 5).GetString());
        Assert.Equal(XLDataType.Text, workbook.Worksheet(2).Cell(4, 5).DataType);
        Assert.Equal("123456789012345678901234567890.00", workbook.Worksheet(2).Cell(4, 5).GetString());
    }

    [Fact]
    public void DuplicateAndInvalidSubjectNames_ProduceUniqueExcelSafeSheets()
    {
        var input = TwoParticipantInput();
        var firstSubject = input.Subjects[0] with { DisplayName = "Synthetic/Math" };
        var secondSubject = new ReportSubject("second", "Synthetic/Math", 2, R(20),
            [new ReportQuestion("q-second-1", 1, R(10)), new ReportQuestion("q-second-2", 2, R(10))]);
        var newSubmissions = input.Submissions.Select(submission =>
        {
            var additional = new ReportSubjectGrade("second", R(1),
                [
                    new ReportQuestionGrade("q-second-1", "X", AnswerOutcome.Correct, R(1)),
                    new ReportQuestionGrade("q-second-2", "", AnswerOutcome.Blank, R(0))
                ]);
            return submission with
            {
                Subjects = [.. submission.Subjects, additional],
                TotalScore = submission.TotalScore + R(1)
            };
        }).ToArray();
        input = input with { Subjects = [firstSubject, secondSubject], Submissions = newSubmissions };

        using var workbook = new XLWorkbook(new MemoryStream(
            new ReportWorkbookWriter().Write(ReportCalculator.Calculate(input))));
        Assert.Equal(8, workbook.Worksheets.Count);
        Assert.Equal(8, workbook.Worksheets.Select(sheet => sheet.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(workbook.Worksheets, sheet =>
        {
            Assert.True(sheet.Name.Length <= 31);
            Assert.DoesNotContain('/', sheet.Name);
        });
        Assert.Equal("第1题答案", workbook.Worksheet(6).Cell(1, 5).GetString());
        Assert.Equal("第2题答案", workbook.Worksheet(6).Cell(1, 6).GetString());
        Assert.Equal("第2题得分（满分10）", workbook.Worksheet(7).Cell(1, 6).GetString());
    }

    private static ReportCalculationInput TwoParticipantInput()
    {
        var subject = new ReportSubject("first", "Synthetic", 1, R(10),
            [new ReportQuestion("q-first", 1, R(10))]);
        ReportParticipant[] participants = [
            new("one", "0002", "Synthetic candidate", "01"),
            new("two", "10", "Synthetic candidate", "10")
        ];
        ReportSubmission[] submissions = [
            new("submission-one", "synthetic-exam", "one", 1,
                [new ReportSubjectGrade("first", R(10),
                    [new ReportQuestionGrade("q-first", "=1+1", AnswerOutcome.PartialCorrect, R(10))])], R(10)),
            new("submission-two", "synthetic-exam", "two", 1,
                [new ReportSubjectGrade("first", R(0),
                    [new ReportQuestionGrade("q-first", "https://example.invalid", AnswerOutcome.Wrong, R(0))])], R(0))
        ];
        return new("synthetic-exam", 1, [subject], participants, submissions);
    }

    private static ExactRational R(int value) => ExactRational.FromInteger(new BigInteger(value));
}
