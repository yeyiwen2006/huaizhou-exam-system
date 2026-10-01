using Exam.Contracts.Importing;
using Exam.Domain.Importing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Actions;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace Exam.Infrastructure.Compilation;

/// <summary>
/// 从逐题编译产物读取隐藏外观链接标记，产出归一化的选项定位区域。
/// 数据语义与接入边界见 docs/d03-option-region-contract.md 第 4 节。
///
/// 关键约定（不可自行放宽）：
/// <list type="bullet">
/// <item>原始 <c>/Rect</c> 用于标记归属及几何校验；可读文字标签进一步取字母/数字的可见字形框，排除标点和链接留白。</item>
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
        if (request.Questions is null || request.Questions.Count == 0)
        {
            issues.Add(new OptionRegionIssue(OptionRegionIssueCode.RequestInvalid, "没有需要抽取的题目"));
            return issues;
        }

        var questionIds = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var question in request.Questions)
        {
            if (question is null)
            {
                issues.Add(new OptionRegionIssue(OptionRegionIssueCode.RequestInvalid, "题目不能为空"));
                continue;
            }
            if (string.IsNullOrWhiteSpace(question.QuestionId))
                issues.Add(new OptionRegionIssue(OptionRegionIssueCode.RequestInvalid, "题目标识不能为空"));
            else if (!questionIds.Add(question.QuestionId))
                issues.Add(new OptionRegionIssue(OptionRegionIssueCode.RequestInvalid, "题目标识重复", question.QuestionId));

            try
            {
                if (string.IsNullOrWhiteSpace(question.PdfPath) || !Path.IsPathFullyQualified(question.PdfPath))
                    issues.Add(new OptionRegionIssue(OptionRegionIssueCode.RequestInvalid, "题目 PDF 必须使用绝对路径", question.QuestionId));
                else if (!paths.Add(Path.GetFullPath(question.PdfPath)))
                    issues.Add(new OptionRegionIssue(OptionRegionIssueCode.RequestInvalid, "同一 PDF 被两道题引用，无法区分标记归属", question.QuestionId));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                issues.Add(new OptionRegionIssue(OptionRegionIssueCode.RequestInvalid, "题目 PDF 路径无效", question.QuestionId));
            }

            if (question.OptionIds is null || question.OptionIds.Count == 0)
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

        int? currentPageIndex = null;
        try
        {
            using var document = PdfDocument.Open(question.PdfPath);
            if (document.NumberOfPages < 1)
            {
                issues.Add(new OptionRegionIssue(OptionRegionIssueCode.PdfUnreadable, "题目 PDF 没有页面", question.QuestionId));
                return [];
            }

            var expectedNames = expected.ToDictionary(item => item.Name, StringComparer.Ordinal);
            var fragments = new Dictionary<string, List<Fragment>>(StringComparer.Ordinal);
            var pages = new Dictionary<int, Page>();

            for (var pageNumber = 1; pageNumber <= document.NumberOfPages; pageNumber++)
            {
                var pageIndex = pageNumber - 1;
                currentPageIndex = pageIndex;
                var page = document.GetPage(pageNumber);
                pages[pageIndex] = page;
                var rotation = page.Rotation.Value;

                if (!OptionRegionGeometry.IsSupportedRotation(rotation))
                {
                    issues.Add(new OptionRegionIssue(
                        OptionRegionIssueCode.PdfUnreadable,
                        $"页面 Rotate 只能是 0/90/180/270，实际为 {rotation}", question.QuestionId, null, pageIndex));
                    continue;
                }

                if (!TryEffectiveDisplayBox(page, document, out var displayBox))
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

                    var (name, ambiguous) = ReadMarkerDestination(annotation, document);
                    if (name is null) continue; // 普通链接不计入选项数。

                    expectedNames.TryGetValue(name, out var expectedOption);
                    if (ambiguous || (annotation.Flags & (AnnotationFlags.Hidden | AnnotationFlags.NoView)) != 0)
                    {
                        issues.Add(new OptionRegionIssue(OptionRegionIssueCode.MarkerMalformed,
                            "选项标记不得同时指定 /A 与 /Dest，也不得隐藏注解或禁用屏幕显示",
                            question.QuestionId, expectedOption?.OptionId, pageIndex));
                        continue;
                    }

                    if (expectedOption is null)
                    {
                        issues.Add(new OptionRegionIssue(
                            OptionRegionIssueCode.MarkerUnexpected,
                            $"PDF 中存在清单未声明的标记 {name}", question.QuestionId, null, pageIndex));
                        continue;
                    }

                    if (annotation.Action is not GoToAction { Destination: { } destination })
                    {
                        issues.Add(Unresolved(name, question.QuestionId, expectedOption.OptionId, pageIndex, "标记不是同一文档内的 GoTo 目标"));
                        continue;
                    }

                    if (destination.PageNumber < 1 || destination.PageNumber > document.NumberOfPages)
                    {
                        issues.Add(new OptionRegionIssue(
                            OptionRegionIssueCode.PageIndexInvalid,
                            $"标记 {name} 的目标页 {destination.PageNumber} 超出该题 PDF 的 {document.NumberOfPages} 页",
                            question.QuestionId, expectedOption.OptionId, pageIndex));
                        continue;
                    }

                    if (destination.PageNumber != pageNumber)
                    {
                        issues.Add(new OptionRegionIssue(
                            OptionRegionIssueCode.DestinationPageMismatch,
                            $"标记 {name} 的目标在第 {destination.PageNumber} 页，注解在第 {pageNumber} 页",
                            question.QuestionId, expectedOption.OptionId, pageIndex));
                        continue;
                    }

                    if (!TryReadRawRect(annotation, document, out var rawRect))
                    {
                        issues.Add(new OptionRegionIssue(OptionRegionIssueCode.RegionOutOfBounds,
                            "注解缺少合法的 /Rect（四个有限数）", question.QuestionId, expectedOption.OptionId, pageIndex));
                        continue;
                    }

                    // 每条碎片先独立校验，防止零面积或无效碎片被与合法矩形取并集后掩盖。
                    var fragmentResult = OptionRegionGeometry.Normalize(rawRect, displayBox, rotation);
                    if (!fragmentResult.Succeeded)
                    {
                        issues.Add(new OptionRegionIssue(fragmentResult.Issue, fragmentResult.Message,
                            question.QuestionId, expectedOption.OptionId, pageIndex));
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

                // 链接目标仍是唯一绑定依据。A. / （A）的链接框包含标点与字体留白，
                // 其中心不是 A 的可见字形中心；只在已验证的标记内部精修显示区域。
                var (anchor, label, coverage) = RefineLabelRegion(pages[first.PageIndex], region, first.DisplayBox, first.Rotation);
                regions[item.OptionId] = new OptionRegion(
                    item.OptionId, first.PageIndex, anchor.X, anchor.Y, anchor.Width, anchor.Height,
                    label is null ? null : new OptionLabelOverlay(label, coverage.X, coverage.Y, coverage.Width, coverage.Height));
            }
        }

        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // PDF 的页面、动作和注解是延迟读取的，不能只保护 Open。
            // 不泄露第三方异常原文；任一页损坏时整题、整次产物均不可用。
            issues.Add(new OptionRegionIssue(OptionRegionIssueCode.PdfUnreadable,
                "题目 PDF 无法读取，或页面、注解结构无效", question.QuestionId, null, currentPageIndex));
            return [];
        }

        // 结果按清单选项顺序输出；有问题的选项此前已记入 issues，调用方据此整次失败。
        return expected
            .Select(item => regions.TryGetValue(item.OptionId, out var region) ? region : null)
            .OfType<OptionRegion>()
            .ToList();
    }

    private static (NormalizedRegion Anchor, string? Label, NormalizedRegion Coverage) RefineLabelRegion(
        Page page, NormalizedRegion marker, PageDisplayBox displayBox, int rotation)
    {
        // PdfPig 的字形框已应用其页面初始矩阵。先逆变换回原始 PDF 坐标，
        // 再按本模块解析的有效框归一化；库的 CropBox 可能遗漏页树继承，不能混用两种页框。
        var crop = page.CropBox.Bounds;
        NormalizedRegion? glyphs = null;
        NormalizedRegion? coverage = null;
        var label = new System.Text.StringBuilder();
        foreach (var letter in page.Letters)
        {
            if (string.IsNullOrWhiteSpace(letter.Value) ||
                letter.RenderingMode is TextRenderingMode.Neither or TextRenderingMode.NeitherClip)
                continue;

            var box = letter.BoundingBox;
            var corners = new[] { box.BottomLeft, box.BottomRight, box.TopLeft, box.TopRight }
                .Select(point => UndoPageTransform(point, crop, rotation)).ToArray();
            var raw = new RawRect(corners.Min(point => point.X), corners.Min(point => point.Y),
                corners.Max(point => point.X), corners.Max(point => point.Y));
            if (OptionRegionGeometry.Normalize(raw, displayBox, rotation).Region is not { } glyph)
                continue;
            var left = glyph.X;
            var right = glyph.X + glyph.Width;
            var top = glyph.Y;
            var bottom = glyph.Y + glyph.Height;
            // 必须完整位于此标记内，不把旁边的选项正文或其它标记并进来。
            if (!double.IsFinite(left) || !double.IsFinite(right) || !double.IsFinite(top) || !double.IsFinite(bottom) ||
                right <= left || bottom <= top || left < marker.X || right > marker.X + marker.Width ||
                top < marker.Y || bottom > marker.Y + marker.Height)
                continue;

            // 覆盖范围含可见标点，但不含链接留白；避免小字号时圆圈被字体空白撑大。
            coverage = coverage is { } covered ? Union(covered, glyph) : glyph;
            if (!letter.Value.All(char.IsLetterOrDigit)) continue;

            if (glyphs is { } previous)
            {
                left = Math.Min(left, previous.X);
                right = Math.Max(right, previous.X + previous.Width);
                top = Math.Min(top, previous.Y);
                bottom = Math.Max(bottom, previous.Y + previous.Height);
            }
            glyphs = new NormalizedRegion(left, top, right - left, bottom - top);
            label.Append(letter.Value);
        }

        // 图片、路径绘制或不可读标签保留已有链接几何，不猜字母、不改变标识绑定。
        return (glyphs ?? marker, glyphs is null ? null : label.ToString(), coverage ?? marker);
    }

    private static NormalizedRegion Union(NormalizedRegion first, NormalizedRegion second)
    {
        var left = Math.Min(first.X, second.X);
        var top = Math.Min(first.Y, second.Y);
        return new NormalizedRegion(left, top,
            Math.Max(first.X + first.Width, second.X + second.Width) - left,
            Math.Max(first.Y + first.Height, second.Y + second.Height) - top);
    }

    private static PdfPoint UndoPageTransform(PdfPoint point, PdfRectangle crop, int rotation) => rotation switch
    {
        90 => new PdfPoint(crop.Left + crop.Width - point.Y, crop.Bottom + point.X),
        180 => new PdfPoint(crop.Right - point.X, crop.Top - point.Y),
        270 => new PdfPoint(crop.Left + point.Y, crop.Bottom + crop.Height - point.X),
        _ => new PdfPoint(crop.Left + point.X, crop.Bottom + point.Y)
    };

    private static OptionRegionIssue Unresolved(string name, string questionId, string optionId, int pageIndex, string reason) =>
        new(OptionRegionIssueCode.MarkerUnresolved, $"标记 {name} 无法解析：{reason}", questionId, optionId, pageIndex);

    /// <summary>解析原始命名目标；与 PdfPig 一样接受字符串、十六进制字符串、名称及间接对象。</summary>
    private static (string? Name, bool Ambiguous) ReadMarkerDestination(Annotation annotation, PdfDocument document)
    {
        var dictionary = annotation.AnnotationDictionary;
        var hasAction = dictionary.TryGet(NameToken.Create("A"), out var action);
        var hasDestination = dictionary.TryGet(NameToken.Create("Dest"), out var directDestination);
        string? fromAction = null;
        if (Resolve(action, document) is DictionaryToken actionDictionary &&
            actionDictionary.TryGet(NameToken.Create("D"), out var actionDestination))
            fromAction = TokenText(Resolve(actionDestination, document));
        var fromDestination = TokenText(Resolve(directDestination, document));
        var name = IsMarker(fromAction) ? fromAction : IsMarker(fromDestination) ? fromDestination : null;
        return (name, hasAction && hasDestination);
    }

    private static bool IsMarker(string? name) =>
        name?.StartsWith(OptionRegionArtifact.MarkerPrefix, StringComparison.Ordinal) == true;

    private static string? TokenText(IToken? token) => token switch
    {
        StringToken text => text.Data,
        HexToken hex => hex.Data,
        NameToken name => name.Data,
        _ => null
    };

    /// <summary>有界地解引用原始对象；不使用已经变换过的 Annotation.Rectangle。</summary>
    private static IToken? Resolve(IToken? token, PdfDocument document)
    {
        for (var depth = 0; token is IndirectReferenceToken reference; depth++)
        {
            if (depth >= 64) throw new InvalidDataException("PDF 间接引用过深或成环");
            token = document.Structure.GetObject(reference.Data).Data;
        }
        return token;
    }

    private static bool TryReadRawRect(Annotation annotation, PdfDocument document, out RawRect rect)
    {
        annotation.AnnotationDictionary.TryGet(NameToken.Create("Rect"), out var token);
        return TryReadRect(token, document, out rect);
    }

    private static bool TryReadRect(IToken? token, PdfDocument document, out RawRect rect)
    {
        rect = default;
        if (Resolve(token, document) is not ArrayToken { Length: 4 } array) return false;
        Span<double> values = stackalloc double[4];
        for (var index = 0; index < 4; index++)
        {
            if (Resolve(array[index], document) is not NumericToken number || !double.IsFinite(number.Double))
                return false;
            values[index] = number.Double;
        }
        rect = RawRect.FromNumbers(values);
        return true;
    }

    /// <summary>按最近页树节点继承页框；PdfPig 0.1.16 的 Page.CropBox 不能代替此查询。</summary>
    private static IToken? ReadInherited(DictionaryToken dictionary, NameToken key, PdfDocument document)
    {
        for (var depth = 0; depth < 128; depth++)
        {
            if (dictionary.TryGet(key, out var value) && Resolve(value, document) is { } resolved && resolved is not NullToken)
                return resolved;
            if (!dictionary.TryGet(NameToken.Create("Parent"), out var parent)) return null;
            dictionary = Resolve(parent, document) as DictionaryToken
                ?? throw new InvalidDataException("PDF 页树父节点无效");
        }
        throw new InvalidDataException("PDF 页树过深或成环");
    }

    /// <summary>显式读取继承的原始页框，再取 CropBox 与 MediaBox 的交集。</summary>
    private static bool TryEffectiveDisplayBox(Page page, PdfDocument document, out PageDisplayBox displayBox)
    {
        displayBox = default;
        if (!TryReadRect(ReadInherited(page.Dictionary, NameToken.Create("MediaBox"), document), document, out var media) ||
            !(media.Width > 0) || !(media.Height > 0)) return false;
        var cropToken = ReadInherited(page.Dictionary, NameToken.Create("CropBox"), document);
        var crop = media;
        if (cropToken is not null && (!TryReadRect(cropToken, document, out crop) || !(crop.Width > 0) || !(crop.Height > 0)))
            return false;

        var left = Math.Max(crop.MinX, media.MinX);
        var bottom = Math.Max(crop.MinY, media.MinY);
        var right = Math.Min(crop.MaxX, media.MaxX);
        var top = Math.Min(crop.MaxY, media.MaxY);
        displayBox = new PageDisplayBox(left, bottom, right - left, top - bottom);
        return double.IsFinite(displayBox.Width) && double.IsFinite(displayBox.Height) &&
               displayBox.Width > 0 && displayBox.Height > 0;
    }

    private sealed record ExpectedOption(OptionRegionSourceQuestion Question, string OptionId, string Name);

    private readonly record struct Fragment(int PageIndex, PageDisplayBox DisplayBox, int Rotation, RawRect RawRect);
}
