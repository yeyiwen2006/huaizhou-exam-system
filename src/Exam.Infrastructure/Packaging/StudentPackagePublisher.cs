using Exam.Contracts.Importing;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Compilation;
using Exam.Infrastructure.Security;

namespace Exam.Infrastructure.Packaging;

/// <summary>
/// D03 的发布流程：从**本次已验收的逐题编译产物**抽取选项坐标，再按学生包规则组包，
/// 一次调用产出可点击作答的学生包（学生清单 v2 + 根目录 option-regions.json）。
///
/// 为什么要有这一层：组包器只打包、抽取器只出坐标，调用方（导入发布入口、CLI）不该同时懂两者。
/// 这里固定发布语义——**必须可点击作答**：
/// <list type="bullet">
/// <item>不基于平铺目录发布：平铺目录无法证明 PDF 与题目的对应关系，必须传本次逐题编译结果。</item>
/// <item>任一题缺少成功编译的产物、或任一选项抽不到区域，整次发布失败，不产出只能看不能点的包。</item>
/// <item>抽取结果仍由组包器二次核对考试、试卷版本与题目选项集合，两侧都不放过不一致。</item>
/// </list>
/// 抽取与组包各自的约定见 docs/d03-option-region-contract.md 第 4 节。
/// </summary>
public sealed class StudentPackagePublisher(PackageEncryptionService encryption)
{
    private readonly PackageEncryptionService _encryption =
        encryption ?? throw new ArgumentNullException(nameof(encryption));

    public async Task<StudentPackageResult> PublishAsync(
        ExamManifest manifest,
        StudentPackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(request);

        if (request.CompiledQuestions is not { } compiled)
            return Failed(manifest, request, "发布必须传入本次逐题编译结果；不能按平铺目录发布可点击作答的学生包。");

        var structure = ImportedGradingRules.BuildStructure(manifest);
        var sources = new List<OptionRegionSourceQuestion>(structure.Questions.Count);
        foreach (var question in structure.Questions)
        {
            if (!compiled.TryGetValue(question.QuestionId, out var result) || result is null ||
                !result.Succeeded || result.OutputPdfPath is null || result.Issues.Count != 0)
                return Failed(manifest, request, $"题目 {question.QuestionId} 缺少本次成功编译的产物，无法抽取选项坐标。");

            sources.Add(new OptionRegionSourceQuestion(
                question.QuestionId, result.OutputPdfPath, question.OptionIds));
        }

        var extraction = new OptionRegionExtractor().Extract(
            new OptionRegionRequest(manifest.ExamId, request.PaperVersion, sources));

        if (extraction.Artifact is not { } artifact || !extraction.Succeeded)
        {
            // 坐标问题的定位信息（题目标识、选项标识、所在页）原样带出，供导入预览逐条展示。
            var issues = extraction.Issues
                .Select(issue => new StudentPackageIssue(
                    StudentPackageIssueCode.OptionRegionInvalid,
                    Describe(issue),
                    OptionRegionArtifact.FileName))
                .ToList();
            if (issues.Count == 0)
            {
                issues.Add(new StudentPackageIssue(
                    StudentPackageIssueCode.OptionRegionInvalid, "选项坐标抽取失败。", OptionRegionArtifact.FileName));
            }

            return new StudentPackageResult(false, EmptyPlan(manifest, request), null, null, issues);
        }

        return await new StudentPackageBuilder(_encryption)
            .BuildAsync(request with { OptionRegions = artifact }, cancellationToken);
    }

    private static StudentPackageResult Failed(ExamManifest manifest, StudentPackageRequest request, string message) =>
        new(false, EmptyPlan(manifest, request), null, null,
            [new StudentPackageIssue(StudentPackageIssueCode.OptionRegionInvalid, message)]);

    /// <summary>发布前失败：没有任何文件进包，计划为空但保留考试与试卷版本。</summary>
    private static StudentPackagePlan EmptyPlan(ExamManifest manifest, StudentPackageRequest request) =>
        new(manifest.ExamId, request.PaperVersion, [], []);

    private static string Describe(OptionRegionIssue issue) => string.Join(' ', new[]
    {
        issue.QuestionId is null ? null : $"题目 {issue.QuestionId}",
        issue.OptionId is null ? null : $"选项 {issue.OptionId}",
        issue.PageIndex is null ? null : $"第 {issue.PageIndex} 页",
        issue.Message
    }.Where(part => part is not null));
}
