using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ClosedXML.Excel;
using Exam.Application.Reporting;
using Exam.Contracts.Common;
using Exam.Contracts.Reporting;

namespace Exam.IntegrationTests.Submissions;

public sealed partial class PostgresSubmissionStoreTests
{
    [Fact]
    public async Task Reporting_ActualCliQueriesAndExportsOverRealHttpAndPreservesFileOnFailure()
    {
        var (context, versions) = await SeedReportExamAsync(); FinishReportExam(context.ExamId, versions);
        var reports = new ReportPublicationService(ReportSource());
        var authoritative = await reports.ReadAsync(context.ExamId);
        await using var app = await ReportAppAsync(reports, realHttp: true);
        var directory = Directory.CreateTempSubdirectory("g-cli-synthetic-").FullName;
        try
        {
            var server = app.Urls.Single();
            var denied = await RunReportCliAsync(server, ["reports", "status", context.ExamId], authenticated: false);
            Assert.Equal(3, denied.Exit);
            var shown = await RunReportCliAsync(server, ["reports", "show", context.ExamId]);
            Assert.Equal(0, shown.Exit);
            var shownResult = JsonSerializer.Deserialize<ApiEnvelope<PublishedReport>>(shown.Output, JsonDefaults.Create());
            Assert.Equal(authoritative.Status.SnapshotId, shownResult!.Result!.Status.SnapshotId);
            Assert.Equal(authoritative.Result.GetRawText(), shownResult.Result.Result.GetRawText());
            var destination = Path.Combine(directory, "synthetic.xlsx");
            await File.WriteAllTextAsync(destination, "synthetic original");
            var exported = await RunReportCliAsync(server, ["reports", "export", context.ExamId, destination]);
            Assert.Equal(0, exported.Exit);
            using (var workbook = new XLWorkbook(destination))
            {
                Assert.Equal(5, workbook.Worksheets.Count);
                Assert.Equal(3d, workbook.Worksheet(1).Cell(2,5).GetDouble());
            }
            var bytes = await File.ReadAllBytesAsync(destination);
            await app.StopAsync();
            var failed = await RunReportCliAsync(server, ["reports", "export", context.ExamId, destination]);
            Assert.Equal(5, failed.Exit);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
            Assert.Single(Directory.EnumerateFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private async Task<(int Exit, string Output)> RunReportCliAsync(string server, string[] args, bool authenticated = true)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Huaizhou.ExamSystem.sln"))) root=root.Parent;
        Assert.NotNull(root);
        var host = Path.Combine(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")),
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var start = new ProcessStartInfo(host) { UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true };
        start.StandardOutputEncoding = new System.Text.UTF8Encoding(false, true);
        start.StandardErrorEncoding = new System.Text.UTF8Encoding(false, true);
        start.ArgumentList.Add(Path.Combine(root.FullName, "src", "Exam.Cli", "bin", "Release", "net10.0", "Exam.Cli.dll"));
        foreach (var argument in args) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--server"); start.ArgumentList.Add(server);
        start.Environment["EXAM_ALLOW_INSECURE_LOOPBACK"]="true";
        start.Environment.Remove("EXAM_OPERATOR_TOKEN");
        // 不读取本机用户保存的 CLI 登录会话。
        start.Environment["EXAM_CLI_SESSION_DIRECTORY"]=Path.Combine(Path.GetTempPath(), "exam-cli-empty-" + Guid.NewGuid().ToString("N"));
        if (authenticated) start.Environment["EXAM_OPERATOR_TOKEN"]=ReportTestToken;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { process.Kill(entireProcessTree: true); throw; }
        await error;
        return (process.ExitCode, await output);
    }
}
