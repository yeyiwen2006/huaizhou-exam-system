using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.Importing;

public sealed class CompilationProcessLimitsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_ResourceLimitTerminatesProcess(bool memoryLimit)
    {
        var root = Path.Combine(Path.GetTempPath(), "exam-process-limit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "synthetic.log"), new string('x', 1024));
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
                : "/bin/sh";
            string[] arguments = OperatingSystem.IsWindows()
                ? ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"]
                : ["-c", "sleep 30"];
            var started = System.Diagnostics.Stopwatch.StartNew();
            await Assert.ThrowsAsync<ProcessResourceLimitException>(() => new ExternalProcessRunner().RunAsync(
                executable, arguments, new Dictionary<string, string>(), TimeSpan.FromSeconds(10),
                workingDirectory: root,
                limits: new ExternalProcessLimits(memoryLimit ? 1 : long.MaxValue,
                    memoryLimit ? long.MaxValue : 1, root)));
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
