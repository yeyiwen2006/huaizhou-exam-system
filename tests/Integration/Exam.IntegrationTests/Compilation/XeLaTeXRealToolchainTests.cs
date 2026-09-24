using Exam.Contracts.Importing;
using Exam.Domain.Importing;
using Exam.Infrastructure.Compilation;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.IntegrationTests.Compilation;

/// <summary>
/// 需要真实 XeLaTeX 的编译用例。夹具是**合成**的（自造的最小包 + 自造的最小 PNG），
/// 不含任何真实试卷内容。
///
/// 用 Category=Latex 标记：CI 的运行机上没有 TeX 发行版，必须在流水线里排除；
/// 本机没有 XeLaTeX 时这些用例会明确失败，而不是跳过 —— 跳过会让「已通过」失去意义。
/// </summary>
[Trait("Category", "Latex")]
public sealed class XeLaTeXRealToolchainTests : IDisposable
{
    private const string SourceRelativePath = "questions/合成样题_1.tex";
    private const string ImageRelativePath = "figures/chemistry/q-figure-01.png";

    private readonly string _root;
    private readonly string _packageRoot;
    private readonly string _outputDirectory;

    public XeLaTeXRealToolchainTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "exam-latex-" + Guid.NewGuid().ToString("N"));
        _packageRoot = Path.Combine(_root, "package");
        _outputDirectory = Path.Combine(_root, "output");
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
    public async Task CompileAsync_SyntheticQuestionWithCustomCommandsAndImage_Succeeds()
    {
        BuildPackage(withImage: true);
        var before = SnapshotPackage();

        var result = await CreateCompiler().CompileAsync(Request());

        Assert.True(result.Succeeded, Describe(result));
        Assert.True(result.Report.PageCount >= 1);
        Assert.NotNull(result.OutputPdfPath);
        Assert.True(File.Exists(result.OutputPdfPath));

        // 材料目录保持零改动：编译产物不得写回包目录。
        Assert.Equal(before, SnapshotPackage());

        // 可选验收输出，只导出此测试生成的合成 PDF，便于独立渲染核对。
        var artifacts = Environment.GetEnvironmentVariable("EXAM_TEST_TEX_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(artifacts))
        {
            Directory.CreateDirectory(artifacts);
            File.Copy(result.OutputPdfPath!, Path.Combine(artifacts, "synthetic-question.pdf"), overwrite: true);
        }
    }

    [Fact]
    public async Task CompileAsync_AfterMovingPackageToAnotherDirectory_StillFindsImages()
    {
        BuildPackage(withImage: true);
        var moved = Path.Combine(_root, "moved-package");
        CopyDirectory(_packageRoot, moved);

        var result = await CreateCompiler().CompileAsync(
            Request() with { PackageRoot = moved });

        Assert.True(result.Succeeded, Describe(result));
    }

    [Fact]
    public async Task CompileAsync_MissingImage_IsLocatedWithLineNumber()
    {
        BuildPackage(withImage: false);

        var result = await CreateCompiler().CompileAsync(Request());

        Assert.False(result.Succeeded);
        var picture = Assert.Single(result.Issues, issue => issue.Code == TeXIssueCode.MissingPicture);
        Assert.Equal("q-figure-01.png", picture.File);
        Assert.NotNull(picture.Line);
    }

    [Fact]
    public async Task CompileAsync_MissingFont_IsLocatedInsteadOfSilentlySubstituted()
    {
        BuildPackage(withImage: true, preambleExtra: "\\usepackage{fontspec}\n\\setmainfont{No Such Synthetic Font}");

        var result = await CreateCompiler().CompileAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, issue => issue.Code == TeXIssueCode.MissingFont);
    }

    [Fact]
    public async Task CompileAsync_ShellEscapeInSource_IsDisabledAndReported()
    {
        // 命令会写一个标记文件：如果外部命令真的执行了，标记就会出现。
        const string marker = "shell-escape-marker.txt";
        var source = "\\immediate\\write18{echo probe > " + marker + "}";
        BuildPackage(withImage: true, preambleExtra: source);

        var result = await CreateCompiler().CompileAsync(Request());

        // 第一层：源文件静态检查就能发现，不必等编译。
        Assert.Contains(
            TeXSourceInspector.Inspect(File.ReadAllText(Path.Combine(_packageRoot, SourceRelativePath))).Violations,
            violation => violation.Code == TeXIssueCode.ShellEscapeAttempt);

        // 第二层：运行期确实禁掉了外部命令——标记文件不得出现。
        Assert.False(File.Exists(Path.Combine(_packageRoot, marker)));
        Assert.False(File.Exists(Path.Combine(_outputDirectory, marker)));

        // 命令被禁不代表可以放过：本次编译仍判为不通过，并给出可定位的问题。
        Assert.Contains(result.Issues, issue => issue.Code == TeXIssueCode.ShellEscapeAttempt);
        Assert.False(result.Succeeded);

        // 编译入口必须执行静态检查，已知违规在启动进程之前阻断。
        Assert.Equal(-1, result.ExitCode);
        Assert.Null(result.OutputPdfPath);
    }

    private XeLaTeXCompiler CreateCompiler()
    {
        // 由实际版本输出选发行版，不能把 PATH 上任意 xelatex 当成 MiKTeX。
        var toolchain = XeLaTeXToolchain.TryDetect(XeLaTeXPlatform.TeXLive);
        Assert.True(
            toolchain is not null,
            "本机没有可用的 XeLaTeX。Category=Latex 的用例需要 TeX 发行版（本项目要求 XeLaTeX）。");

        var runner = new ExternalProcessRunner();
        var version = runner.RunAsync(toolchain!.ExecutablePath, ["--version"],
            new Dictionary<string, string>(), TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Assert.Equal(0, version.ExitCode);
        Assert.True(version.StandardOutput.Contains("MiKTeX", StringComparison.OrdinalIgnoreCase)
                    || version.StandardOutput.Contains("TeX Live", StringComparison.OrdinalIgnoreCase));
        toolchain = toolchain with
        {
            Platform = version.StandardOutput.Contains("MiKTeX", StringComparison.OrdinalIgnoreCase)
                ? XeLaTeXPlatform.MiKTeX : XeLaTeXPlatform.TeXLive
        };
        return new XeLaTeXCompiler(runner, toolchain, TimeSpan.FromMinutes(3));
    }

    [Fact]
    public async Task CompileAsync_MissingPackage_IsLocated()
    {
        BuildPackage(true, "\\usepackage{no-such-synthetic-package-2026}");
        var result = await CreateCompiler().CompileAsync(Request());
        Assert.False(result.Succeeded);
        // 缺包时 TeX 在交互提示前只保证给出依赖名；不把随后停机的位置误当成声明行。
        Assert.Contains(result.Issues, issue => issue.Code == TeXIssueCode.MissingPackage
            && issue.File == "no-such-synthetic-package-2026.sty");
        Assert.Null(result.OutputPdfPath);
    }

    [Fact]
    public async Task CompileAsync_DefaultFontSubstitution_IsBlockingEvenWithPdf()
    {
        BuildPackage(true);
        File.WriteAllText(Path.Combine(_packageRoot, SourceRelativePath),
            "\\documentclass{article}\\begin{document}\\fontfamily{NoSuchSyntheticFont}\\selectfont Probe\\end{document}");
        var result = await CreateCompiler().CompileAsync(Request());
        Assert.False(result.Succeeded);
        Assert.True(result.Report.HasOutput);
        Assert.Contains(result.Issues, issue => issue.Code == TeXIssueCode.MissingFont);
        Assert.Null(result.OutputPdfPath);
    }

    [Fact]
    public async Task CompileAsync_RetryWithSameFileName_DoesNotOverwritePreviousPdf()
    {
        BuildPackage(true);
        var compiler = CreateCompiler();
        var first = await compiler.CompileAsync(Request());
        Assert.True(first.Succeeded, Describe(first));
        var previous = File.ReadAllBytes(first.OutputPdfPath!);
        var source = Path.Combine(_packageRoot, SourceRelativePath);
        File.AppendAllText(source, "\n% second compile");
        var second = await compiler.CompileAsync(Request());
        Assert.True(second.Succeeded, Describe(second));
        Assert.NotEqual(first.OutputPdfPath, second.OutputPdfPath);
        Assert.Equal(previous, File.ReadAllBytes(first.OutputPdfPath!));
    }

    [Fact]
    public async Task CompileAsync_TimeoutStopsRealEngine()
    {
        BuildPackage(true);
        File.WriteAllText(Path.Combine(_packageRoot, SourceRelativePath),
            "\\documentclass{article}\\begin{document}\\loop\\iftrue\\repeat\\end{document}");
        var result = await CreateCompiler().CompileAsync(Request() with { Timeout = TimeSpan.FromSeconds(2) });
        Assert.False(result.Succeeded);
        Assert.True(result.TimedOut);
        Assert.Null(result.OutputPdfPath);
    }

    [Fact]
    public async Task CompileAsync_IndirectShellEscape_IsDisabledByRealEngine()
    {
        // 间接构造命令用于验证引擎开关本身，而非只验证静态检查器。
        BuildPackage(true, "\\immediate\\csname write\\endcsname18{echo probe > indirect-marker.txt}");
        var result = await CreateCompiler().CompileAsync(Request());
        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, issue => issue.Code == TeXIssueCode.ShellEscapeAttempt);
        Assert.Empty(Directory.GetFiles(_root, "indirect-marker.txt", SearchOption.AllDirectories));
    }

    private TeXCompileRequest Request() =>
        new(_packageRoot, SourceRelativePath, _outputDirectory);

    /// <summary>构造一个最小导入包：自定义宏包住图片引用、\graphicspath 指向按科目分的子目录。</summary>
    private void BuildPackage(bool withImage, string? preambleExtra = null)
    {
        Directory.CreateDirectory(Path.Combine(_packageRoot, "questions"));
        Directory.CreateDirectory(Path.Combine(_packageRoot, "figures", "chemistry"));

        // 用占位符替换而不是插值：LaTeX 里满是花括号，插值会与之冲突。
        const string template = """
            \documentclass{ctexart}
            \usepackage{graphicx}
            \newcommand{\questionimage}[2][0.3\textwidth]{\includegraphics[width=#1]{#2}}
            \newcommand{\syntheticblank}[1][2em]{\underline{\hspace{#1}}}
            \graphicspath{{figures/chemistry/}}
            __PREAMBLE__
            \begin{document}
            合成样题：图中阴影部分表示（\quad）。

            \questionimage{q-figure-01.png}

            \begin{enumerate}
              \item[A.] 甲\quad
              \item[B.] 乙
            \end{enumerate}
            \end{document}
            """;

        var source = template.Replace("__PREAMBLE__", preambleExtra ?? string.Empty, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_packageRoot, SourceRelativePath), source);

        if (withImage)
        {
            File.WriteAllBytes(Path.Combine(_packageRoot, ImageRelativePath), SyntheticPng.Create());
        }
    }

    private List<string> SnapshotPackage() =>
        Directory
            .GetFiles(_packageRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_packageRoot, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    private static void CopyDirectory(string source, string target)
    {
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }

    private static string Describe(TeXCompileResult result) =>
        result.Succeeded
            ? "成功"
            : $"退出码={result.ExitCode} 超时={result.TimedOut} 写出PDF={result.Report.HasOutput}；" +
              string.Join(" | ", result.Issues.Select(issue => $"{issue.Code}:{issue.File}@{issue.Line} {issue.Message}"));
}
