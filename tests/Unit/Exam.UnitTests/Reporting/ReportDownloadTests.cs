using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Exam.Reporting;

namespace Exam.UnitTests.Reporting;

// Every identifier, byte sequence and destination in these cases is synthetic.
public sealed partial class ReportWorkbookWriterTests
{
    [Theory]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("snapshot")]
    [InlineData("type")]
    [InlineData("identity")]
    [InlineData("oversize")]
    public async Task Download_RejectsInvalidOrTruncatedResponse(string corruption)
    {
        byte[] bytes = [80,75,3,4,1,2,3];
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new(ReportWorkbookDownload.ContentType);
        response.Headers.Add("X-Exam-Server-Id", "synthetic-server");
        response.Headers.Add("X-Exam-Node-Id", "synthetic-node");
        response.Headers.Add("X-Exam-Server-Epoch", "1");
        response.Headers.Add("X-Exam-Report-Snapshot", corruption == "snapshot" ? "old" : "current");
        response.Headers.Add("X-Exam-Workbook-Sha256", corruption == "hash" ? "invalid" : Convert.ToHexString(SHA256.HashData(bytes)));
        if (corruption == "length") response.Content.Headers.ContentLength = bytes.Length + 1;
        if (corruption == "oversize") response.Content.Headers.ContentLength = ReportWorkbookDownload.MaximumBytes + 1L;
        if (corruption == "type") response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
        if (corruption == "identity") response.Headers.Remove("X-Exam-Node-Id");
        await Assert.ThrowsAsync<InvalidDataException>(() => ReportWorkbookDownload.ReadAsync(response,"current"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Download_CancellationBeforeOrAfterValidationPreservesOriginal(bool cancelAtCommit)
    {
        await WithExportDirectory(async directory =>
        {
            var path=Path.Combine(directory,"synthetic.xlsx");
            await File.WriteAllTextAsync(path,"synthetic original");
            using var cancel=new CancellationTokenSource();
            if (!cancelAtCommit) cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>ReportWorkbookDownload.SaveAsync(
                new([80,75,3,4],"synthetic-server","synthetic-node",1,"current"),path,
                ()=>{ cancel.Cancel(); return Task.CompletedTask; },cancel.Token));
            Assert.Equal("synthetic original",await File.ReadAllTextAsync(path));
            Assert.Single(Directory.EnumerateFiles(directory));
        });
    }

    [Fact]
    public async Task Download_LockedDestinationPreservesOriginalAndRemovesTemporaryFile()
    {
        await WithExportDirectory(async directory =>
        {
            var path=Path.Combine(directory,"synthetic.xlsx");
            await File.WriteAllTextAsync(path,"synthetic original");
            using (var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None))
            {
                var error=await Record.ExceptionAsync(()=>ReportWorkbookDownload.SaveAsync(
                    new([80,75,3,4],"synthetic-server","synthetic-node",1,"current"),path,()=>Task.CompletedTask));
                Assert.True(error is IOException or UnauthorizedAccessException);
            }
            Assert.Equal("synthetic original",await File.ReadAllTextAsync(path));
            Assert.Single(Directory.EnumerateFiles(directory));
        });
    }
}
