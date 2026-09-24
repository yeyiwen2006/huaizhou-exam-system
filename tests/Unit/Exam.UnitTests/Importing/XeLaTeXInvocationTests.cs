using Exam.Contracts.Importing;
using Exam.Domain.Importing;

namespace Exam.UnitTests.Importing;

/// <summary>
/// XeLaTeX 参数构造与成功判定的用例。纯逻辑，不需要安装 TeX。
/// </summary>
public sealed class XeLaTeXInvocationTests
{
    [Fact]
    public void BuildArguments_MiKTeX_DisablesInstallerAndShellEscape()
    {
        // MiKTeX 默认会按需安装缺失宏包，必须显式关闭，否则「不得联网取依赖」会被悄悄破坏。
        var arguments = XeLaTeXInvocation.BuildArguments(
            XeLaTeXPlatform.MiKTeX,
            "questions/合成样题_1.tex",
            @"C:\work\output");

        Assert.Contains("-disable-installer", arguments);
        Assert.Contains("-disable-write18", arguments);
        Assert.Contains("-no-shell-escape", arguments);
        Assert.Contains(XeLaTeXInvocation.InteractionMode, arguments);
        Assert.Contains(@"-output-directory=C:\work\output", arguments);

        // 源文件放在最后，且保持相对路径——以包根目录为工作目录才有意义。
        Assert.Equal("./questions/合成样题_1.tex", arguments[^1]);
    }

    [Fact]
    public void BuildArguments_TeXLive_OmitsMiKTeXOnlySwitches()
    {
        // TeX Live 本身不会自动安装，也不接受这两个参数；带上会让编译直接失败。
        var arguments = XeLaTeXInvocation.BuildArguments(
            XeLaTeXPlatform.TeXLive,
            "questions/合成样题_1.tex",
            "/work/output");

        Assert.DoesNotContain("-disable-installer", arguments);
        Assert.DoesNotContain("-disable-write18", arguments);
        Assert.Contains("-no-shell-escape", arguments);
        Assert.Contains("/work/output", arguments[^2]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildArguments_BlankSource_IsRejected(string source)
    {
        Assert.Throws<ArgumentException>(() => XeLaTeXInvocation.BuildArguments(
            XeLaTeXPlatform.MiKTeX,
            source,
            @"C:\work\output"));
    }

    [Fact]
    public void BuildArguments_UnknownPlatform_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => XeLaTeXInvocation.BuildArguments(
            (XeLaTeXPlatform)99,
            "questions/合成样题_1.tex",
            @"C:\work\output"));
    }

    [Fact]
    public void Evaluate_CleanRun_Succeeds()
    {
        var report = XeLaTeXLogReader.Read("""
            (./questions/合成样题_1.tex
            Output written on 合成样题_1.pdf (5 pages).
            """);

        var outcome = XeLaTeXInvocation.Evaluate(0, timedOut: false, outputPdfProduced: true, report);

        Assert.True(outcome.Succeeded);
        Assert.Empty(outcome.Issues);
    }

    [Fact]
    public void Evaluate_ExitCodeZeroButMissingPicture_DoesNotSucceed()
    {
        // 实测事实：缺图片时 XeLaTeX 退出码为 0，只看退出码会把半份试卷判成通过。
        var report = XeLaTeXLogReader.Read("""
            (./questions/合成样题_1.tex
            LaTeX Warning: File `q-figure-01.png' not found on input line 12.
            ! Unable to load picture or PDF file 'q-figure-01.png'.
            l.12 \questionimage[0.3\textwidth]{q-figure-01.png}
            """);

        var outcome = XeLaTeXInvocation.Evaluate(0, timedOut: false, outputPdfProduced: true, report);

        Assert.False(outcome.Succeeded);
        Assert.Contains(outcome.Issues, issue => issue.Code == TeXIssueCode.MissingPicture);
    }

    [Fact]
    public void Evaluate_PdfWrittenDespiteFontError_DoesNotSucceed()
    {
        // 实测事实：缺字体时仍会写出 PDF，同样不能只看「有输出」。
        var report = XeLaTeXLogReader.Read("""
            (./questions/合成样题_1.tex
            ! Font TU/SyntheticMissingFont(0)/m/n/10.5="Synthetic Missing Font" at 10.5pt not loadable: Metric (TFM) file or installed font not found.
            Output written on 合成样题_1.pdf (2 pages).
            """);

        var outcome = XeLaTeXInvocation.Evaluate(0, timedOut: false, outputPdfProduced: true, report);

        Assert.False(outcome.Succeeded);
        Assert.Contains(outcome.Issues, issue => issue.Code == TeXIssueCode.MissingFont);
    }

    [Fact]
    public void Evaluate_Timeout_ReportsTimeoutAndDoesNotSucceed()
    {
        var outcome = XeLaTeXInvocation.Evaluate(
            -1,
            timedOut: true,
            outputPdfProduced: false,
            new TeXCompileReport(false, 0, null, [], 0));

        Assert.False(outcome.Succeeded);
        Assert.Contains(outcome.Issues, issue => issue.Code == TeXIssueCode.Timeout);
    }

    [Fact]
    public void Evaluate_NonZeroExitCode_ReportsProcessFailure()
    {
        var outcome = XeLaTeXInvocation.Evaluate(
            1,
            timedOut: false,
            outputPdfProduced: false,
            new TeXCompileReport(false, 0, null, [], 0));

        Assert.False(outcome.Succeeded);
        Assert.Contains(outcome.Issues, issue => issue.Code == TeXIssueCode.ProcessFailed);
        Assert.Contains(outcome.Issues, issue => issue.Code == TeXIssueCode.OutputMissing);
    }

    [Fact]
    public void Evaluate_LogClaimsOutputButPdfIsMissing_ReportsOutputMissing()
    {
        var report = XeLaTeXLogReader.Read("""
            (./questions/合成样题_1.tex
            Output written on 合成样题_1.pdf (3 pages).
            """);

        var outcome = XeLaTeXInvocation.Evaluate(0, timedOut: false, outputPdfProduced: false, report);

        Assert.False(outcome.Succeeded);
        Assert.Contains(outcome.Issues, issue => issue.Code == TeXIssueCode.OutputMissing);
    }

    [Fact]
    public void Evaluate_DuplicateIssues_AreReportedOnce()
    {
        var report = XeLaTeXLogReader.Read("""
            (./questions/合成样题_1.tex
            ! LaTeX Error: File `no-such-package.sty' not found.
            """);

        var outcome = XeLaTeXInvocation.Evaluate(1, timedOut: false, outputPdfProduced: false, report);

        Assert.Single(outcome.Issues, issue => issue.Code == TeXIssueCode.MissingPackage);
    }
}
