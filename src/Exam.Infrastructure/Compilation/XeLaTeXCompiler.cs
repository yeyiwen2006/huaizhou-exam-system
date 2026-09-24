using Exam.Contracts.Importing;
using Exam.Domain.Importing;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Infrastructure.Compilation;

/// <summary>XeLaTeX 可执行文件与发行版。</summary>
public sealed record XeLaTeXToolchain(string ExecutablePath, XeLaTeXPlatform Platform)
{
    /// <summary>
    /// 在 PATH 中查找 xelatex；找不到返回 null。只做查找，不执行任何命令。
    /// 调用方也可以直接给出绝对路径（例如考务主机上固定安装的位置）。
    /// </summary>
    public static XeLaTeXToolchain? TryDetect(
        XeLaTeXPlatform platform = XeLaTeXPlatform.MiKTeX,
        string executableName = "xelatex")
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var candidates = OperatingSystem.IsWindows()
            ? new[] { executableName + ".exe", executableName }
            : [executableName];

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in candidates)
            {
                string full;
                try
                {
                    full = Path.GetFullPath(Path.Combine(directory.Trim(), candidate));
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(full))
                {
                    return new XeLaTeXToolchain(full, platform);
                }
            }
        }

        return null;
    }
}

/// <summary>
/// 逐题编译一个包内 TeX 文件。执行指南第 164 行与 D02 任务卡的要求在这里落地：
/// 独立目录编译、以包根目录解析相对路径、禁外部命令、超时与路径校验、保留原版 PDF、错误可定位。
///
/// 不写回材料目录：OutputDirectory 必须是包根之外的目录，工作目录仍是包根目录，
/// 因此 \graphicspath 与清单里的相对路径按包内结构解析，而编译产物落在独立目录。
/// 路径一律经 PathBoundary 校验：越出包根或经过符号链接/目录联接的路径会被拒绝。
/// </summary>
public sealed class XeLaTeXCompiler
{
    /// <summary>日志读取上限，防止异常日志占满内存。</summary>
    public const int MaxLogBytes = 8 * 1024 * 1024;

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly IExternalProcessRunner _runner;
    private readonly XeLaTeXToolchain _toolchain;
    private readonly TimeSpan _defaultTimeout;

    public XeLaTeXCompiler(
        IExternalProcessRunner runner,
        XeLaTeXToolchain toolchain,
        TimeSpan? defaultTimeout = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _toolchain = toolchain ?? throw new ArgumentNullException(nameof(toolchain));

        var timeout = defaultTimeout ?? DefaultTimeout;
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(6))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultTimeout));
        }

        _defaultTimeout = timeout;
    }

    public async Task<TeXCompileResult> CompileAsync(
        TeXCompileRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var packageRoot = RequireDirectory(request.PackageRoot, nameof(request.PackageRoot));
        var outputDirectory = RequireOutsidePackageRoot(request.OutputDirectory, packageRoot);
        var sourcePath = PathBoundary.ResolveWithinRoot(
            packageRoot,
            Path.Combine(packageRoot, request.SourceRelativePath),
            $"题目源文件必须位于导入包内，且路径不能经过符号链接或目录联接：{request.SourceRelativePath}");

        if (!File.Exists(sourcePath))
        {
            return TeXCompileResult.NotStarted(new TeXIssue(
                TeXIssueCode.MissingFile,
                $"包内找不到题目源文件：{request.SourceRelativePath}。",
                request.SourceRelativePath));
        }

        Directory.CreateDirectory(outputDirectory);

        var timeout = request.Timeout ?? _defaultTimeout;
        var arguments = XeLaTeXInvocation.BuildArguments(
            _toolchain.Platform,
            request.SourceRelativePath,
            outputDirectory);

        ExternalProcessResult process;
        var timedOut = false;
        try
        {
            process = await _runner.RunAsync(
                _toolchain.ExecutablePath,
                arguments,
                new Dictionary<string, string>(),
                timeout,
                cancellationToken,
                packageRoot);
        }
        catch (TimeoutException exception)
        {
            timedOut = true;
            process = new ExternalProcessResult(-1, string.Empty, exception.Message);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return TeXCompileResult.NotStarted(new TeXIssue(
                TeXIssueCode.ToolchainMissing,
                $"找不到可用的 XeLaTeX：{exception.Message}。请在该主机安装 TeX 发行版并配置路径。"));
        }
        catch (InvalidOperationException exception)
        {
            return TeXCompileResult.NotStarted(new TeXIssue(
                TeXIssueCode.ToolchainMissing,
                $"无法启动 XeLaTeX：{exception.Message}"));
        }

        // 日志与 PDF 都按 jobname（源文件基名）命名。
        var jobName = Path.GetFileNameWithoutExtension(sourcePath);
        var logPath = Path.Combine(outputDirectory, jobName + ".log");
        var pdfPath = Path.Combine(outputDirectory, jobName + ".pdf");

        var report = XeLaTeXLogReader.Read(ReadLog(logPath));
        var outputPdfProduced = File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 0;

        var outcome = XeLaTeXInvocation.Evaluate(process.ExitCode, timedOut, outputPdfProduced, report);

        return new TeXCompileResult(
            outcome.Succeeded,
            process.ExitCode,
            timedOut,
            report,
            outputPdfProduced ? pdfPath : null,
            outcome.Issues);
    }

    /// <summary>读取日志；不存在或不可读时返回 null（由日志解析器判定为「没有写出 PDF」）。</summary>
    private static string? ReadLog(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return null;
        }

        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = (int)Math.Min(stream.Length, MaxLogBytes);
        var buffer = new byte[length];
        var read = stream.ReadAtLeast(buffer, length, throwOnEndOfStream: false);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static string RequireDirectory(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("必须是绝对路径。", parameterName);
        }

        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"目录不存在：{path}");
        }

        return Path.GetFullPath(path);
    }

    private static string RequireOutsidePackageRoot(string outputDirectory, string packageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory, nameof(outputDirectory));
        if (!Path.IsPathFullyQualified(outputDirectory))
        {
            throw new ArgumentException("输出目录必须是绝对路径。", nameof(outputDirectory));
        }

        var full = Path.GetFullPath(outputDirectory);
        var relative = Path.GetRelativePath(packageRoot, full);
        var insidePackage = relative == "." ||
                            (!Path.IsPathFullyQualified(relative) &&
                             !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                             relative != "..");

        if (insidePackage)
        {
            throw new ArgumentException(
                $"编译产物不得写回导入包目录，材料目录必须保持零改动：{full}",
                nameof(outputDirectory));
        }

        return full;
    }
}
