using ClosedXML.Excel;
using Exam.Domain.Reporting;
using Exam.Infrastructure.Reporting;
using Exam.Reporting;

namespace Exam.UnitTests.Reporting;

// Synthetic files only. These exercise the actual download saver used by CLI and Admin.
public sealed partial class ReportWorkbookWriterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Download_ConcurrentSavesCommitCompleteWorkbooks(bool existing)
    {
        await WithExportDirectory(async directory =>
        {
            var destination=Path.Combine(directory,"synthetic.xlsx");
            if (existing) await File.WriteAllTextAsync(destination,"synthetic original");
            var bytes=new ReportWorkbookWriter().Write(ReportCalculator.Calculate(TwoParticipantInput()));
            var download=new DownloadedWorkbook(bytes,"synthetic-server","synthetic-node",1,"synthetic-current");
            for (var round=0;round<4;round++)
            {
                await Task.WhenAll(Enumerable.Range(0,8).Select(_=>
                    ReportWorkbookDownload.SaveAsync(download,destination,()=>Task.CompletedTask)));
                using var workbook=new XLWorkbook(destination);
                Assert.Equal(5,workbook.Worksheets.Count);
                Assert.Equal(10d,workbook.Worksheet(1).Cell(2,5).GetDouble());
                Assert.Equal(10d,workbook.Worksheet(2).Cell(2,5).GetDouble());
                Assert.Single(Directory.EnumerateFiles(directory));
            }
        });
    }

    [Fact]
    public async Task Download_QueuedSnapshotIsRevalidatedAfterEarlierCommit()
    {
        await WithExportDirectory(async directory=>
        {
            var destination=Path.Combine(directory,"synthetic.xlsx");
            var firstEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var current=new DownloadedWorkbook([80,75,3,4,1],"synthetic-server","synthetic-node",1,"synthetic-current");
            var obsolete=current with { Bytes=[80,75,3,4,2],SnapshotId="synthetic-obsolete" };
            var obsoleteIsReadable=true;
            var first=ReportWorkbookDownload.SaveAsync(current,destination,async()=>
            { firstEntered.TrySetResult(); await releaseFirst.Task; });
            Task? second=null;
            try
            {
                await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                second=ReportWorkbookDownload.SaveAsync(obsolete,destination,()=>obsoleteIsReadable
                    ? Task.CompletedTask : throw new InvalidDataException("Synthetic snapshot changed while queued."));
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!second.IsCompleted && Directory.EnumerateFiles(directory,"*.partial").Count()<2)
                    await Task.Delay(5,timeout.Token);
                obsoleteIsReadable=false;
            }
            finally { releaseFirst.TrySetResult(); await first; }
            await Assert.ThrowsAsync<InvalidDataException>(()=>second!);
            Assert.Equal(current.Bytes,await File.ReadAllBytesAsync(destination));
            Assert.Single(Directory.EnumerateFiles(directory));
        });
    }

    [Fact]
    public async Task Download_FailedFinalValidationReleasesCommitAndPreservesOriginal()
    {
        await WithExportDirectory(async directory=>
        {
            var destination=Path.Combine(directory,"synthetic.xlsx");
            await File.WriteAllTextAsync(destination,"synthetic original");
            var download=new DownloadedWorkbook([80,75,3,4],"synthetic-server","synthetic-node",1,"synthetic-current");
            await Assert.ThrowsAsync<InvalidDataException>(()=>ReportWorkbookDownload.SaveAsync(download,destination,
                ()=>throw new InvalidDataException("Synthetic report expired.")));
            Assert.Equal("synthetic original",await File.ReadAllTextAsync(destination));
            Assert.Single(Directory.EnumerateFiles(directory));
            await ReportWorkbookDownload.SaveAsync(download,destination,()=>Task.CompletedTask)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(download.Bytes,await File.ReadAllBytesAsync(destination));
            Assert.Single(Directory.EnumerateFiles(directory));
        });
    }

    [Fact]
    public async Task Download_CancelledQueuedSaveCannotValidateOrReplaceAnotherPendingCommit()
    {
        await WithExportDirectory(async directory=>
        {
            var destination=Path.Combine(directory,"synthetic.xlsx");
            await File.WriteAllTextAsync(destination,"synthetic original");
            var firstEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var download=new DownloadedWorkbook([80,75,3,4],"synthetic-server","synthetic-node",1,"synthetic-current");
            var first=ReportWorkbookDownload.SaveAsync(download,destination,async()=>
            { firstEntered.TrySetResult(); await releaseFirst.Task; });
            Task? second=null;
            using var cancellation=new CancellationTokenSource();
            var secondValidations=0;
            try
            {
                await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                second=ReportWorkbookDownload.SaveAsync(download,destination,()=>
                { Interlocked.Increment(ref secondValidations); return Task.CompletedTask; },cancellation.Token);
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (!second.IsCompleted && Directory.EnumerateFiles(directory,"*.partial").Count()<2)
                    await Task.Delay(5,timeout.Token);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>second);
                Assert.Equal(0,secondValidations);
                Assert.Equal("synthetic original",await File.ReadAllTextAsync(destination));
            }
            finally
            {
                cancellation.Cancel(); releaseFirst.TrySetResult();
                await first;
                if (second is not null) { try { await second; } catch(OperationCanceledException) { } }
            }
            Assert.Equal(download.Bytes,await File.ReadAllBytesAsync(destination));
            Assert.Single(Directory.EnumerateFiles(directory));
        });
    }
}
