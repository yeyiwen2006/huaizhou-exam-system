using Exam.Contracts.Importing;
using Exam.Infrastructure.Compilation;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.Importing;

/// <summary>
/// 编译编排的用例。用替身进程运行器驱动，**不需要安装 TeX**，因此随现有流水线执行。
/// 真正需要 XeLaTeX 的用例在集成测试里以 Category=Latex 单独标记。
/// </summary>
public sealed class XeLaTeXCompilerTests : IDisposable
{
    private const string SourceRelativePath = "questions/合成样题_1.tex";
    private const string JobName = "合成样题_1";

    private readonly string _root;
    private readonly string _packageRoot;
    private readonly string _outputDirectory;
    private string? _runDirectory;

    public XeLaTeXCompilerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "exam-tex-" + Guid.NewGuid().ToString("N"));
        _packageRoot = Path.Combine(_root, "package");
        _outputDirectory = Path.Combine(_root, "output");

        Directory.CreateDirectory(Path.Combine(_packageRoot, "questions"));
        File.WriteAllText(
            Path.Combine(_packageRoot, "questions", "合成样题_1.tex"),
            "\\documentclass{ctexart}\n\\begin{document}合成占位\\end{document}\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    [Fact]
    public async Task CompileAsync_CleanRun_SucceedsAndUsesPackageRootAsWorkingDirectory()
    {
        var runner = new FakeProcessRunner
        {
            Result = new ExternalProcessResult(0, "ok", string.Empty),
            OnRun = () =>
            {
                WriteLog("""
                    (./questions/合成样题_1.tex
                    Output written on 合成样题_1.pdf (3 pages).
                    """);
                WritePdf();
            }
        };

        var result = await CreateCompiler(runner).CompileAsync(Request());

        Assert.True(result.Succeeded);
        Assert.Equal(3, result.Report.PageCount);
        Assert.NotNull(result.OutputPdfPath);
        Assert.True(File.Exists(result.OutputPdfPath));

        // 相对路径要按包内结构解析，工作目录必须是包根目录。
        Assert.Equal(Path.GetFullPath(_packageRoot), runner.WorkingDirectory);
        Assert.Contains("-no-shell-escape", runner.Arguments!);
        Assert.Contains(runner.Arguments!, argument => argument.StartsWith("-output-directory=", StringComparison.Ordinal));
        Assert.Equal("./" + SourceRelativePath, runner.Arguments![^1]);
    }

    [Fact]
    public async Task CompileAsync_ExitCodeZeroWithMissingPicture_DoesNotSucceed()
    {
        // 最关键的一条：缺图片时退出码为 0，判定必须来自日志里的阻断性问题。
        var runner = new FakeProcessRunner
        {
            Result = new ExternalProcessResult(0, "ok", string.Empty),
            OnRun = () =>
            {
                WriteLog("""
                    (./questions/合成样题_1.tex
                    LaTeX Warning: File `q-figure-01.png' not found on input line 12.
                    ! Unable to load picture or PDF file 'q-figure-01.png'.
                    l.12 \questionimage[0.3\textwidth]{q-figure-01.png}
                    ! Package graphics Error: Division by 0.
                    Output written on 合成样题_1.pdf (2 pages).
                    """);
                WritePdf();
            }
        };

        var result = await CreateCompiler(runner).CompileAsync(Request());

        Assert.False(result.Succeeded);
        var picture = Assert.Single(result.Issues, issue => issue.Code == TeXIssueCode.MissingPicture);
        Assert.Equal("q-figure-01.png", picture.File);
        Assert.Equal(12, picture.Line);

        // 连带的图形错误不算独立缺陷。
        Assert.DoesNotContain(result.Issues, issue => issue.Code == TeXIssueCode.SourceError);
    }

    [Fact]
    public async Task CompileAsync_Timeout_IsReportedAndSpawnsNothingFurther()
    {
        var runner = new FakeProcessRunner
        {
            Result = new ExternalProcessResult(0, string.Empty, string.Empty),
            Failure = new TimeoutException("XeLaTeX 执行超过 00:02:00。")
        };

        var result = await CreateCompiler(runner, TimeSpan.FromMinutes(2)).CompileAsync(Request());

        Assert.False(result.Succeeded);
        Assert.True(result.TimedOut);
        Assert.Contains(result.Issues, issue => issue.Code == TeXIssueCode.Timeout);
    }

    [Fact]
    public async Task CompileAsync_MissingToolchain_IsReportedWithoutThrowing()
    {
        var runner = new FakeProcessRunner
        {
            Result = new ExternalProcessResult(0, string.Empty, string.Empty),
            Failure = new FileNotFoundException("外部工具不存在或不是绝对路径。")
        };

        var result = await CreateCompiler(runner).CompileAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(result.Issues, issue => issue.Code == TeXIssueCode.ToolchainMissing);
    }

    [Fact]
    public async Task CompileAsync_NoLogWritten_ReportsOutputMissing()
    {
        var runner = new FakeProcessRunner
        {
            Result = new ExternalProcessResult(0, string.Empty, string.Empty)
        };

        var result = await CreateCompiler(runner).CompileAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, issue => issue.Code == TeXIssueCode.OutputMissing);
    }

    [Fact]
    public async Task CompileAsync_UnknownSourceFile_IsReportedBeforeRunning()
    {
        var runner = new FakeProcessRunner
        {
            Result = new ExternalProcessResult(0, string.Empty, string.Empty)
        };

        var result = await CreateCompiler(runner).CompileAsync(
            Request() with { SourceRelativePath = "questions/不存在_9.tex" });

        Assert.False(result.Succeeded);
        Assert.Null(runner.Arguments);
        Assert.Contains(result.Issues, issue => issue.Code == TeXIssueCode.MissingFile);
    }

    [Fact]
    public async Task CompileAsync_SourceEscapingPackageRoot_IsRejected()
    {
        var outside = Path.Combine(_root, "outside_1.tex");
        File.WriteAllText(outside, "\\documentclass{ctexart}");

        var runner = new FakeProcessRunner
        {
            Result = new ExternalProcessResult(0, string.Empty, string.Empty)
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CreateCompiler(runner).CompileAsync(
            Request() with { SourceRelativePath = "../outside_1.tex" }));
    }

    [Fact]
    public async Task CompileAsync_OutputDirectoryInsidePackageRoot_IsRejected()
    {
        // 编译产物不得写回材料目录。
        var runner = new FakeProcessRunner
        {
            Result = new ExternalProcessResult(0, string.Empty, string.Empty)
        };

        await Assert.ThrowsAsync<ArgumentException>(() => CreateCompiler(runner).CompileAsync(
            Request() with { OutputDirectory = Path.Combine(_packageRoot, "build") }));
    }

    [Fact]
    public void Construct_InvalidDefaultTimeout_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new XeLaTeXCompiler(
            new FakeProcessRunner { Result = new ExternalProcessResult(0, string.Empty, string.Empty) },
            new XeLaTeXToolchain(@"C:\fake\xelatex.exe", XeLaTeXPlatform.MiKTeX),
            TimeSpan.Zero));
    }

    private XeLaTeXCompiler CreateCompiler(IExternalProcessRunner runner, TimeSpan? timeout = null) =>
        new(PrepareRunner(runner), new XeLaTeXToolchain(@"C:\fake\xelatex.exe", XeLaTeXPlatform.MiKTeX), timeout);

    private IExternalProcessRunner PrepareRunner(IExternalProcessRunner runner)
    {
        if (runner is FakeProcessRunner fake)
        {
            fake.OnOutputDirectory = path => _runDirectory = path;
        }
        return runner;
    }

    [Fact]
    public async Task CompileAsync_PreexistingArtifacts_CannotSatisfyNewRun()
    {
        Directory.CreateDirectory(_outputDirectory);
        var oldPdf = Path.Combine(_outputDirectory, JobName + ".pdf");
        File.WriteAllText(oldPdf, "previous valid pdf");
        File.WriteAllText(Path.Combine(_outputDirectory, JobName + ".log"),
            $"Output written on {JobName}.pdf (1 page).");
        var result = await CreateCompiler(new FakeProcessRunner()).CompileAsync(Request());
        Assert.False(result.Succeeded);
        Assert.Null(result.OutputPdfPath);
        Assert.Equal("previous valid pdf", File.ReadAllText(oldPdf));
    }

    [Theory]
    [InlineData(@"\input ../outside.tex")]
    [InlineData(@"\includegraphics{/outside.png}")]
    [InlineData(@"\ShellEscape{echo probe}")]
    [InlineData(@"\write 18{echo probe}")]
    [InlineData(@"\RequirePackage{../outside}")]
    [InlineData(@"\openout\stream=questions/source.tex")]
    public async Task CompileAsync_NestedStyleViolation_DoesNotStartProcess(string source)
    {
        Directory.CreateDirectory(Path.Combine(_packageRoot, "styles"));
        File.WriteAllText(Path.Combine(_packageRoot, "styles", "nested.sty"), source);
        var runner = new FakeProcessRunner();
        var result = await CreateCompiler(runner).CompileAsync(Request());
        Assert.False(result.Succeeded);
        Assert.Null(runner.Arguments);
        Assert.Contains(result.Issues, issue => issue.Message.Contains("nested.sty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompileAsync_LogOverLimit_CannotBeAcceptedFromPrefix()
    {
        var runner = new FakeProcessRunner
        {
            OnRun = () =>
            {
                WriteLog($"Output written on {JobName}.pdf (1 page).\n" + new string('x', XeLaTeXCompiler.MaxLogBytes));
                WritePdf();
            }
        };
        var result = await CreateCompiler(runner).CompileAsync(Request());
        Assert.False(result.Succeeded);
        Assert.Null(result.OutputPdfPath);
        Assert.Contains(result.Issues, issue => issue.Code == TeXIssueCode.ResourceLimit);
    }

    [Fact]
    public async Task CompileAsync_ResourceLimit_IsBlocking()
    {
        var result = await CreateCompiler(new FakeProcessRunner
        {
            Failure = new ProcessResourceLimitException("输出超限")
        }).CompileAsync(Request());
        Assert.False(result.Succeeded);
        Assert.Null(result.OutputPdfPath);
        Assert.Contains(result.Issues, issue => issue.Code == TeXIssueCode.ResourceLimit);
    }

    [Fact]
    public async Task CompileAsync_FailedRunNeverExposesPartialPdf()
    {
        var result = await CreateCompiler(new FakeProcessRunner
        {
            Result = new ExternalProcessResult(1, "", ""),
            OnRun = () =>
            {
                WriteLog($"! Undefined control sequence.\nl.7 \\badmacro\nOutput written on {JobName}.pdf (1 page).");
                WritePdf();
            }
        }).CompileAsync(Request());
        Assert.False(result.Succeeded);
        Assert.Null(result.OutputPdfPath);
        Assert.Contains(result.Issues, issue => issue.Code == TeXIssueCode.SourceError
            && issue.File == SourceRelativePath && issue.Line == 7);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompileAsync_LinkedPackageDependencyOrOutput_IsRejected(bool linkOutput)
    {
        var target = Path.Combine(_root, "link-target");
        Directory.CreateDirectory(target);
        var link = linkOutput ? _outputDirectory : Path.Combine(_packageRoot, "figures");
        // Windows 用普通账户也能创建的目录联接，避免没有 symlink 权限时漏测边界。
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target })
            {
                start.ArgumentList.Add(argument);
            }
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
        try
        {
            var runner = new FakeProcessRunner();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CreateCompiler(runner).CompileAsync(Request()));
            Assert.Null(runner.Arguments);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    private TeXCompileRequest Request() =>
        new(_packageRoot, SourceRelativePath, _outputDirectory);

    private void WriteLog(string content)
    {
        File.WriteAllText(Path.Combine(_runDirectory!, JobName + ".log"), content);
    }

    private void WritePdf()
    {
        File.WriteAllText(Path.Combine(_runDirectory!, JobName + ".pdf"), "%PDF-1.4 合成占位");
    }

    private sealed class FakeProcessRunner : IExternalProcessRunner
    {
        public ExternalProcessResult Result { get; init; } = new(0, string.Empty, string.Empty);

        public Exception? Failure { get; init; }

        public Action? OnRun { get; init; }
        public Action<string>? OnOutputDirectory { get; set; }

        public IReadOnlyList<string>? Arguments { get; private set; }

        public string? WorkingDirectory { get; private set; }

        public Task<ExternalProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string> environment,
            TimeSpan timeout,
            CancellationToken cancellationToken = default,
            string? workingDirectory = null,
            ExternalProcessLimits? limits = null)
        {
            Arguments = arguments;
            WorkingDirectory = workingDirectory;
            OnOutputDirectory?.Invoke(arguments.Single(argument => argument.StartsWith("-output-directory=", StringComparison.Ordinal))[18..]);

            if (Failure is not null)
            {
                throw Failure;
            }

            OnRun?.Invoke();
            return Task.FromResult(Result);
        }
    }
}
