using Exam.Contracts.Importing;
using Exam.Domain.Importing;
using Exam.Infrastructure.Compilation;

namespace Exam.UnitTests.Importing;

/// <summary>
/// 包内文件与依赖存在性检查的用例。用临时目录构造包结构；
/// 需要符号链接的用例在无权限时跳过（与仓库既有 ServiceManagement 用例相同的处理方式）。
/// </summary>
public sealed class PackageContentCheckerTests : IDisposable
{
    private readonly string _root;
    private readonly string _packageRoot;
    private readonly PackageContentChecker _checker = new();

    public PackageContentCheckerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "exam-pkg-" + Guid.NewGuid().ToString("N"));
        _packageRoot = Path.Combine(_root, "package");
        Directory.CreateDirectory(_packageRoot);
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
    public void CheckImages_ResolvesThroughGraphicsPath()
    {
        WriteFile("figures/chemistry/q-figure-01.png", "synthetic");

        var issues = _checker.CheckImages(_packageRoot, Declaration(["figures/chemistry/"], ["q-figure-01.png"]));

        Assert.Empty(issues);
    }

    [Fact]
    public void CheckImages_ExplicitRelativePath_IsResolvedFromPackageRoot()
    {
        WriteFile("figures/chemistry/q-figure-02.png", "synthetic");

        var issues = _checker.CheckImages(
            _packageRoot,
            Declaration(["figures/physics/"], ["figures/chemistry/q-figure-02.png"]));

        Assert.Empty(issues);
    }

    [Fact]
    public void CheckImages_MissingImage_IsReportedWithCandidatePaths()
    {
        var issues = _checker.CheckImages(_packageRoot, Declaration(["figures/chemistry/"], ["q-figure-03.png"]));

        var issue = Assert.Single(issues);
        Assert.Equal(TeXIssueCode.MissingPicture, issue.Code);
        Assert.Equal("q-figure-03.png", issue.File);
        Assert.Contains("figures/chemistry/q-figure-03.png", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckImages_ReferenceEscapingPackageRoot_IsReportedRatherThanAccepted()
    {
        WriteFile("outside.png", "synthetic");

        var issues = _checker.CheckImages(_packageRoot, Declaration([], ["../outside.png"]));

        var issue = Assert.Single(issues);
        Assert.Equal(TeXIssueCode.MissingPicture, issue.Code);
    }

    [Fact]
    public void CheckSourceExists_ReportsMissingSource()
    {
        Assert.NotNull(_checker.CheckSourceExists(_packageRoot, "questions/不存在_1.tex"));

        WriteFile("questions/合成样题_1.tex", "\\documentclass{ctexart}");

        Assert.Null(_checker.CheckSourceExists(_packageRoot, "questions/合成样题_1.tex"));
        Assert.NotNull(_checker.CheckSourceExists(_packageRoot, "questions/不存在_1.tex"));
    }

    [Fact]
    public void CheckResourceRoots_ReportsMissingDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_packageRoot, "styles"));

        var issues = _checker.CheckResourceRoots(_packageRoot, ["styles", "figures"]);

        var issue = Assert.Single(issues);
        Assert.Equal("figures", issue.File);
    }

    [Fact]
    public void CheckResourceRoots_EscapingRoot_IsReported()
    {
        var issues = _checker.CheckResourceRoots(_packageRoot, ["../outside"]);

        var issue = Assert.Single(issues);
        Assert.Contains("越出导入包根目录", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckResourceRoots_DirectoryLink_IsRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(Path.Combine(_packageRoot, "styles"));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_packageRoot, "linked"), outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        var issues = _checker.CheckResourceRoots(_packageRoot, ["styles", "linked"]);

        var issue = Assert.Single(issues);
        Assert.Equal("linked", issue.File);
        Assert.Contains("符号链接", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckImages_ImageOnlyReachableThroughDirectoryLink_IsNotAccepted()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // 图片藏在指向包外的目录联接后面：能"找到文件"也不能接受，
        // 否则符号链接会把包外内容带进导入流程。
        var outside = Path.Combine(_root, "outside-figures");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "q-figure-04.png"), "synthetic");

        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_packageRoot, "figures"), outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return;
        }

        var issues = _checker.CheckImages(_packageRoot, Declaration(["figures/"], ["q-figure-04.png"]));

        var issue = Assert.Single(issues);
        Assert.Equal(TeXIssueCode.MissingPicture, issue.Code);
    }

    [Fact]
    public void CheckImages_DeclarationFromInspector_IsConsistent()
    {
        // 与源文件检查串起来：图片宏调用 + \graphicspath 由检查器读出，再交给存在性检查。
        WriteFile("figures/chemistry/q-figure-05.png", "synthetic");
        const string source = """
            \documentclass{ctexart}
            \newcommand{\questionimage}[2][0.3\textwidth]{\includegraphics[width=#1]{#2}}
            \graphicspath{{figures/chemistry/}}
            \begin{document}
            \questionimage{q-figure-05.png}
            \end{document}
            """;

        var declaration = TeXSourceInspector.Inspect(source);
        var issues = _checker.CheckImages(_packageRoot, declaration);

        Assert.Empty(declaration.Violations);
        Assert.Empty(issues);
    }

    private static TeXSourceDeclaration Declaration(
        IReadOnlyList<string> graphicsRoots,
        IReadOnlyList<string> imageReferences) =>
        new("ctexart", [], [], graphicsRoots, imageReferences, []);

    private void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_packageRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
