using System.Text.Json;
using System.Text.RegularExpressions;

using Exam.Domain.Importing;

namespace Exam.UnitTests.Importing;

public sealed class NamingRulesTests
{
    private static readonly JsonDocument ManifestSchema =
        JsonDocument.Parse(TestRepositoryPaths.ReadText("schemas", "exam-manifest.schema.json"));

    [Theory]
    [InlineData("a")]
    [InlineData("a/b")]
    [InlineData("figures/question-2-diagram.png")]
    [InlineData("questions/三题型合成考试_1.tex")]
    [InlineData("a//b")]
    [InlineData("a/b/")]
    [InlineData("./a")]
    [InlineData("..a")]
    [InlineData("a..b")]
    [InlineData("a/./b")]
    [InlineData("1:/a")]
    [InlineData("a:b")]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../a")]
    [InlineData("a/..")]
    [InlineData("a/../b")]
    [InlineData("../outside_1.tex")]
    [InlineData("/a")]
    [InlineData("\\a")]
    [InlineData("C:/a")]
    [InlineData("c:a")]
    [InlineData("a/..\\b")]
    public void IsRelativeAndInside_MatchesSchemaPattern(string path)
    {
        // schema 的 relativePath 正则是这条路线的对外契约，C# 判定必须与它逐例等价。
        var pattern = ManifestSchema.RootElement
            .GetProperty("$defs")
            .GetProperty("relativePath")
            .GetProperty("pattern")
            .GetString()!;

        Assert.Equal(Regex.IsMatch(path, pattern), PackagePathRules.IsRelativeAndInside(path));
    }

    [Theory]
    [InlineData("questions/演示考试_1.tex")]
    [InlineData("演示考试_1.tex")]
    [InlineData("演示考试_1.TEX")]
    [InlineData("questions/演示考试_1.TeX")]
    [InlineData("questions/演示考试.tex")]
    [InlineData("questions/演示考试_1.pdf")]
    [InlineData("questions/演示考试_x.tex")]
    [InlineData("../演示考试_1.tex")]
    public void QuestionSourcePattern_RejectsAndAcceptsAsExpected(string path)
    {
        var pattern = ManifestSchema.RootElement
            .GetProperty("$defs")
            .GetProperty("questionSource")
            .GetProperty("allOf")[1]
            .GetProperty("pattern")
            .GetString()!;

        Assert.Equal(QuestionSourceFileName.TryParse(path, out _, out _), Regex.IsMatch(path, pattern));
    }

    [Theory]
    [InlineData("questions/三题型合成考试_1.tex", "三题型合成考试", 1)]
    [InlineData("三题型合成考试_10.tex", "三题型合成考试", 10)]
    [InlineData("考试_名称_10.tex", "考试_名称", 10)]
    [InlineData("考试_01.tex", "考试", 1)]
    [InlineData("考试_1.TEX", "考试", 1)]
    [InlineData("a/b/考试_7.tex", "考试", 7)]
    [InlineData("考试_1_2.tex", "考试_1", 2)]
    public void TryParse_ValidName_ReturnsPrefixAndNumber(string path, string prefix, int questionNo)
    {
        Assert.True(QuestionSourceFileName.TryParse(path, out var actualPrefix, out var actualNo));
        Assert.Equal(prefix, actualPrefix);
        Assert.Equal(questionNo, actualNo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("考试")]
    [InlineData("考试.tex")]
    [InlineData("考试_x.tex")]
    [InlineData("_1.tex")]
    [InlineData("考试_.tex")]
    [InlineData("考试_1.pdf")]
    [InlineData("考试_1.tex/x")]
    [InlineData("考试_99999999999999999999.tex")]
    [InlineData("考试_1.tex.tex")]
    public void TryParse_InvalidName_IsRejected(string path)
    {
        Assert.False(QuestionSourceFileName.TryParse(path, out _, out _));
    }

    [Theory]
    [InlineData("合成最小考试")]
    [InlineData("exam-1")]
    [InlineData("合 成 考 试")]
    [InlineData("a.b")]
    public void IsValidFileName_AcceptsUsableNames(string candidate)
    {
        Assert.True(WindowsFileNameRules.IsValidFileName(candidate), WindowsFileNameRules.Describe(candidate));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("错误合成考试:")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a\"b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a|b")]
    [InlineData("a?b")]
    [InlineData("a*b")]
    [InlineData("a.")]
    [InlineData("a ")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("AUX")]
    [InlineData("COM1")]
    [InlineData("lpt9")]
    [InlineData("错误合成考试:_1")]
    [InlineData("a\u0001b")]
    public void IsValidFileName_RejectsUnusableNames(string candidate)
    {
        Assert.False(WindowsFileNameRules.IsValidFileName(candidate));
        Assert.NotNull(WindowsFileNameRules.Describe(candidate));
    }

    [Fact]
    public void IsValidFileName_UsesWindowsRulesOnEveryPlatform()
    {
        // ':' 在 Linux 上合法，因此不能依赖 Path.GetInvalidFileNameChars()——
        // 它的取值随运行平台变化，本系统要求在任何平台上按 Windows 规则判定。
        Assert.False(WindowsFileNameRules.IsValidFileName("错误合成考试:"));
        Assert.Contains("不能包含字符", WindowsFileNameRules.Describe("错误合成考试:"), StringComparison.Ordinal);
    }

    [Fact]
    public void IsValidFileName_RejectsNameLongerThanLimit()
    {
        var candidate = new string('a', WindowsFileNameRules.MaxLength + 1);

        Assert.False(WindowsFileNameRules.IsValidFileName(candidate));
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("3.5", "3.5")]
    [InlineData("-0.75", "-0.75")]
    [InlineData("+2", "2")]
    [InlineData("100", "100")]
    public void ScoreConstant_ValidValue_ParsesExactly(string value, string expected)
    {
        Assert.True(ScoreConstantRules.TryParse(value, out var exact));
        Assert.Equal(expected, exact.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData(".5")]
    [InlineData("1.")]
    [InlineData("1.2.3")]
    [InlineData("1+1")]
    [InlineData("{正确选项总数}")]
    [InlineData("1e3")]
    [InlineData("1,000")]
    public void ScoreConstant_InvalidValue_IsRejected(string? value)
    {
        Assert.False(ScoreConstantRules.IsValid(value));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3.5")]
    [InlineData("-0.75")]
    [InlineData("+2")]
    [InlineData("")]
    [InlineData(" 1")]
    [InlineData(".5")]
    [InlineData("1.")]
    [InlineData("1+1")]
    [InlineData("{正确选项总数}")]
    public void ScoreConstant_MatchesSchemaPattern(string value)
    {
        // 满分常量规则与 schema 的 scoreConstant 正则必须逐例一致。
        var pattern = ManifestSchema.RootElement
            .GetProperty("$defs")
            .GetProperty("scoreConstant")
            .GetProperty("pattern")
            .GetString()!;

        Assert.Equal(Regex.IsMatch(value, pattern), ScoreConstantRules.IsValid(value));
    }
}
