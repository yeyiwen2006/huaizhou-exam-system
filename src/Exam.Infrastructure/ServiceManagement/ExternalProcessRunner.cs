using System.Diagnostics;
using System.Text;

namespace Exam.Infrastructure.ServiceManagement;

public sealed record ExternalProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed record ExternalProcessLimits(long MaxMemoryBytes, long MaxOutputBytes, string OutputDirectory);

public sealed class ProcessResourceLimitException(string message) : Exception(message);

public interface IExternalProcessRunner
{
    /// <summary>
    /// 运行外部工具。workingDirectory 为空时使用可执行文件所在目录；逐题编译需要以导入包根目录
    /// 为工作目录，包内的相对路径（含 \graphicspath）才会按包结构解析。
    /// </summary>
    Task<ExternalProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? workingDirectory = null,
        ExternalProcessLimits? limits = null);
}

public sealed class ExternalProcessRunner : IExternalProcessRunner
{
    private const int MaxCapturedCharacters = 64 * 1024;

    public async Task<ExternalProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? workingDirectory = null,
        ExternalProcessLimits? limits = null)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
        {
            throw new FileNotFoundException("外部工具不存在或不是绝对路径。", executable);
        }

        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(6))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (workingDirectory is not null
            && (!Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory)))
        {
            throw new DirectoryNotFoundException($"外部工具的工作目录不存在或不是绝对路径：{workingDirectory}");
        }
        if (limits is not null && (limits.MaxMemoryBytes <= 0 || limits.MaxOutputBytes <= 0
            || !Path.IsPathFullyQualified(limits.OutputDirectory) || !Directory.Exists(limits.OutputDirectory)))
        {
            throw new ArgumentException("资源上限必须为正数，产物目录必须是已存在的绝对路径。", nameof(limits));
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executable)!
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动外部工具。");
        }

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, linked.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, linked.Token);
        try
        {
            var exitTask = process.WaitForExitAsync(linked.Token);
            if (limits is not null)
            {
                while (!exitTask.IsCompleted)
                {
                    CheckResourceLimits(process, limits);
                    await Task.WhenAny(exitTask, Task.Delay(100, linked.Token));
                    linked.Token.ThrowIfCancellationRequested();
                }

                CheckResourceLimits(process, limits);
            }

            await exitTask;
        }
        catch (ProcessResourceLimitException)
        {
            linked.Cancel();
            await TerminateAndWaitAsync(process, stdoutTask, stderrTask);
            throw;
        }
        catch (OperationCanceledException)
        {
            await TerminateAndWaitAsync(process, stdoutTask, stderrTask);
            if (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"外部工具执行超过 {timeout}。");
            }

            throw;
        }
        catch
        {
            linked.Cancel();
            await TerminateAndWaitAsync(process, stdoutTask, stderrTask);
            throw;
        }

        return new ExternalProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static void CheckResourceLimits(Process process, ExternalProcessLimits limits)
    {
        process.Refresh();
        try
        {
            if (!process.HasExited && process.WorkingSet64 > limits.MaxMemoryBytes)
            {
                throw new ProcessResourceLimitException("编译进程内存超过上限，已终止。");
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // 进程可能在 HasExited 检查后自然退出；仍须检查已经写出的产物。
        }

        long size = 0;
        foreach (var path in Directory.EnumerateFiles(limits.OutputDirectory, "*", SearchOption.AllDirectories))
        {
            try
            {
                size += new FileInfo(path).Length;
            }
            catch (FileNotFoundException)
            {
                continue; // xdvipdfmx 可能已删掉刚枚举到的临时文件。
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            if (size > limits.MaxOutputBytes)
            {
                throw new ProcessResourceLimitException("编译产物超过空间上限，已终止。");
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var result = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            var remaining = MaxCapturedCharacters - result.Length;
            if (remaining > 0)
            {
                result.Append(buffer, 0, Math.Min(read, remaining));
            }
        }

        return result.ToString();
    }

    private static async Task TerminateAndWaitAsync(
        Process process,
        Task<string> stdoutTask,
        Task<string> stderrTask)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            // Preserve the original timeout/cancellation. The caller must treat the staging
            // directory as incomplete if the process tree could not be confirmed stopped.
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch (OperationCanceledException)
        {
            // The linked reader token is expected to be cancelled with the process wait.
        }
    }
}
