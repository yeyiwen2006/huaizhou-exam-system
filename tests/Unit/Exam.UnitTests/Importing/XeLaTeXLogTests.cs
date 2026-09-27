using Exam.Contracts.Importing;
using Exam.Domain.Importing;

namespace Exam.UnitTests.Importing;

/// <summary>
/// XeLaTeX 日志解析的用例。这里的日志夹具全部是**合成**的，但逐字复刻本机实测到的真实格式
/// （MiKTeX 26.5 / XeTeX 4.18）：按列宽硬折行、续行带 (fontspec) 前缀、缺图片的成对错误、
/// 缺依赖后的连带失败。真实试卷的日志不入库。
/// </summary>
public sealed class XeLaTeXLogTests
{
    [Theory]
    [InlineData("LaTeX Font Warning: Font shape `TU/NoSuchFont/m/n' undefined\n(Font) using `TU/lmr/m/n' instead on input line 4.")]
    [InlineData("LaTeX Font Warning: Some font shapes were not available, defaults substituted.")]
    [InlineData("Missing character: There is no 中 (U+4E2D) in font cmr10!")]
    [InlineData("! Illegal unit of measure (pt inserted).\nl.4 \\hspace{bad}")]
    public void Read_PdfWithSubstitutionMissingGlyphOrStandaloneError_IsNotClean(string diagnostic)
    {
        var report = XeLaTeXLogReader.Read(diagnostic + "\nOutput written on synthetic.pdf (1 page).");
        Assert.True(report.HasOutput);
        Assert.False(report.IsClean);
    }

    [Fact]
    public void Read_SuccessfulRun_ReportsPagesAndNoBlockingIssues()
    {
        // 折行发生在 "(5 " 与 "pages)." 之间，拼接时不能补空格。
        const string log = """
            This is XeTeX, Version 3.141592653-2.6-0.999995 (MiKTeX 26.5)
            entering extended mode
            (./questions/合成样题_1.tex
            LaTeX2e <2025-11-01>
            (/install/tex/latex/ctex/ctexart.cls
            Document Class: ctexart 2025/11/01 v2.5.10 Chinese typesetting
            )
            No file 合成样题_1.aux.
            [1] (./合成样题_1.aux)
            (see the transcript file for additional information)
            Output written on 合成样题_1.pdf (5
            pages).
            """;

        var report = XeLaTeXLogReader.Read(log);

        Assert.True(report.HasOutput);
        Assert.Equal(5, report.PageCount);
        Assert.Equal("合成样题_1.pdf", report.OutputFileName);
        Assert.Empty(report.BlockingIssues);
        Assert.True(report.IsClean);
    }

    [Fact]
    public void Read_OutputLineWrappedRightAfterFileNameDot_StillReportsOutput()
    {
        // 79 列硬折行的折点恰好落在文件名的扩展名点上，续行以 "pdf" 开头。
        // Fold 的句末规则会把上一行当成句子结束，只按折叠结果匹配就找不到产物，
        // 于是成功编译被判为「没有写出 PDF」，合法导入被拒绝。
        // 真实材料的文件名长度各异，折点位置不可控，必然命中。
        const string log = """
            This is XeTeX, Version 3.141592653-2.6-0.999995 (MiKTeX 26.5)
            entering extended mode
            (./questions/合成选项题_1.tex
            [1] (./合成选项题_1.aux)
            Output written on C:\synthetic\output\compile-0123456789abcdef\合成选项题_1.
            pdf (3 pages).
            """;

        var report = XeLaTeXLogReader.Read(log);

        Assert.True(report.HasOutput);
        Assert.Equal(3, report.PageCount);
        Assert.Equal("合成选项题_1.pdf", report.OutputFileName);
    }


    [Fact]
    public void Read_MissingPicture_ReportsOnceWithLineAndTreatsGraphicsFailureAsCascade()
    {
        const string log = """
            (./questions/合成样题_2.tex
            LaTeX Warning: File `q-figure-01.png' not found on input line 12.

            ! Unable to load picture or PDF file 'q-figure-01.png'.
            <to be read again>
                               }
            l.12 \questionimage[0.22\textwidth]{q-figure-01.png}

            The requested image couldn't be read because
            it was not a recognized image format.


            ! Package graphics Error: Division by 0.
            """;

        var report = XeLaTeXLogReader.Read(log);

        // 缺图片是同一个缺陷：警告与随后的图形错误只能报一条。
        var picture = Assert.Single(report.Issues, issue => issue.Code == TeXIssueCode.MissingPicture);
        Assert.Equal("q-figure-01.png", picture.File);
        Assert.Equal(12, picture.Line);

        // 图形 Division by 0 是连带失败，不作为独立缺陷，也不阻断之外的东西。
        var cascade = Assert.Single(report.Issues, issue => issue.Code == TeXIssueCode.Cascade);
        Assert.False(cascade.IsBlocking);

        Assert.False(report.HasOutput);
        Assert.False(report.IsClean);
        Assert.Single(report.BlockingIssues);
    }

    [Fact]
    public void Read_MissingPictureWarningAlone_IsStillReported()
    {
        // 只出现「文件未找到」提示、没有后续图形错误时也要报出来（依赖可能被宏延后加载）。
        const string log = """
            (./questions/合成样题_2.tex
            LaTeX Warning: File `q-figure-02.png' not found on input line 30.
            """;

        var report = XeLaTeXLogReader.Read(log);

        var picture = Assert.Single(report.Issues);
        Assert.Equal(TeXIssueCode.MissingPicture, picture.Code);
        Assert.Equal("q-figure-02.png", picture.File);
        Assert.Equal(30, picture.Line);
    }

    [Fact]
    public void Read_MissingPackage_ReportsPackage_AndEmergencyStopIsCascade()
    {
        const string log = """
            (./questions/合成样题_3.tex
            ! LaTeX Error: File `no-such-synthetic-package.sty' not found.

            Type X to quit or <RETURN> to proceed,
            or enter new name. (Default extension: sty)

            Enter file name:
            ! Emergency stop.
            <read *>
            """;

        var report = XeLaTeXLogReader.Read(log);

        var package = Assert.Single(report.Issues, issue => issue.Code == TeXIssueCode.MissingPackage);
        Assert.Equal("no-such-synthetic-package.sty", package.File);
        Assert.True(package.IsBlocking);

        var cascade = Assert.Single(report.Issues, issue => issue.Code == TeXIssueCode.Cascade);
        Assert.False(cascade.IsBlocking);
        Assert.Single(report.BlockingIssues);
    }

    [Fact]
    public void Read_MissingFontspecFont_ReportsFontNameFromContinuationLines()
    {
        const string log = """
            (./questions/合成样题_4.tex
            ! Package fontspec Error:
            (fontspec)                The font "Synthetic Missing Font" cannot be found; this
            (fontspec)                may be but usually is not a fontspec bug. Either
            (fontspec)                there is a typo in the font name/file, the font is
            (fontspec)                not installed (correctly), or there is a bug in
            (fontspec)                the underlying font loading engine
            (fontspec)                (XeTeX/luaotfload).

            For immediate help type H <return>.
             ...

            l.3 \setmainfont{Synthetic Missing Font}
            """;

        var report = XeLaTeXLogReader.Read(log);

        var font = Assert.Single(report.Issues, issue => issue.Code == TeXIssueCode.MissingFont);
        Assert.Equal("Synthetic Missing Font", font.File);
        Assert.True(font.IsBlocking);
    }

    [Fact]
    public void Read_FontNotLoadable_IsReportedEvenWhenNameIsWrappedMidWord()
    {
        // 实测的折行就发生在这里：`not lo` + `adable:` 必须拼成 `not loadable:`。
        const string log = """
            (./questions/合成样题_5.tex
            ! Font TU/SyntheticMissingFont(0)/m/n/10.5="Synthetic Missing Font" at 10.5pt not lo
            adable: Metric (TFM) file or installed font not found.
            """;

        var report = XeLaTeXLogReader.Read(log);

        var font = Assert.Single(report.Issues, issue => issue.Code == TeXIssueCode.MissingFont);
        Assert.Equal("Synthetic Missing Font", font.File);
        Assert.Single(report.BlockingIssues);
    }

    [Fact]
    public void Read_FontNotLoadableWithoutName_IsStillReported()
    {
        const string log = """
            (./questions/合成样题_6.tex
            ! Font \zf@basefont at 10.0pt not loadable: Metric (TFM) file or installed font not found.
            """;

        var report = XeLaTeXLogReader.Read(log);

        var font = Assert.Single(report.Issues, issue => issue.Code == TeXIssueCode.MissingFont);
        Assert.Contains("未能从日志确定", font.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_ShellEscapeAttempt_IsReportedAndBlocking()
    {
        const string log = """
            (./questions/合成样题_7.tex
            runsystem(echo probe)...disabled (restricted).
            No file 合成样题_7.aux.
            )
            Output written on 合成样题_7.pdf (1 page).
            """;

        var report = XeLaTeXLogReader.Read(log);

        var attempt = Assert.Single(report.Issues, issue => issue.Code == TeXIssueCode.ShellEscapeAttempt);
        Assert.Contains("echo probe", attempt.Message, StringComparison.Ordinal);
        Assert.True(attempt.IsBlocking);

        // 写出了 PDF，但因为试图执行外部命令，仍不得视为干净。
        Assert.True(report.HasOutput);
        Assert.False(report.IsClean);
    }

    [Fact]
    public void Read_OtherLatexError_IsBlockingSourceError()
    {
        const string log = """
            (./questions/合成样题_8.tex
            ! Undefined control sequence.
            l.9 \nosuchmacro
            """;

        var report = XeLaTeXLogReader.Read(log);

        var error = Assert.Single(report.Issues);
        Assert.Equal(TeXIssueCode.SourceError, error.Code);
        Assert.True(error.IsBlocking);
        Assert.Equal(9, error.Line);
    }

    [Fact]
    public void Read_OverfullBoxes_AreCountedButNotBlocking()
    {
        const string log = """
            (./questions/合成样题_9.tex
            Overfull \hbox (12.0pt too wide) in paragraph at lines 10--12
            Underfull \vbox (badness 10000) has occurred while \output is active [3]
            Output written on 合成样题_9.pdf (3 pages).
            """;

        var report = XeLaTeXLogReader.Read(log);

        Assert.Equal(2, report.OverfullBoxCount);
        Assert.Empty(report.BlockingIssues);
        Assert.True(report.IsClean);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Read_EmptyLog_HasNoOutputAndIsNotClean(string? log)
    {
        var report = XeLaTeXLogReader.Read(log);

        Assert.False(report.HasOutput);
        Assert.False(report.IsClean);
        Assert.Equal(0, report.PageCount);
    }

    [Fact]
    public void Read_PdfWrittenDespiteFontError_IsNotClean()
    {
        // 实测事实：缺字体时 XeLaTeX 仍会写出 PDF，因此「有输出」不能当作成功。
        const string log = """
            (./questions/合成样题_10.tex
            ! Font TU/SyntheticMissingFont(0)/m/n/10.5="Synthetic Missing Font" at 10.5pt not lo
            adable: Metric (TFM) file or installed font not found.
            Output written on 合成样题_10.pdf (2 pages).
            """;

        var report = XeLaTeXLogReader.Read(log);

        Assert.True(report.HasOutput);
        Assert.Equal(2, report.PageCount);
        Assert.False(report.IsClean);
    }
}
