using ClosedXML.Excel;
using Exam.Domain.Reporting;
using Exam.Infrastructure.Reporting;

namespace Exam.UnitTests.Reporting;

public sealed partial class ReportWorkbookWriterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileExport_SeparateWritersCommitConcurrentSavesWithoutPartialResults(bool existing)
    {
        await WithExportDirectory(async directory =>
        {
            var destination = Path.Combine(directory, "synthetic.xlsx");
            if (existing) await File.WriteAllTextAsync(destination, "synthetic original");
            var report = ReportCalculator.Calculate(TwoParticipantInput());
            for (var round = 0; round < 4; round++)
            {
                await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
                    new ReportWorkbookFileWriter().WriteAsync(report, destination)));
                using var workbook = new XLWorkbook(destination);
                Assert.Equal(5, workbook.Worksheets.Count);
                Assert.Equal(10d, workbook.Worksheet(1).Cell(2, 5).GetDouble());
                Assert.Equal(10d, workbook.Worksheet(2).Cell(2, 5).GetDouble());
                Assert.Single(Directory.EnumerateFiles(directory));
            }
        });
    }
}
