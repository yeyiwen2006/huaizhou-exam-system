using Exam.Application.Reporting;
using Exam.Contracts.Common;
using Exam.Contracts.Reporting;
using Exam.Domain.Common;
using Exam.Domain.Reporting;
using Exam.Domain.Scoring;
using Exam.Reporting;

namespace Exam.UnitTests.Reporting;

public sealed partial class ReportWorkbookWriterTests
{
    [Fact]
    public void Contract_SyntheticSampleDeserializesAndExportsAllFiveTables()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"Huaizhou.ExamSystem.sln"))) root=root.Parent;
        Assert.NotNull(root);
        var json=File.ReadAllText(Path.Combine(root.FullName,"docs","samples","g-reporting","published-report.synthetic.json"));
        var published=System.Text.Json.JsonSerializer.Deserialize<PublishedReport>(json,JsonDefaults.Create())!;
        Assert.Equal(1,published.SchemaVersion); Assert.Equal(ReportState.Ready,published.Status.State);
        var report=ReportJson.Read(published.Result);
        using var workbook=new ClosedXML.Excel.XLWorkbook(new MemoryStream(new Exam.Infrastructure.Reporting.ReportWorkbookWriter().Write(report)));
        Assert.Equal(5,workbook.Worksheets.Count); Assert.Equal(3d,workbook.Worksheet(1).Cell(2,5).GetDouble());
        Assert.Equal("0001",workbook.Worksheet(1).Cell(2,2).GetString());
    }
    [Fact]
    public async Task Publication_RecheckRejectsVersionChangedDuringCalculation()
    {
        var input = TwoParticipantInput();
        var source = new SequenceReportSource(new(Status("first"), input), new(Status("second"), input));
        var error = await Assert.ThrowsAsync<DomainRuleException>(() => new ReportPublicationService(source).ReadAsync(input.ExamId));
        Assert.Equal(ErrorCodes.VersionConflict, error.Code);
    }
    [Fact]
    public async Task Publication_RecheckRejectsCollectionInvalidation()
    {
        var input = TwoParticipantInput();
        var source = new SequenceReportSource(new(Status("first"), input),
            new(Status("first") with { State=ReportState.Collecting, SnapshotId=null }, null));
        Assert.Equal(ErrorCodes.ReportNotReady, (await Assert.ThrowsAsync<DomainRuleException>(() =>
            new ReportPublicationService(source).ReadAsync(input.ExamId))).Code);
    }
    [Fact]
    public async Task Publication_ExactPayloadRoundTripPreservesAllTables()
    {
        var input = TwoParticipantInput(); var snapshot = new ReportSourceSnapshot(Status("first"),input);
        var published = await new ReportPublicationService(new SequenceReportSource(snapshot,snapshot)).ReadAsync(input.ExamId);
        var read = ReportJson.Read(published.Result);
        Assert.Equal(1,published.SchemaVersion);
        Assert.Equal(10d, double.Parse(read.TotalMean!.Display, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("0002",read.StudentTotals[0].Participant.ExamNumber);
        Assert.Equal("=1+1",read.Subjects[0].Answers[0].Answers[0].Text);
    }
    [Theory]
    [InlineData("-201/200", "-1.005")]
    [InlineData("1/3", "1/3")]
    public void ReportTransport_PreservesNegativeAndRepeatingRationals(string rational, string expected)
    {
        var value=ReportJson.Parse(rational);
        var json=System.Text.Json.JsonSerializer.Serialize(value,ReportJson.Create());
        Assert.Equal(value, System.Text.Json.JsonSerializer.Deserialize<ExactRational>(json, ReportJson.Create()));
        Assert.Contains(expected, json);
    }
    [Fact]
    public async Task Download_StaleBeforeCommitPreservesExistingFile()
    {
        await WithExportDirectory(async directory =>
        {
            var destination=Path.Combine(directory,"synthetic.xlsx");
            await File.WriteAllTextAsync(destination,"synthetic original");
            var workbook=new DownloadedWorkbook([80,75,3,4],"synthetic-server","node-a",1,"first");
            await Assert.ThrowsAsync<InvalidDataException>(()=>ReportWorkbookDownload.SaveAsync(workbook,destination,
                ()=>throw new InvalidDataException("synthetic stale snapshot")));
            Assert.Equal("synthetic original",await File.ReadAllTextAsync(destination));
            Assert.Single(Directory.EnumerateFiles(directory));
        });
    }
    private static ReportStatus Status(string token)=>new("synthetic-exam",ReportState.Ready,"synthetic ready",
        2,2,2,1,1,1,token,[]);
    private sealed class SequenceReportSource(params ReportSourceSnapshot[] snapshots):IReportSource
    {
        private int _index;
        public ValueTask<ReportSourceSnapshot> ReadAsync(string examId,CancellationToken cancellationToken=default)=>
            ValueTask.FromResult(snapshots[Math.Min(_index++,snapshots.Length-1)]);
    }
}
