using System.Diagnostics;
using System.Text;

namespace Exam.IntegrationTests.Cli;

/// <summary>真 CLI 进程运行器：只使用合成凭据与环回地址，供 CLI 端到端用例共用。</summary>
internal static class CliProcess
{
    public static string Repository()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Huaizhou.ExamSystem.sln"))) root = root.Parent;
        return root?.FullName ?? throw new InvalidOperationException("Repository root missing.");
    }

    public static async Task<(int Exit, string Out, string Error)> RunAsync(params string[] args)
        => await RunWithTokenAsync("synthetic-sensitive-sentinel", args);

    public static async Task<(int Exit, string Out, string Error)> RunWithTokenAsync(string token, params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = new UTF8Encoding(false, true) };
        start.ArgumentList.Add(Path.Combine(Repository(), "src/Exam.Cli/bin/Release/net10.0/Exam.Cli.dll"));
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("EXAM_", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(key);
        start.Environment["EXAM_ALLOW_INSECURE_LOOPBACK"] = "true";
        start.Environment["EXAM_OPERATOR_TOKEN"] = token;
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(deadline.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        return (process.ExitCode, await output, await error);
    }

    /// <summary>写一份临时 JSON 输入文件，返回其路径；调用方负责删除。</summary>
    public static string WriteInput(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"synthetic-cli-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json, new UTF8Encoding(false));
        return path;
    }
}
