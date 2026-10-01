using System.Numerics;
using System.Text;
using ClosedXML.Excel;
using Exam.Domain.Reporting;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Reporting;

namespace Exam.UnitTests.Reporting;

public sealed partial class ReportWorkbookWriterTests
{
    [Theory]
    [InlineData("100000000000000.000000000000001")]
    [InlineData("999999999999999.000000000000001")]
    [InlineData("123456789012345.123456789012345")]
    public void Review_LongFractionCannotPassThroughRoundedDecimalParsing(string exact)
    {
        var parts = exact.Split('.');
        var score = ExactRational.Create(BigInteger.Parse(parts[0] + parts[1]), BigInteger.Pow(10, parts[1].Length));
        var input = TwoParticipantInput();
        var subject = input.Subjects[0] with
        {
            MaximumScore = score,
            Questions = [input.Subjects[0].Questions[0] with { MaximumScore = score }]
        };
        var first = input.Submissions[0] with
        {
            Subjects = [input.Submissions[0].Subjects[0] with
            {
                FinalScore = score,
                Questions = [input.Submissions[0].Subjects[0].Questions[0] with { Score = score }]
            }], TotalScore = score
        };
        var result = ReportCalculator.Calculate(input with { Subjects = [subject], Submissions = [first, input.Submissions[1]] });
        using var book = new XLWorkbook(new MemoryStream(new ReportWorkbookWriter().Write(result)));
        foreach (var sheet in new[] { 1, 2, 4 })
        {
            Assert.Equal(XLDataType.Text, book.Worksheet(sheet).Cell(2, 5).DataType);
            Assert.Equal(exact, book.Worksheet(sheet).Cell(2, 5).GetString());
        }
    }

    [Theory]
    [InlineData("'literal")]
    [InlineData("=1+1")]
    [InlineData("+1+1")]
    [InlineData("-1+1")]
    [InlineData("@SUM(A1:A2)")]
    public void Review_AnswerTextRoundTripsWithoutExcelInterpretation(string text)
    {
        var input = TwoParticipantInput();
        var first = input.Submissions[0] with
        {
            Subjects = [input.Submissions[0].Subjects[0] with
            {
                Questions = [input.Submissions[0].Subjects[0].Questions[0] with { AnswerText = text }]
            }]
        };
        using var book = new XLWorkbook(new MemoryStream(new ReportWorkbookWriter().Write(
            ReportCalculator.Calculate(input with { Submissions = [first, input.Submissions[1]] }))));
        var cell = book.Worksheet(3).Cell(2, 6);
        Assert.Equal(text, cell.GetString());
        Assert.Equal(XLDataType.Text, cell.DataType);
        Assert.False(cell.HasFormula);
        Assert.False(cell.HasHyperlink);
    }

    [Fact]
    public void Review_SheetNameTruncationDoesNotSplitUnicodePairs()
    {
        var input = TwoParticipantInput();
        var subject = input.Subjects[0] with { DisplayName = new string('数', 30) + "📘" };
        using var book = new XLWorkbook(new MemoryStream(new ReportWorkbookWriter().Write(
            ReportCalculator.Calculate(input with { Subjects = [subject] }))));
        var strict = new UTF8Encoding(false, true);
        Assert.All(book.Worksheets, sheet =>
        {
            Assert.InRange(sheet.Name.Length, 1, 31);
            Assert.NotEmpty(strict.GetBytes(sheet.Name));
        });
        Assert.Equal(5, book.Worksheets.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
