using Exam.Domain.Importing;

namespace Exam.UnitTests.Importing;

/// <summary>
/// 学生端包允许清单规则的用例。重点验证「不能仅按扩展名筛选」：改了名但内容带答案的文件也要被挡下。
/// </summary>
public sealed class StudentPackageAllowlistTests
{
    [Theory]
    [InlineData("server-only/answer-key.json")]
    [InlineData("server-only/解析/第1题.md")]
    [InlineData("Server-Only/answer-key.json")]
    [InlineData("server-only")]
    [InlineData(@"server-only\answer-key.json")]
    [InlineData("./server-only/answer-key.json")]
    public void IsServerOnly_RecognizesServerSideDirectory(string path)
    {
        Assert.True(StudentPackageAllowlist.IsServerOnly(path, out var reason));
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("questions/合成样题_1.pdf")]
    [InlineData("figures/chemistry/q-figure-01.png")]
    [InlineData("manifest.json")]
    [InlineData("server-only.txt")]
    public void IsServerOnly_LeavesOtherPathsAlone(string path)
    {
        Assert.False(StudentPackageAllowlist.IsServerOnly(path, out var reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("questions/答案_1.pdf")]
    [InlineData("figures/第1题解析.png")]
    [InlineData("styles/answer-macros.sty")]
    [InlineData("figures/solution-figure.png")]
    [InlineData("styles/SOLUTION.sty")]
    public void LooksLikeAnswerResource_RejectsAnswerNamedPaths(string path)
    {
        Assert.True(StudentPackageAllowlist.LooksLikeAnswerResource(path, out var reason));
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("questions/合成样题_1.pdf")]
    [InlineData("figures/chemistry/q-figure-01.png")]
    [InlineData("styles/合成样式.sty")]
    [InlineData("figures/答题卡.png")]
    public void LooksLikeAnswerResource_AcceptsOrdinaryNames(string path)
    {
        // 「答题卡」不含「答案」这个关键词，不应被误伤。
        Assert.False(StudentPackageAllowlist.LooksLikeAnswerResource(path, out var reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("""{"questionId":"q-001","correctOptionIds":["A"]}""")]
    [InlineData("""{"answers":[{"questionId":"q-001"}]}""")]
    [InlineData("""{"questionId":"q-001","SOLUTION":"选 A"}""")]
    [InlineData("""{"CorrectOptionIds":["A"]}""")]
    public void ContainsAnswerBearingJson_DetectsAnswerFields(string content)
    {
        Assert.True(StudentPackageAllowlist.ContainsAnswerBearingJson(content, out var reason));
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData("""{"questionId":"q-001","options":["A","B"]}""")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("第 1 题的选项是 A、B、C、D。")]
    [InlineData(@"\newcommand{\answers}[1]{}")]
    [InlineData(@"\newenvironment{solution}{\comment}{\endcomment}")]
    public void ContainsAnswerBearingJson_AcceptsCleanContent(string? content)
    {
        // 样式文件里名为 \answers 或 solution 的宏不是答案数据，不能因关键词出现就误伤——
        // 否则本该进包的共享样式会被悄悄排除掉。
        Assert.False(StudentPackageAllowlist.ContainsAnswerBearingJson(content, out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void ContainsAnswerBearingJson_ScansNestedProperties()
    {
        Assert.True(StudentPackageAllowlist.ContainsAnswerBearingJson(
            """{"examId":"x","questions":[{"questionId":"q-001","meta":{"answers":["A"]}}]}""",
            out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void IsAllowed_ServerOnlyWinsEvenWhenContentLooksClean()
    {
        // 服务器专用目录优先：即使内容看起来没问题也不放行。
        Assert.False(StudentPackageAllowlist.IsAllowed(
            "server-only/notes.txt",
            "这里只是一段普通说明。",
            out var reason));
        Assert.Contains("服务器专用目录", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void IsAllowed_DetectsAnswersHiddenInAnInnocuousName()
    {
        // 文件名完全正常，内容却是答案——只按扩展名或文件名筛选会漏掉。
        Assert.False(StudentPackageAllowlist.IsAllowed(
            "figures/chemistry/q-figure-01.txt",
            """{"questionId":"q-001","correctOptionIds":["B"]}""",
            out var reason));
        Assert.Contains("答案相关字段", reason!, StringComparison.Ordinal);
    }

    [Fact]
    public void IsAllowed_AcceptsOrdinaryPackageFiles()
    {
        Assert.True(StudentPackageAllowlist.IsAllowed(
            "questions/合成样题_1.pdf", null, out var reason));
        Assert.Null(reason);

        Assert.True(StudentPackageAllowlist.IsAllowed(
            "manifest.json", """{"examId":"synthetic-package"}""", out reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsAllowed_EmptyPath_IsRejected(string? path)
    {
        Assert.False(StudentPackageAllowlist.IsAllowed(path, null, out var reason));
        Assert.NotNull(reason);
    }
}
