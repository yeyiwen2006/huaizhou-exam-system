using Exam.Contracts.Importing;
using Exam.Domain.Importing;
using Exam.Infrastructure.Compilation;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.IntegrationTests.Compilation;

/// <summary>
/// 选项定位抽取的真实 XeLaTeX 用例：真编译合成题，再用 PdfPig 抽取并核对。
/// 夹具全部是**合成**的（自造最小包），不含任何真实试卷内容；产物只写临时目录。
///
/// 用 Category=Latex 标记：CI 运行机没有 TeX 发行版，必须在流水线里排除；
/// 本机没有 XeLaTeX 时这些用例明确失败而不是跳过。
/// </summary>
[Trait("Category", "Latex")]
public sealed class OptionRegionExtractionTests : IDisposable
{
    private const string SourceRelativePath = "questions/合成选项题_1.tex";
    private const string QuestionId = "q-001";

    private static readonly string[] OptionIds = ["A", "B", "C", "D"];

    private readonly string _root;
    private readonly string _packageRoot;
    private readonly string _outputDirectory;
    private string? _lastPdfPath;

    public OptionRegionExtractionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "exam-option-region-" + Guid.NewGuid().ToString("N"));
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
    public async Task Extract_PlainQuestion_ProducesOrderedRegionsWithinUnitSquare()
    {
        var result = await ExtractAsync(DefaultBody(), pageAttribute: null);

        Assert.True(result.Succeeded, Describe(result));
        var question = Assert.Single(result.Artifact!.Questions);
        Assert.Equal(QuestionId, question.QuestionId);
        Assert.Equal(OptionIds, question.Options.Select(option => option.OptionId));

        foreach (var option in question.Options)
        {
            Assert.Equal(0, option.PageIndex);
            Assert.InRange(option.X, 0, 1);
            Assert.InRange(option.Y, 0, 1);
            Assert.True(option.Width > 0 && option.Height > 0);
            Assert.True(option.X + option.Width <= 1);
            Assert.True(option.Y + option.Height <= 1);
        }

        // 选项标签处于同一列（enumerate 的标签右对齐，字母宽度不同，左边界只会相差极小），
        // 且纵向严格递增：归一化方向与基准正确的基本特征。
        var lefts = question.Options.Select(option => option.X).ToList();
        Assert.True(lefts.Max() - lefts.Min() < 0.01, "同一题的选项标签应处于同一列");
        for (var index = 1; index < question.Options.Count; index++)
            Assert.True(question.Options[index].Y > question.Options[index - 1].Y, "选项应按纵向递增排列");

        ExportForB03(result.Artifact!);
    }

    /// <summary>
    /// 可选验收输出：导出这道合成题的 PDF 与坐标产物，供 B03 按同一格式接入并核对点击位置。
    /// 与真实材料无关，全部为合成内容。
    /// </summary>
    private void ExportForB03(OptionRegionArtifact artifact)
    {
        var artifacts = Environment.GetEnvironmentVariable("EXAM_TEST_TEX_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(artifacts) || _lastPdfPath is null) return;

        Directory.CreateDirectory(artifacts);
        File.Copy(_lastPdfPath, Path.Combine(artifacts, "synthetic-question.pdf"), overwrite: true);
        File.WriteAllText(
            Path.Combine(artifacts, OptionRegionArtifact.FileName),
            OptionRegionArtifactJson.Serialize(artifact),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    [Fact]
    public async Task Extract_ChineseParenthesizedLabels_UseLetterOutlines()
    {
        var body = DefaultBody();
        foreach (var id in OptionIds)
            body = body.Replace($"{{{id}.}}", $"{{（{id}）}}", StringComparison.Ordinal);
        var result = await ExtractAsync(body, pageAttribute: null);
        Assert.True(result.Succeeded, Describe(result));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(_lastPdfPath!);
        var page = pdf.GetPage(1);
        foreach (var option in result.Artifact!.Questions[0].Options)
        {
            var letter = Assert.Single(page.Letters, letter => letter.Value == option.OptionId);
            var glyph = letter.BoundingBox;
            Assert.Equal(glyph.Left / page.Width, option.X, 6);
            Assert.Equal(1 - glyph.Top / page.Height, option.Y, 6);
            Assert.Equal(glyph.Width / page.Width, option.Width, 6);
            Assert.Equal(glyph.Height / page.Height, option.Height, 6);
        }
    }

    [Fact]
    public async Task Extract_QuestionWithExplicitPageBreak_ReportsZeroBasedPageIndex()
    {
        // 第一页只有题干，选项被显式换页推到第二页；页索引须为题内从 0 起的 1。
        var body = """
            合成题第一页：下列选项正确的是（\quad）。
            \newpage
            \begin{enumerate}
              \item[\optmark{q-001}{A}{A.}] 甲
              \item[\optmark{q-001}{B}{B.}] 乙
              \item[\optmark{q-001}{C}{C.}] 丙
              \item[\optmark{q-001}{D}{D.}] 丁
            \end{enumerate}
            """;

        var result = await ExtractAsync(body, pageAttribute: null);

        Assert.True(result.Succeeded, Describe(result));
        Assert.All(result.Artifact!.Questions[0].Options, option => Assert.Equal(1, option.PageIndex));
    }

    /// <summary>
    /// 同一份内容只改 <c>/Rotate</c>。由契约换算表可推出各角度与 0 度结果之间的精确关系，
    /// 因此不需要知道版心的绝对位置也能严格校验旋转是否按表应用。
    /// </summary>
    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public async Task Extract_RotatedPage_MatchesContractRotationTable(int rotation)
    {
        var baseline = await ExtractAsync(DefaultBody(), pageAttribute: null);
        Assert.True(baseline.Succeeded, Describe(baseline));
        var rotated = await ExtractAsync(DefaultBody(), $@"\special{{pdf:put @thispage <</Rotate {rotation}>>}}");
        Assert.True(rotated.Succeeded, Describe(rotated));

        var baselineOptions = baseline.Artifact!.Questions[0].Options;
        var rotatedOptions = rotated.Artifact!.Questions[0].Options;
        Assert.Equal(baselineOptions.Count, rotatedOptions.Count);

        for (var index = 0; index < baselineOptions.Count; index++)
        {
            var b = baselineOptions[index];
            var r = rotatedOptions[index];
            var (x, y, width, height) = rotation switch
            {
                90 => (1 - b.Y - b.Height, b.X, b.Height, b.Width),
                180 => (1 - b.X - b.Width, 1 - b.Y - b.Height, b.Width, b.Height),
                _ => (b.Y, 1 - b.X - b.Width, b.Height, b.Width)
            };

            Assert.Equal(x, r.X, 6);
            Assert.Equal(y, r.Y, 6);
            Assert.Equal(width, r.Width, 6);
            Assert.Equal(height, r.Height, 6);
        }
    }

    [Fact]
    public async Task Extract_CroppedPage_NormalizesRelativeToEffectiveDisplayBox()
    {
        // 无裁剪时的基线；裁剪后有效显示框更矮，同一版式的归一化纵向间距必须随之变大。
        // 若实现错误地按整页 MediaBox 归一化，两者会得到相同的间距。
        var baseline = await ExtractAsync(DefaultBody(), pageAttribute: null);
        Assert.True(baseline.Succeeded, Describe(baseline));

        var cropped = await ExtractAsync(
            DefaultBody(), @"\special{pdf:put @thispage <</CropBox [40 40 556 800]>>}");
        Assert.True(cropped.Succeeded, Describe(cropped));

        var baselineSpacing = Spacing(baseline.Artifact!.Questions[0].Options);
        var croppedSpacing = Spacing(cropped.Artifact!.Questions[0].Options);

        Assert.True(croppedSpacing > baselineSpacing * 1.02,
            $"裁剪后纵向归一化间距应变大：基线 {baselineSpacing:F6}，裁剪 {croppedSpacing:F6}");
    }

    [Fact]
    public async Task Extract_OptionWithoutMarker_ReportsMissingMarker()
    {
        var body = """
            合成题：下列选项正确的是（\quad）。

            \begin{enumerate}
              \item[\optmark{q-001}{A}{A.}] 甲
              \item[\optmark{q-001}{B}{B.}] 乙
              \item[\optmark{q-001}{C}{C.}] 丙
              \item[D.] 丁
            \end{enumerate}
            """;

        var result = await ExtractAsync(body, pageAttribute: null);

        Assert.False(result.Succeeded);
        Assert.Null(result.Artifact);
        Assert.Contains(result.Issues, issue =>
            issue.Code == OptionRegionIssueCode.MarkerMissing && issue.OptionId == "D");
    }

    [Fact]
    public async Task Extract_MarkerNotInManifest_ReportsUnexpectedMarker()
    {
        var body = """
            合成题：下列选项正确的是（\quad）。

            \begin{enumerate}
              \item[\optmark{q-001}{A}{A.}] 甲
              \item[\optmark{q-001}{B}{B.}] 乙
              \item[\optmark{q-001}{C}{C.}] 丙
              \item[\optmark{q-001}{D}{D.}] 丁
              \item[\optmark{q-001}{E}{E.}] 戊
            \end{enumerate}
            """;

        var result = await ExtractAsync(body, pageAttribute: null);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, issue => issue.Code == OptionRegionIssueCode.MarkerUnexpected);
    }

    [Fact]
    public async Task Extract_MarkerWithoutResolvableTarget_ReportsUnresolved()
    {
        // 只写链接不定义目标：原始字典里有目标名，但解析不出同一文档内的 GoTo 目标。
        var body = """
            合成题：下列选项正确的是（\quad）。

            \begin{enumerate}
              \item[\optmark{q-001}{A}{A.}] 甲
              \item[\optmark{q-001}{B}{B.}] 乙
              \item[\optmark{q-001}{C}{C.}] 丙
              \item[\hyperlink{opt:q-001:D}{D.}] 丁
            \end{enumerate}
            """;

        var result = await ExtractAsync(body, pageAttribute: null);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, issue => issue.Code == OptionRegionIssueCode.MarkerUnresolved);
    }

    [Fact]
    public async Task Extract_SeparatedDuplicateMarker_ReportsDuplicate()
    {
        // 同一选项在两处分离位置各标一次：属重复标记，不能取并集掩盖。
        var body = """
            合成题：下列选项正确的是（\quad）。
            \optmark{q-001}{A}{A.}

            \begin{enumerate}
              \item[\optmark{q-001}{A}{A.}] 甲
              \item[\optmark{q-001}{B}{B.}] 乙
              \item[\optmark{q-001}{C}{C.}] 丙
              \item[\optmark{q-001}{D}{D.}] 丁
            \end{enumerate}
            """;

        var result = await ExtractAsync(body, pageAttribute: null);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, issue =>
            issue.Code == OptionRegionIssueCode.MarkerDuplicate && issue.OptionId == "A");
    }

    [Fact]
    public void Extract_UnreadablePdf_ReportsPdfUnreadable()
    {
        Directory.CreateDirectory(Path.Combine(_packageRoot, "questions"));
        var path = Path.Combine(_packageRoot, SourceRelativePath);
        File.WriteAllText(path, "这不是 PDF");

        var result = Extract(path);

        Assert.False(result.Succeeded);
        Assert.Null(result.Artifact);
        Assert.Equal(OptionRegionIssueCode.PdfUnreadable, Assert.Single(result.Issues).Code);
    }

    [Fact]
    public void Extract_NameCollisionBetweenTwoOptionPairs_ReportsCollision()
    {
        // 标识内允许冒号，因此 "a:b"+"c" 与 "a"+"b:c" 会生成同一目标名，必须拒绝而不是猜绑定。
        var result = new OptionRegionExtractor().Extract(new OptionRegionRequest("synthetic", 1,
        [
            new OptionRegionSourceQuestion("a:b", Path.Combine(_root, "nonexistent-1.pdf"), ["c"]),
            new OptionRegionSourceQuestion("a", Path.Combine(_root, "nonexistent-2.pdf"), ["b:c"])
        ]));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Issues, issue => issue.Code == OptionRegionIssueCode.ExpectedNameCollision);
    }

    private static string DefaultBody() => """
        合成题：下列选项正确的是（\quad）。

        \begin{enumerate}
          \item[\optmark{q-001}{A}{A.}] 甲
          \item[\optmark{q-001}{B}{B.}] 乙
          \item[\optmark{q-001}{C}{C.}] 丙
          \item[\optmark{q-001}{D}{D.}] 丁
        \end{enumerate}
        """;

    private static double Spacing(IReadOnlyList<OptionRegion> options)
    {
        var spacings = new List<double>();
        for (var index = 1; index < options.Count; index++)
            spacings.Add(options[index].Y - options[index - 1].Y);
        return spacings.Average();
    }

    private async Task<OptionRegionExtractionResult> ExtractAsync(string body, string? pageAttribute)
    {
        BuildPackage(body, pageAttribute);
        _lastPdfPath = await CompileAsync();
        return Extract(_lastPdfPath);
    }

    private static OptionRegionExtractionResult Extract(string pdfPath) =>
        new OptionRegionExtractor().Extract(new OptionRegionRequest(
            "synthetic-contract", 1,
            [new OptionRegionSourceQuestion(QuestionId, pdfPath, OptionIds)]));

    private async Task<string> CompileAsync()
    {
        var compiler = CreateCompiler();
        var result = await compiler.CompileAsync(
            new TeXCompileRequest(_packageRoot, SourceRelativePath, _outputDirectory));

        Assert.True(result.Succeeded, Describe(result));
        Assert.NotNull(result.OutputPdfPath);
        return result.OutputPdfPath!;
    }

    /// <summary>最小合成包：三参数标记宏与 hidelinks 与合成样包样式保持同一约定。</summary>
    private void BuildPackage(string body, string? pageAttribute)
    {
        Directory.CreateDirectory(Path.Combine(_packageRoot, "questions"));

        const string template = """
            \documentclass[UTF8]{ctexart}
            \pagestyle{empty}
            \usepackage{hyperref}
            \hypersetup{hidelinks}
            \DeclareRobustCommand{\optmark}[3]{\hypertarget{opt:#1:#2}{}\hyperlink{opt:#1:#2}{#3}}
            \begin{document}
            __PAGEATTR__
            __BODY__
            \end{document}
            """;

        var source = template
            .Replace("__PAGEATTR__", pageAttribute ?? string.Empty, StringComparison.Ordinal)
            .Replace("__BODY__", body, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(_packageRoot, SourceRelativePath), source);
    }

    private static XeLaTeXCompiler CreateCompiler()
    {
        var toolchain = XeLaTeXToolchain.TryDetect(XeLaTeXPlatform.TeXLive);
        Assert.True(
            toolchain is not null,
            "本机没有可用的 XeLaTeX。Category=Latex 的用例需要 TeX 发行版（本项目要求 XeLaTeX）。");

        var runner = new ExternalProcessRunner();
        var version = runner.RunAsync(toolchain!.ExecutablePath, ["--version"],
            new Dictionary<string, string>(), TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        Assert.Equal(0, version.ExitCode);
        var platform = version.StandardOutput.Contains("MiKTeX", StringComparison.OrdinalIgnoreCase)
            ? XeLaTeXPlatform.MiKTeX : XeLaTeXPlatform.TeXLive;

        return new XeLaTeXCompiler(runner, toolchain with { Platform = platform }, TimeSpan.FromMinutes(3));
    }

    private static string Describe(OptionRegionExtractionResult result) =>
        result.Succeeded
            ? "抽取成功"
            : string.Join("；", result.Issues.Select(issue =>
                $"{issue.Code}@{issue.QuestionId}/{issue.OptionId}/{issue.PageIndex}: {issue.Message}"));

    private static string Describe(TeXCompileResult result) =>
        $"ExitCode={result.ExitCode} TimedOut={result.TimedOut} HasOutput={result.Report.HasOutput} " +
        $"PageCount={result.Report.PageCount} OutputFileName={result.Report.OutputFileName} | " +
        string.Join("；", result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));
}
