using System.Text.Json;

using Exam.Contracts.Importing;
using Exam.Domain.Importing;

namespace Exam.UnitTests.Importing;

/// <summary>
/// 把 samples/synthetic 下的合成样例当作可执行的验收清单：
/// 合法样包必须全部通过校验，错误样包必须只报出 expected-errors.json 声明的那几条规则。
/// 新增用例只需改样例与期望清单，不改测试。
/// </summary>
public sealed class SamplePackageTests
{
    private static readonly ExamManifestValidator Validator = new();
    private static readonly string SamplesRoot = TestRepositoryPaths.SyntheticSamplesRoot;
    private static readonly IReadOnlyList<ExpectedCase> Expectations = ReadExpectations();

    public static TheoryData<string> ValidSampleNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in DiscoverValidSampleNames())
            {
                data.Add(name);
            }

            return data;
        }
    }

    public static TheoryData<string> ErrorCaseNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var expected in Expectations)
            {
                data.Add(expected.Case);
            }

            return data;
        }
    }

    [Fact]
    public void Samples_ContainRequiredPackagesAndErrorCases()
    {
        foreach (var name in new[] { "minimal", "three-types", "figures-chemistry", "ordering" })
        {
            Assert.True(
                Directory.Exists(Path.Combine(SamplesRoot, name)),
                $"缺少合成样包：{name}");
        }

        // 防截断：错误样包是与 expected-errors.json 绑定的验收清单，数量异常说明文件被破坏。
        Assert.True(Expectations.Count >= 20, $"错误样包数量异常：{Expectations.Count}");
    }

    [Fact]
    public void ErrorPackages_MatchExpectationList()
    {
        var directories = Directory
            .GetDirectories(Path.Combine(SamplesRoot, "errors"))
            .Select(directory => Path.GetFileName(directory)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var declared = Expectations
            .Select(expected => expected.Case)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(declared, directories);
    }

    [Theory]
    [MemberData(nameof(ValidSampleNames))]
    public void ValidSample_IsAccepted(string name)
    {
        var (manifest, answerKey) = ReadPackage(Path.Combine(SamplesRoot, name));

        var report = Validator.ValidateJson(manifest, answerKey);

        Assert.True(report.IsValid, $"{name}: " + Describe(report));
        Assert.False(report.RequiresAnswerKeyArithmetic, $"{name}: 满分核对不应仍需答案");
    }

    [Theory]
    [MemberData(nameof(ErrorCaseNames))]
    public void InvalidSample_ReportsExactlyDeclaredRules(string name)
    {
        var expected = Expectations.Single(item => item.Case == name);
        var (manifest, answerKey) = ReadPackage(Path.Combine(SamplesRoot, "errors", name));

        var report = Validator.ValidateJson(manifest, answerKey);

        Assert.False(report.IsValid, $"{name}: 错误样包必须被拒绝");

        var actual = report.Issues.Select(issue => (issue.Rule, issue.Location)).ToList();

        foreach (var want in expected.Expect)
        {
            Assert.True(
                actual.Any(item => item.Rule == want.Rule && item.Location == want.Location),
                $"{name}: 期望 {want.Rule}@{want.Location}，实际为 {Describe(report)}");
        }

        if (!expected.ExpectAdditionalErrors)
        {
            var declared = expected.Expect.Select(item => (item.Rule, item.Location)).ToHashSet();
            var extra = actual.Where(item => !declared.Contains(item)).ToList();

            Assert.True(
                extra.Count == 0,
                $"{name}: 出现未声明的额外错误 {string.Join(", ", extra.Select(item => $"{item.Rule}@{item.Location}"))}");
        }
    }

    [Fact]
    public void ThreeTypesSample_WithoutAnswerKey_OnlyFlagsScoreCheck()
    {
        // 含 {正确选项总数} 的题组无法在缺答案时核对满分，此时必须通过校验但置位标志，
        // 由 精确计分/评分处理 在拿到答案后完成核对。
        var (manifest, _) = ReadPackage(Path.Combine(SamplesRoot, "three-types"));

        var report = Validator.ValidateJson(manifest);

        Assert.True(report.IsValid, Describe(report));
        Assert.True(report.RequiresAnswerKeyArithmetic);
    }

    private static IEnumerable<string> DiscoverValidSampleNames() =>
        Directory
            .GetDirectories(SamplesRoot)
            .Select(directory => Path.GetFileName(directory))
            .Where(name => !string.Equals(name, "errors", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal);

    private static (string Manifest, string? AnswerKey) ReadPackage(string directory)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        var answerKeyPath = Path.Combine(directory, "server-only", "answer-key.json");

        Assert.True(File.Exists(manifestPath), $"缺少清单：{manifestPath}");

        return (
            File.ReadAllText(manifestPath),
            File.Exists(answerKeyPath) ? File.ReadAllText(answerKeyPath) : null);
    }

    private static string Describe(ImportValidationReport report) =>
        report.Issues.Count == 0
            ? "无问题"
            : string.Join(" | ", report.Issues.Select(issue => $"{issue.Rule}@{issue.Location} {issue.Message}"));

    private static IReadOnlyList<ExpectedCase> ReadExpectations()
    {
        var json = TestRepositoryPaths.ReadText("samples", "synthetic", "errors", "expected-errors.json");
        using var document = JsonDocument.Parse(json);

        var cases = new List<ExpectedCase>();
        foreach (var element in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var expect = element
                .GetProperty("expect")
                .EnumerateArray()
                .Select(item => new ExpectedIssue(
                    item.GetProperty("rule").GetString()!,
                    item.GetProperty("location").GetString()!))
                .ToList();

            cases.Add(new ExpectedCase(
                element.GetProperty("case").GetString()!,
                expect,
                element.GetProperty("expectAdditionalErrors").GetBoolean()));
        }

        return cases;
    }

    private sealed record ExpectedIssue(string Rule, string Location);

    private sealed record ExpectedCase(string Case, IReadOnlyList<ExpectedIssue> Expect, bool ExpectAdditionalErrors);
}
