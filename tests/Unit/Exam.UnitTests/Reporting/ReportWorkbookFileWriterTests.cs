using ClosedXML.Excel;
using Exam.Domain.Reporting;
using Exam.Infrastructure.Reporting;

namespace Exam.UnitTests.Reporting;

/// <summary>All workbooks and original-file sentinels are synthetic examples.</summary>
public sealed partial class ReportWorkbookWriterTests
{
    [Fact]
    public async Task FileExport_ReplacesOriginalWithCompleteReadableWorkbook() =>
        await WithExportDirectory(async directory =>
        {
            var destination = Path.Combine(directory, "synthetic.xlsx");
            await File.WriteAllTextAsync(destination, "synthetic original");

            await new ReportWorkbookFileWriter().WriteAsync(
                ReportCalculator.Calculate(TwoParticipantInput()), destination);

            using var workbook = new XLWorkbook(destination);
            Assert.Equal(5, workbook.Worksheets.Count);
            Assert.Equal("0002", workbook.Worksheet(1).Cell(2, 2).GetString());
            Assert.Equal(10d, workbook.Worksheet(1).Cell(2, 5).GetDouble());
            Assert.Single(Directory.EnumerateFiles(directory));
        });

    [Fact]
    public async Task FileExport_CancelledRequestPreservesOriginalAndLeavesNoTemporaryFile() =>
        await WithExportDirectory(async directory =>
        {
            var destination = Path.Combine(directory, "synthetic.xlsx");
            await File.WriteAllTextAsync(destination, "synthetic original");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new ReportWorkbookFileWriter().WriteAsync(
                    ReportCalculator.Calculate(TwoParticipantInput()), destination, cancellation.Token));

            Assert.Equal("synthetic original", await File.ReadAllTextAsync(destination));
            Assert.Single(Directory.EnumerateFiles(directory));
        });

    [Fact]
    public async Task FileExport_LockedDestinationPreservesOriginalAndCleansTemporaryFile() =>
        await WithExportDirectory(async directory =>
        {
            var destination = Path.Combine(directory, "synthetic.xlsx");
            await File.WriteAllTextAsync(destination, "synthetic original");
            await using (var locked = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite,
                FileShare.None))
            {
                var error = await Record.ExceptionAsync(() => new ReportWorkbookFileWriter().WriteAsync(
                    ReportCalculator.Calculate(TwoParticipantInput()), destination));
                Assert.True(error is IOException or UnauthorizedAccessException);
            }

            Assert.Equal("synthetic original", await File.ReadAllTextAsync(destination));
            Assert.Single(Directory.EnumerateFiles(directory));
        });

    [Fact]
    public async Task FileExport_ConcurrentSavesLeaveOneWholeWorkbook() =>
        await WithExportDirectory(async directory =>
        {
            var input = TwoParticipantInput();
            var submission = input.Submissions[0];
            var grade = submission.Subjects[0];
            var second = input with
            {
                Submissions = [submission with
                {
                    TotalScore = R(8),
                    Subjects = [grade with
                    {
                        FinalScore = R(8),
                        Questions = [grade.Questions[0] with { Score = R(8) }]
                    }]
                }, input.Submissions[1]]
            };
            var destination = Path.Combine(directory, "synthetic.xlsx");
            var writer = new ReportWorkbookFileWriter();
            await Task.WhenAll(
                writer.WriteAsync(ReportCalculator.Calculate(input), destination),
                writer.WriteAsync(ReportCalculator.Calculate(second), destination));

            using var workbook = new XLWorkbook(destination);
            Assert.Equal(5, workbook.Worksheets.Count);
            var score = workbook.Worksheet(1).Cell(2, 5).GetDouble();
            Assert.True(score is 8d or 10d);
            Assert.Equal(score, workbook.Worksheet(2).Cell(2, 5).GetDouble());
            Assert.Single(Directory.EnumerateFiles(directory));
        });

    private static async Task WithExportDirectory(Func<string, Task> action)
    {
        var directory = Directory.CreateTempSubdirectory("synthetic-report-").FullName;
        try { await action(directory); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
