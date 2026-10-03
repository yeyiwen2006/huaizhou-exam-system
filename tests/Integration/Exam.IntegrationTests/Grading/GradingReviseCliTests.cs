using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Exam.IntegrationTests.Grading;

/// <summary>
/// `grading revise`（只改答案与计分规则的专用 CLI）的用法校验：缺必需选项或选项非法都是用法错误，
/// 在连服务器之前就退出 2。只需要已构建的 CLI，不需要数据库或 TeX。
/// </summary>
public sealed class GradingReviseCliTests
{
    [Fact]
    public async Task Revise_WithoutPackageOrReasonOrWithAnUnknownOption_IsUsageError()
    {
        Assert.Equal(2, (await RunCliAsync(["grading", "revise", "合成考试"])).Exit);
        Assert.Equal(2, (await RunCliAsync(["grading", "revise", "合成考试", "--package", "."])).Exit);
        Assert.Equal(2, (await RunCliAsync(["grading", "revise", "合成考试", "--reason", "合成说明"])).Exit);
        Assert.Equal(2, (await RunCliAsync(["grading", "revise", "合成考试",
            "--package", ".", "--reason", "合成说明", "--node-cert", "node-a=cert.cer"])).Exit);
    }

    [Fact]
    public async Task UnknownGradingAction_IsUsageError()
    {
        Assert.Equal(2, (await RunCliAsync(["grading", "publish", "合成考试"])).Exit);
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
