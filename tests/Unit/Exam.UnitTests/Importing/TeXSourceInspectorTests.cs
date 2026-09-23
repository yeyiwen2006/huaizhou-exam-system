using Exam.Contracts.Importing;
using Exam.Domain.Importing;

namespace Exam.UnitTests.Importing;

/// <summary>
/// 逐题 TeX 源文件静态检查的用例。夹具是**合成**的，但复刻真实材料的形态：
/// 多宏包一次声明、带可选参数的字体声明、newCJKfontfamily、自定义图片宏、注释里的示例命令。
/// </summary>
public sealed class TeXSourceInspectorTests
{
    private const string SyntheticSource = """
        \documentclass{ctexart}
        \usepackage{amsmath,unicode-math}
        \usepackage[a4paper, margin=1in]{geometry}
        \usepackage{graphicx}
        \setmainfont{Times New Roman}
        \setmathfont{XITS Math}
        \newCJKfontfamily\footerfont{SimSun}
        % 选项换行规范：注释里的示例命令不应被当成真实引用
        % \write18{echo should-not-be-detected}
        % \includegraphics{commented-out.png}
        \newcommand{\optionbreak}{\\[0.20em]}
        \newcommand{\questionimage}[2][0.3\textwidth]{\includegraphics[width=#1]{#2}}
        \graphicspath{{figures/chemistry/}}
        \begin{document}
        \questionimage[0.22\textwidth]{q-figure-01.png}
        \includegraphics{q-figure-02.png}
        \includegraphics{figures/chemistry/q-figure-03.png}
        \end{document}
        """;

    [Fact]
    public void Inspect_ReadsDocumentClassPackagesFontsAndGraphicsRoots()
    {
        var declaration = TeXSourceInspector.Inspect(SyntheticSource);

        Assert.Equal("ctexart", declaration.DocumentClass);
        Assert.Contains("amsmath", declaration.Packages);
        Assert.Contains("unicode-math", declaration.Packages);
        Assert.Contains("geometry", declaration.Packages);
        Assert.DoesNotContain("a4paper", declaration.Packages);

        Assert.Contains("Times New Roman", declaration.Fonts);
        Assert.Contains("XITS Math", declaration.Fonts);
        Assert.Contains("SimSun", declaration.Fonts);

        Assert.Equal(["figures/chemistry/"], declaration.GraphicsRoots);
    }

    [Fact]
    public void Inspect_ExpandsCustomImageMacrosAndIgnoresMacroDefinitionPlaceholders()
    {
        var declaration = TeXSourceInspector.Inspect(SyntheticSource);

        // 自定义宏的调用要收进来，宏体里的 #2 占位符不能当成图片名。
        Assert.Contains("q-figure-01.png", declaration.ImageReferences);
        Assert.Contains("q-figure-02.png", declaration.ImageReferences);
        Assert.Contains("figures/chemistry/q-figure-03.png", declaration.ImageReferences);
        Assert.DoesNotContain("#2", declaration.ImageReferences);
        Assert.DoesNotContain("commented-out.png", declaration.ImageReferences);
    }

    [Fact]
    public void Inspect_IgnoredCommentsProduceNoViolations()
    {
        var declaration = TeXSourceInspector.Inspect(SyntheticSource);

        Assert.Empty(declaration.Violations);
    }

    [Fact]
    public void Inspect_ShellEscapeCommand_IsAViolationWithLineNumber()
    {
        const string source = """
            \documentclass{ctexart}
            \begin{document}
            \immediate\write18{echo probe}
            \end{document}
            """;

        var declaration = TeXSourceInspector.Inspect(source);

        var violation = Assert.Single(declaration.Violations);
        Assert.Equal(TeXIssueCode.ShellEscapeAttempt, violation.Code);
        Assert.Equal(3, violation.Line);
    }

    [Fact]
    public void Inspect_PackageRequiringExternalCommands_IsAViolation()
    {
        const string source = """
            \documentclass{ctexart}
            \usepackage{minted}
            \begin{document}
            占位
            \end{document}
            """;

        var declaration = TeXSourceInspector.Inspect(source);

        var violation = Assert.Single(declaration.Violations);
        Assert.Equal(TeXIssueCode.ShellEscapeAttempt, violation.Code);
        Assert.Equal("minted", violation.File);
    }

    [Fact]
    public void Inspect_EscapedPercentInText_IsNotTreatedAsComment()
    {
        const string source = """
            \documentclass{ctexart}
            \begin{document}
            得分率 100\% 且 \write18{echo probe}
            \end{document}
            """;

        var declaration = TeXSourceInspector.Inspect(source);

        var violation = Assert.Single(declaration.Violations);
        Assert.Equal(TeXIssueCode.ShellEscapeAttempt, violation.Code);
    }

    [Fact]
    public void Inspect_GraphicsPathEscapingPackageRoot_IsAViolation()
    {
        const string source = """
            \documentclass{ctexart}
            \graphicspath{{../outside/}}
            \begin{document}
            \includegraphics{q-figure-01.png}
            \end{document}
            """;

        var declaration = TeXSourceInspector.Inspect(source);

        var violation = Assert.Single(declaration.Violations);
        Assert.Contains("..", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_AbsoluteInputPath_IsAViolation()
    {
        const string source = """
            \documentclass{ctexart}
            \begin{document}
            \input{C:/outside/chapter.tex}
            \end{document}
            """;

        var declaration = TeXSourceInspector.Inspect(source);

        var violation = Assert.Single(declaration.Violations);
        Assert.Equal("C:/outside/chapter.tex", violation.File);
        Assert.Equal(3, violation.Line);
    }

    [Theory]
    [InlineData("q-figure-01.png", "figures/chemistry/", "figures/chemistry/q-figure-01.png")]
    [InlineData("q-figure-01.png", "figures/physics/", "figures/physics/q-figure-01.png")]
    [InlineData("figures/chemistry/q-figure-01.png", "figures/physics/", "figures/chemistry/q-figure-01.png")]
    public void ResolveImageCandidates_UsesGraphicsRootsUnlessPathIsExplicit(
        string reference,
        string graphicsRoot,
        string expectedFirst)
    {
        var candidates = TeXSourceInspector.ResolveImageCandidates(reference, [graphicsRoot]);

        Assert.NotEmpty(candidates);
        Assert.Equal(expectedFirst, candidates[0]);
    }

    [Fact]
    public void ResolveImageCandidates_AlwaysFallsBackToPackageRoot()
    {
        var candidates = TeXSourceInspector.ResolveImageCandidates(
            "q-figure-01.png",
            ["figures/chemistry/", "figures/physics/"]);

        Assert.Equal(
            ["figures/chemistry/q-figure-01.png", "figures/physics/q-figure-01.png", "q-figure-01.png"],
            candidates);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Inspect_EmptySource_ReturnsEmptyDeclaration(string? source)
    {
        var declaration = TeXSourceInspector.Inspect(source);

        Assert.Equal(string.Empty, declaration.DocumentClass);
        Assert.Empty(declaration.Packages);
        Assert.Empty(declaration.Fonts);
        Assert.Empty(declaration.ImageReferences);
        Assert.Empty(declaration.Violations);
    }

    [Fact]
    public void Inspect_ImageReferenceWithInvalidWindowsSegment_IsAViolation()
    {
        // 具体的图片引用会真的落到文件系统上，逐段按 Windows 规则校验（':' 在 Windows 上非法）。
        const string source = """
            \documentclass{ctexart}
            \begin{document}
            \includegraphics{figures/a:b.png}
            \end{document}
            """;

        var declaration = TeXSourceInspector.Inspect(source);

        var violation = Assert.Single(declaration.Violations);
        Assert.Contains("Windows", violation.Message, StringComparison.Ordinal);
        Assert.Equal("figures/a:b.png", violation.File);
    }

    [Fact]
    public void Inspect_GraphicsPathWildcardRoot_IsNotAViolation()
    {
        // kpathsea 允许 `**` 递归通配，图片搜索路径不能按 Windows 文件名逐段校验。
        const string source = """
            \documentclass{ctexart}
            \graphicspath{{figures/**/}}
            \begin{document}
            \includegraphics{q-figure-01.png}
            \end{document}
            """;

        var declaration = TeXSourceInspector.Inspect(source);

        Assert.Equal(["figures/**/"], declaration.GraphicsRoots);
        Assert.Empty(declaration.Violations);
    }

    [Fact]
    public void Inspect_ImageReferencesAreNormalizedBeforeDeduplication()
    {
        const string source = """
            \documentclass{ctexart}
            \begin{document}
            \includegraphics{./figures/q-figure-01.png}
            \includegraphics{figures/q-figure-01.png}
            \end{document}
            """;

        var declaration = TeXSourceInspector.Inspect(source);

        Assert.Equal(["figures/q-figure-01.png"], declaration.ImageReferences);
    }
}
