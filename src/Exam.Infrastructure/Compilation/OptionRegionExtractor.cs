using Exam.Contracts.Importing;
using Exam.Domain.Importing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Actions;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Tokens;

namespace Exam.Infrastructure.Compilation;

/// <summary>
/// 从逐题编译产物读取隐藏外观链接标记，产出归一化的选项定位区域。
/// 数据语义与接入边界见 docs/d03-option-region-contract.md 第 4 节。
///
/// 关键约定（不可自行放宽）：
/// <list type="bullet">
/// <item>几何取原始字典的 <c>/Rect</c>，不取 PdfPig 已应用初始矩阵的 <c>Annotation.Rectangle</c>。</item>
/// <item>目标名取自原始动作字典的 <c>/A /D</c> 或直接 <c>/Dest</c>；<c>Annotation.Name</c> 是 <c>/NM</c>，不能充当目标名。</item>
/// <item>只筛选 <c>page.GetAnnotations()</c> 的 Link 注解；<c>GetHyperlinks()</c> 只含外部链接，不能使用。</item>
/// <item>标记必须解析为同一 PDF 内的 GoTo 目标，且目标页与注解页一致。</item>
/// <item>失败一律整次不产出坐标文件，不做部分成功、不静默跳过。</item>
/// </list>
/// </summary>
public sealed class OptionRegionExtractor
{
    public OptionRegionExtractionResult Extract(OptionRegionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Validate(request) is { Count: > 0 } requestIssues)
            return OptionRegionExtractionResult.Failure(requestIssues);

        var expected = new List<ExpectedOption>();
        if (BuildExpectedOptions(request, expected) is { Count: > 0 } collisionIssues)
            return OptionRegionExtractionResult.Failure(collisionIssues);

        var issues = new List<OptionRegionIssue>();
        var extracted = request.Questions
            .Select(question => (
                Question: question,
                Options: ExtractQuestion(
                    question,
                    expected.Where(item => ReferenceEquals(item.Question, question)).ToList(),
                    issues)))
            .ToList();

        if (issues.Count > 0)
            return OptionRegionExtractionResult.Failure(issues);

        // 全部选项都拿到区域才构造产物：任何问题都整次失败，不产出部分坐标文件。
        var questions = extracted
            .Select(item => new OptionRegionQuestion(item.Question.QuestionId, item.Options))
            .ToList();

        return new OptionRegionExtractionResult(
            new OptionRegionArtifact(
                OptionRegionArtifact.CurrentArtifactVersion,
                OptionRegionArtifact.AnchorSchemeName,
                request.ExamId,
                request.PaperVersion,
                questions),
            []);
    }

    private static IReadOnlyList<OptionRegionIssue> Validate(OptionRegionRequest request)
    {
        var issues = new List<OptionRegionIssue>();

        if (string.IsNullOrWhiteSpace(request.ExamId))
            issues.Add(new OptionRegionIssue(OptionRegionIssueCode.RequestInvalid, "考试标识不能为空"));
        if (request.PaperVersion <= 0)
            issues.Add(new OptionRegionIssue(OptionRegionIssueCode.RequestInvalid, "试卷版本必须为正"));
        if (request.Questions.Count == 0)
            issues.Add(new OptionRegionIssue(OptionRegionIssueCode.RequestInvalid, "没有需要抽取的题目"));

        var questionIds = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in request.Questions)
        {
            if (string.IsNullOrWhiteSpace(question.QuestionId))
                issues.Add(new OptionRegionIssue(OptionRegionIssueCode.RequestInvalid, "题目标识不能为空"));
            else if (!questionIds.Add(question.QuestionId))
                issues.Add(new OptionRegionIssue(OptionRegionIssueCode.RequestInvalid, "题目标识重复", question.QuestionId));

            if (string.IsNullOrWhiteSpace(question.PdfPath))
                issues.Add(new OptionRegionIssue(OptionRegionIssueCode.RequestInvalid, "题目 PDF 路径不能为空", question.QuestionId));
            else if (!paths.Add(Path.GetFullPath(question.PdfPath)))
                issues.Add(new OptionRegionIssue(OptionRegionIssueCode.RequestInvalid, "同一 PDF 被两道题引用，无法区分标记归属", question.QuestionId));

            if (question.OptionIds.Count == 0)
            {
                issues.Add(new OptionRegionIssue(OptionRegionIssueCode.RequestInvalid, "题目没有选项", question.QuestionId));
                continue;
            }

            var optionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var optionId in question.OptionIds)
            {
                if (string.IsNullOrWhiteSpace(optionId))
                    issues.Add(new OptionRegionIssue(OptionRegionIssueCode.RequestInvalid, "选项标识不能为空", question.QuestionId));
                else if (!optionIds.Add(optionId))
                    issues.Add(new OptionRegionIssue(OptionRegionIssueCode.RequestInvalid, "选项标识重复", question.QuestionId, optionId));
            }
        }

        return issues;
    }

    /// <summary>
    /// 按清单生成期望的目标名。标识内允许出现冒号，因此只做精确比较，不把名称反解成三段。
    /// 两个不同组合生成同名时无法唯一绑定，必须拒绝。
    /// </summary>
    private static IReadOnlyList<OptionRegionIssue> BuildExpectedOptions(
        OptionRegionRequest request,
        List<ExpectedOption> expected)
    {
        var issues = new List<OptionRegionIssue>();
        var seen = new Dictionary<string, ExpectedOption>(StringComparer.Ordinal);

        foreach (var question in request.Questions)
        {
            foreach (var optionId in question.OptionIds)
            {
                var name = OptionRegionArtifact.MarkerName(question.QuestionId, optionId);
                var item = new ExpectedOption(question, optionId, name);
                if (seen.TryGetValue(name, out var previous))
                {
                    issues.Add(new OptionRegionIssue(
                        OptionRegionIssueCode.ExpectedNameCollision,
                        $"目标名 {name} 同时对应 ({previous.Question.QuestionId}, {previous.OptionId}) 与 ({question.QuestionId}, {optionId})",
                        question.QuestionId,
                        optionId));
                    continue;
                }

                seen[name] = item;
                expected.Add(item);
            }
        }

        return issues;
    }

    private static List<OptionRegion> ExtractQuestion(
        OptionRegionSourceQuestion question,
        IReadOnlyList<ExpectedOption> expected,
        List<OptionRegionIssue> issues)
    {
        var regions = new Dictionary<string, OptionRegion>(StringComparer.Ordinal);

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(question.PdfPath);
        }
        catch (Exception)
        {
            // 第三方库在损坏或不可读文件上抛出的异常一律归为稳定的 PdfUnreadable，
            // 不把异常原文带进问题消息。
            issues.Add(new OptionRegionIssue(OptionRegionIssueCode.PdfUnreadable, "题目 PDF 无法打开", question.QuestionId));
            return [];
        }

        using (document)
        {
            if (document.NumberOfPages < 1)
            {
                issues.Add(new OptionRegionIssue(OptionRegionIssueCode.PdfUnreadable, "题目 PDF 没有页面", question.QuestionId));
                return [];
            }

            var expectedNames = expected.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
            var fragments = new Dictionary<string, List<Fragment>>(StringComparer.Ordinal);

            for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                var page = document.GetPage(pageNumber);
                var pageIndex = pageNumber - 1;
                var rotation = page.Rotation.Value;

                if (!OptionRegionGeometry.IsSupportedRotation(rotation))
                {
                    issues.Add(new OptionRegionIssue(
                        OptionRegionIssueCode.PdfUnreadable,
                        $"页面 Rotate 只能是 0/90/180/270，实际为 {rotation}", question.QuestionId, null, pageIndex));
                    continue;
                }

                if (!TryEffectiveDisplayBox(page, out var displayBox))
                {
                    issues.Add(new OptionRegionIssue(
                        OptionRegionIssueCode.PdfUnreadable,
                        "页面缺少可用的 MediaBox/CropBox，或有效显示框为空",
                        question.QuestionId, null, pageIndex));
                    continue;
                }

                foreach (var annotation in page.GetAnnotations())
                {
                    if (annotation.Type != AnnotationType.Link) continue;

                    var name = TryReadDestinationName(annotation);
                    if (name is null || !name.StartsWith(OptionRegionArtifact.MarkerPrefix, StringComparison.Ordinal))
                        continue;   // 普通链接不计入选项数

                    if (!expectedNames.Contains(name))
                    {
                        issues.Add(new OptionRegionIssue(
                            OptionRegionIssueCode.MarkerUnexpected,
                            $"PDF 中存在清单未声明的标记 {name}", question.QuestionId, null, pageIndex));
                        continue;
                    }

                    if (annotation.Action is not GoToAction { Destination: { } destination })
                    {
                        issues.Add(Unresolved(name, question.QuestionId, pageIndex, "标记不是同一文档内的 GoTo 目标"));
                        continue;
                    }

                    if (destination.PageNumber < 1 || destination.PageNumber > document.NumberOfPages)
                    {
                        issues.Add(new OptionRegionIssue(
                            OptionRegionIssueCode.PageIndexInvalid,
                            $"标记 {name} 的目标页 {destination.PageNumber} 超出该题 PDF 的 {document.NumberOfPages} 页",
                            question.QuestionId, null, pageIndex));
                        continue;
                    }

                    if (destination.PageNumber != pageNumber)
                    {
                        issues.Add(new OptionRegionIssue(
                            OptionRegionIssueCode.DestinationPageMismatch,
                            $"标记 {name} 的目标在第 {destination.PageNumber} 页，注解在第 {pageNumber} 页",
                            question.QuestionId, null, pageIndex));
                        continue;
                    }

                    if (!TryReadRawRect(annotation, out var rawRect))
                    {
                        issues.Add(Unresolved(name, question.QuestionId, pageIndex, "注解缺少合法的 /Rect（四个有限数）"));
                        continue;
                    }

                    if (!fragments.TryGetValue(name, out var list))
                        fragments[name] = list = [];
                    list.Add(new Fragment(pageIndex, displayBox, rotation, rawRect));
                }
            }

            foreach (var item in expected)
            {
                if (!fragments.TryGetValue(item.Name, out var list))
                {
                    issues.Add(new OptionRegionIssue(
                        OptionRegionIssueCode.MarkerMissing, "清单声明的选项没有对应标签区域",
                        question.QuestionId, item.OptionId));
                    continue;
                }

                if (list.Select(fragment => fragment.PageIndex).Distinct().Count() != 1)
                {
                    issues.Add(new OptionRegionIssue(
                        OptionRegionIssueCode.MarkerDuplicate, "同名标记跨页出现，禁止跨页取并集",
                        question.QuestionId, item.OptionId, list[0].PageIndex));
                    continue;
                }

                var first = list[0];
                if (!OptionRegionGeometry.TryMergeConnected(
                        list.Select(fragment => fragment.RawRect).ToList(), out var union))
                {
                    issues.Add(new OptionRegionIssue(
                        OptionRegionIssueCode.MarkerDuplicate, "同名标记相互分离，只能合并重叠或相接的碎片",
                        question.QuestionId, item.OptionId, first.PageIndex));
                    continue;
                }

                var normalized = OptionRegionGeometry.Normalize(union, first.DisplayBox, first.Rotation);
                if (!normalized.Succeeded || normalized.Region is not { } region)
                {
                    issues.Add(new OptionRegionIssue(
                        normalized.Issue, normalized.Message,
                        question.QuestionId, item.OptionId, first.PageIndex));
                    continue;
                }

                regions[item.OptionId] = new OptionRegion(
                    item.OptionId, first.PageIndex, region.X, region.Y, region.Width, region.Height);
            }
        }

        // 结果按清单选项顺序输出；有问题的选项此前已记入 issues，调用方据此整次失败。
        return expected
            .Select(item => regions.TryGetValue(item.OptionId, out var region) ? region : null)
            .OfType<OptionRegion>()
            .ToList();
    }

    private static OptionRegionIssue Unresolved(string name, string questionId, int pageIndex, string reason) =>
        new(OptionRegionIssueCode.MarkerUnresolved, $"标记 {name} 无法解析：{reason}", questionId, null, pageIndex);

    /// <summary>目标名取自原始动作字典的 <c>/A /D</c> 或直接 <c>/Dest</c>；不使用 <c>Annotation.Name</c>（即 <c>/NM</c>）。</summary>
    private static string? TryReadDestinationName(Annotation annotation)
    {
        var dictionary = annotation.AnnotationDictionary;

        if (dictionary.TryGet(NameToken.Create("A"), out var action) && action is DictionaryToken actionDictionary &&
            actionDictionary.TryGet(NameToken.Create("D"), out var actionDestination) &&
            TokenText(actionDestination) is { } fromAction)
            return fromAction;

        if (dictionary.TryGet(NameToken.Create("Dest"), out var directDestination))
            return TokenText(directDestination);

        return null;
    }

    private static string? TokenText(IToken token) => token switch
    {
        StringToken text => text.Data,
        NameToken name => name.Data,
        _ => null
    };

    private static bool TryReadRawRect(Annotation annotation, out RawRect rect)
    {
        rect = default;
        if (!annotation.AnnotationDictionary.TryGet(NameToken.Create("Rect"), out var token) ||
            token is not ArrayToken { Length: 4 } array)
            return false;

        Span<double> values = stackalloc double[4];
        for (var index = 0; index < 4; index++)
        {
            if (array[index] is not NumericToken number) return false;
            values[index] = number.Double;
        }

        rect = RawRect.FromNumbers(values);
        return true;
    }

    /// <summary>有效显示框取 CropBox 与 MediaBox 的交集，缺少 CropBox 时使用 MediaBox。</summary>
    private static bool TryEffectiveDisplayBox(Page page, out PageDisplayBox displayBox)
    {
        displayBox = default;
        if (page.MediaBox?.Bounds is not { } media) return false;
        var crop = page.CropBox?.Bounds is { } cropBox ? cropBox : media;

        var left = Math.Max(crop.Left, media.Left);
        var bottom = Math.Max(crop.Bottom, media.Bottom);
        var right = Math.Min(crop.Right, media.Right);
        var top = Math.Min(crop.Top, media.Top);

        displayBox = new PageDisplayBox(left, bottom, right - left, top - bottom);
        return double.IsFinite(displayBox.Width) && double.IsFinite(displayBox.Height) &&
               displayBox.Width > 0 && displayBox.Height > 0;
    }

    private sealed record ExpectedOption(OptionRegionSourceQuestion Question, string OptionId, string Name);

    private readonly record struct Fragment(int PageIndex, PageDisplayBox DisplayBox, int Rotation, RawRect RawRect);
}
