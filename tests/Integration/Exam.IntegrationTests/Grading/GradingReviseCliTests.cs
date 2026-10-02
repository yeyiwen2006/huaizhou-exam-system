using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Exam.IntegrationTests.Grading;

/// <summary>
/// `grading revise`（E01-C49 的专用 CLI）的用法校验：缺必需选项或选项非法都是用法错误，
/// 在连服务器之前就退出 2，不发出请求。
///
/// 只需要已构建的 CLI，不需要数据库或 TeX。
/// </summary>
public sealed class GradingReviseCliTests
{
    [Fact]
    public async Task Revise_WithoutPackageOrReasonOrWithAnUnknownOption_IsUsageError()
    {
        var missingReason = await RunCliAsync(["grading", "revise", "synthetic-exam", "--package", "."]);
        Assert.Equal(2, missingReason.Exit);

        var missingPackage = await RunCliAsync(["grading", "revise", "synthetic-exam", "--reason", "合成用例"]);
        Assert.Equal(2, missingPackage.Exit);

        var unknown = await RunCliAsync(["grading", "revise", "synthetic-exam",
            "--package", ".", "--reason", "合成用例", "--node-cert", "node-a=cert.cer"]);
        Assert.Equal(2, unknown.Exit);

        var badVersion = await RunCliAsync(["grading", "revise", "synthetic-exam",
            "--package", ".", "--reason", "合成用例", "--version", "abc"]);
        Assert.Equal(2, badVersion.Exit);
    }

    [Fact]
    public async Task UnknownGradingAction_IsUsageError()
    {
        var result = await RunCliAsync(["grading", "publish", "synthetic-exam"]);

        Assert.Equal(2, result.Exit);
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
