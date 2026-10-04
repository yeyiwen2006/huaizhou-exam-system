using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Exam.IntegrationTests.Importing;

/// <summary>
/// 开考后修订相关 CLI 的**用法**校验：取回源材料、以及「原因与变更题号只在预览时给」。
/// 都是连服务器之前就退出的用法错误，只需要已构建的 CLI，不需要数据库或 TeX。
/// </summary>
public sealed class ImportRevisionCliTests
{
    [Fact]
    public async Task Materials_RequiresAnExamNameAndANewOutputDirectory()
    {
        Assert.Equal(2, (await RunCliAsync(["import", "materials", "合成考试"])).Exit);
        Assert.Equal(2, (await RunCliAsync(["import", "materials", "合成考试", "--confirm", "--output", "out"])).Exit);
        Assert.Equal(2, (await RunCliAsync(["import", "materials", "合成考试", "--output", "out", "--changed", "q1"])).Exit);
    }

    [Fact]
    public async Task Publish_DoesNotAcceptReasonOrChangedQuestions()
    {
        // 发布只重放已保存的预览：原因与变更题号必须在 preview 时定稿，否则跨进程重放的请求就不是同一份。
        Assert.Equal(2, (await RunCliAsync(["import", "publish", "preview-dir", "--confirm", "--reason", "说明"])).Exit);
        Assert.Equal(2, (await RunCliAsync(["import", "publish", "preview-dir", "--confirm", "--changed", "q1"])).Exit);
    }

    [Fact]
    public async Task Preview_StillRequiresExamAndOutput()
    {
        Assert.Equal(2, (await RunCliAsync(["import", "preview", "source-dir", "--reason", "说明"])).Exit);
        Assert.Equal(2, (await RunCliAsync(["import", "preview", "source-dir", "--exam", "合成考试"])).Exit);
    }

    private static async Task<(int Exit, string Output, string Error)> RunCliAsync(string[] args)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Huaizhou.ExamSystem.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var host = Path.Combine(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")),
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(root.FullName, "src", "Exam.Cli", "bin", "Release", "net10.0", "Exam.Cli.dll"));
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await output, await error);
    }
}
