using System.Globalization;
using System.Text;
using Exam.Contracts.Importing;
using Exam.Domain.Importing;
using Exam.Infrastructure.Compilation;

namespace Exam.UnitTests.Importing;

/// <summary>
/// PR #64 审查回归。最小 PDF 全部在临时目录由合成对象生成，不依赖 TeX、字体或真实材料，普通 CI 必须执行。
/// </summary>
public sealed class OptionRegionReviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "exam-option-review-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Merge_DoesNotUseBoundingBoxAsConnectivityEvidence(int offset)
    {
        // L 形连通分量的外接框包含独立矩形，但独立矩形没有接触任一原始碎片。
        RawRect[] rectangles = [new(10, 10, 20, 60), new(10, 50, 60, 60), new(40, 20, 50, 30)];
        var ordered = rectangles.Skip(offset).Concat(rectangles.Take(offset)).ToArray();
        Assert.False(OptionRegionGeometry.TryMergeConnected(ordered, out var union));
        Assert.Equal(default, union);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Normalize_RejectsRealOutOfBoundsEvenInsideFormerTolerance(int rotation)
    {
        var result = OptionRegionGeometry.Normalize(new RawRect(20, 20, 100.00000001, 60), new(0, 0, 100, 100), rotation);
        Assert.False(result.Succeeded);
        Assert.Equal(OptionRegionIssueCode.RegionOutOfBounds, result.Issue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Normalize_ExactBoundaryRemainsStrictlyInsideUnitSquare(int rotation)
    {
        var result = OptionRegionGeometry.Normalize(new RawRect(0.2, 0.3, 1, 1), new(0, 0, 1, 1), rotation);
        Assert.True(result.Succeeded, result.Message);
        var region = result.Region!.Value;
        Assert.InRange(region.X, 0, 1);
        Assert.InRange(region.Y, 0, 1);
        Assert.InRange(region.X + region.Width, 0, 1);
        Assert.InRange(region.Y + region.Height, 0, 1);
    }

    [Theory]
    [InlineData("/A << /S /GoTo /D (opt:q-001:A) >>")]
    [InlineData("/Dest (opt:q-001:A)")]
    [InlineData("/Dest /opt:q-001:A")]
    [InlineData("/A << /S /GoTo /D <6F70743A712D3030313A41> >>")]
    [InlineData("/Dest <6F70743A712D3030313A41>")]
    public void Extract_ReadsSupportedNamedDestinationRepresentations(string target)
    {
        var result = Extract(CreatePdf([Link(target)]));
        Assert.True(result.Succeeded, Describe(result));
        var option = Assert.Single(Assert.Single(result.Artifact!.Questions).Options);
        Assert.Equal("A", option.OptionId);
        Assert.Equal(0, option.PageIndex);
        Assert.Equal(0.1, option.X, 9);
        Assert.Equal(0.6, option.Y, 9);
        Assert.Equal(0.1, option.Width, 9);
        Assert.Equal(0.2, option.Height, 9);
    }

    [Theory]
    [InlineData("/A 6 0 R", "<< /S /GoTo /D (opt:q-001:A) >>")]
    [InlineData("/A << /S /GoTo /D 6 0 R >>", "(opt:q-001:A)")]
    [InlineData("/Dest 6 0 R", "(opt:q-001:A)")]
    public void Extract_ResolvesIndirectDestinationObjects(string target, string extraObject)
    {
        var result = Extract(CreatePdf([Link(target)], extraObjects: [extraObject]));
        Assert.True(result.Succeeded, Describe(result));
    }

    [Fact]
    public void Extract_ResolvesIndirectRectAndNumbers()
    {
        var pdf = CreatePdf([Link("/Dest (opt:q-001:A)", "6 0 R")],
            extraObjects: ["[7 0 R 20 40 40]", "20"]);
        var result = Extract(pdf);
        Assert.True(result.Succeeded, Describe(result));
    }

    [Fact]
    public void Extract_DoesNotIgnoreUnexpectedHexEncodedMarker()
    {
        var pdf = CreatePdf([
            Link("/Dest (opt:q-001:A)"),
            Link("/Dest <6F70743A712D3030313A42>", "[60 20 80 40]")
        ], names: "(opt:q-001:A) [3 0 R /Fit] (opt:q-001:B) [3 0 R /Fit]");
        var result = Extract(pdf);
        AssertFailed(result, OptionRegionIssueCode.MarkerUnexpected);
    }

    [Fact]
    public void Extract_DoesNotMergeDisconnectedThirdMarkerInsideBoundingBox()
    {
        var pdf = CreatePdf([
            Link("/Dest (opt:q-001:A)", "[10 10 20 60]"),
            Link("/Dest (opt:q-001:A)", "[10 50 60 60]"),
            Link("/Dest (opt:q-001:A)", "[40 20 50 30]")
        ]);
        AssertFailed(Extract(pdf), OptionRegionIssueCode.MarkerDuplicate);
    }

    [Fact]
    public void Extract_DoesNotHideZeroAreaFragmentByUnionWithValidRegion()
    {
        var pdf = CreatePdf([
            Link("/Dest (opt:q-001:A)"),
            Link("/Dest (opt:q-001:A)", "[30 30 30 30]")
        ]);
        AssertFailed(Extract(pdf), OptionRegionIssueCode.RegionOutOfBounds);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(32)]
    public void Extract_RejectsHiddenOrNoViewMarker(int flags)
    {
        var result = Extract(CreatePdf([Link($"/Dest (opt:q-001:A) /F {flags}")]));
        AssertFailed(result, OptionRegionIssueCode.MarkerMalformed);
    }

    [Fact]
    public void Extract_RejectsConflictingActionAndDestination()
    {
        // PdfPig 优先 /Dest，原抽取器优先 /A；两套目标来源不得交叉绑定。
        var result = Extract(CreatePdf([
            Link("/A << /S /GoToR /F (other.pdf) /D (opt:q-001:A) >> /Dest (ordinary)")
        ], names: "(ordinary) [3 0 R /Fit] (opt:q-001:A) [3 0 R /Fit]"));
        AssertFailed(result, OptionRegionIssueCode.MarkerMalformed);
    }

    [Fact]
    public void Extract_MalformedAnnotationReturnsFailureRatherThanThrowing()
    {
        // 打开 PDF 成功，实际枚举注解时第三方库才读取缺失的 Rect。
        var result = Extract(CreatePdf(["<< /Type /Annot /Subtype /Link /Dest (opt:q-001:A) >>"]));
        AssertFailed(result, OptionRegionIssueCode.PdfUnreadable);
        Assert.Contains(result.Issues, issue => issue.QuestionId == "q-001" && issue.PageIndex == 0);
    }

    [Fact]
    public void Extract_UnresolvedMarkerIncludesKnownOptionId()
    {
        var result = Extract(CreatePdf([Link("/Dest (opt:q-001:A)")], names: ""));
        AssertFailed(result, OptionRegionIssueCode.MarkerUnresolved);
        Assert.Contains(result.Issues, issue => issue.Code == OptionRegionIssueCode.MarkerUnresolved
            && issue.QuestionId == "q-001" && issue.OptionId == "A" && issue.PageIndex == 0);
    }

    [Theory]
    [InlineData(0, 0.1, 0.5, 0.2, 0.2)]
    [InlineData(90, 0.3, 0.1, 0.2, 0.2)]
    [InlineData(180, 0.7, 0.3, 0.2, 0.2)]
    [InlineData(270, 0.5, 0.7, 0.2, 0.2)]
    public void Extract_InheritsCropMediaAndRotateFromPageTree(int rotation, double x, double y, double width, double height)
    {
        var pdf = CreatePdf([Link("/Dest (opt:q-001:A)", "[70 130 110 150]")], pageAttributes: "",
            treeAttributes: $"/MediaBox [0 0 300 220] /CropBox [50 100 250 200] /Rotate {rotation}");
        var result = Extract(pdf);
        Assert.True(result.Succeeded, Describe(result));
        var option = result.Artifact!.Questions[0].Options[0];
        Assert.Equal(x, option.X, 9);
        Assert.Equal(y, option.Y, 9);
        Assert.Equal(width, option.Width, 9);
        Assert.Equal(height, option.Height, 9);
    }

    [Fact]
    public void Extract_UsesManifestOrderAndIgnoresOrdinaryLinks()
    {
        var pdf = CreatePdf([
            Link("/Dest (opt:q-001:B)", "[60 20 80 40]"),
            Link("/A << /S /URI /URI (https://example.invalid/) >>"),
            Link("/Dest (opt:q-001:A)")
        ], names: "(opt:q-001:A) [3 0 R /Fit] (opt:q-001:B) [3 0 R /Fit]");
        var result = Extract(pdf, ["A", "B"]);
        Assert.True(result.Succeeded, Describe(result));
        Assert.Equal(new[] { "A", "B" }, result.Artifact!.Questions[0].Options.Select(option => option.OptionId));
    }

    [Fact]
    public void Extract_OneBadQuestionPreventsPartialArtifact()
    {
        var good = CreatePdf([Link("/Dest (opt:q-001:A)")]);
        var result = new OptionRegionExtractor().Extract(new("synthetic", 1,
            [new("q-001", good, ["A"]), new("q-002", Path.Combine(_root, "missing.pdf"), ["A"])]));
        AssertFailed(result, OptionRegionIssueCode.PdfUnreadable);
    }

    [Fact]
    public void Extract_NullCollectionsReturnRequestInvalid()
    {
        var extractor = new OptionRegionExtractor();
        AssertFailed(extractor.Extract(new("synthetic", 1, null!)), OptionRegionIssueCode.RequestInvalid);
        AssertFailed(extractor.Extract(new("synthetic", 1, [null!])), OptionRegionIssueCode.RequestInvalid);
        AssertFailed(extractor.Extract(new("synthetic", 1,
            [new("q-001", Path.Combine(_root, "missing.pdf"), null!)])), OptionRegionIssueCode.RequestInvalid);
    }

    [Theory]
    [InlineData("relative.pdf")]
    [InlineData("C:\\invalid\0.pdf")]
    public void Extract_InvalidPathReturnsRequestInvalid(string path)
    {
        AssertFailed(Extract(path), OptionRegionIssueCode.RequestInvalid);
    }

    [Fact]
    public void Log_OutputLongerThanFormerWindowIsNotTruncated()
    {
        var path = "C:/" + string.Concat(Enumerable.Repeat("synthetic/", 60)) + "sample.pdf";
        var report = XeLaTeXLogReader.Read("Output written on " + path + " (2 pages).");
        Assert.True(report.HasOutput);
        Assert.Equal("sample.pdf", report.OutputFileName);
        Assert.Equal(2, report.PageCount);
    }

    [Fact]
    public void Log_FoldDoesNotStripParenthesizedFilenameContinuation()
    {
        var report = XeLaTeXLogReader.Read("Output written on C:/synthetic/sample\n(draft).pdf (2 pages).");
        Assert.True(report.HasOutput);
        Assert.Equal("sample(draft).pdf", report.OutputFileName);
    }


    [Fact]
    public void Extract_DestinationOnOtherPageIsRejected()
    {
        // 第二页是第 6 个对象；第一页上的链接不得绑定到第二页上的同名目标。
        var pdf = CreatePdf([Link("/Dest (opt:q-001:A)")],
            names: "(opt:q-001:A) [6 0 R /Fit]", secondPageAnnotation: "");
        var result = Extract(pdf);
        AssertFailed(result, OptionRegionIssueCode.DestinationPageMismatch);
        Assert.Contains(result.Issues, issue => issue.Code == OptionRegionIssueCode.DestinationPageMismatch
            && issue.QuestionId == "q-001" && issue.OptionId == "A" && issue.PageIndex == 0);
    }

    [Fact]
    public void Extract_DuplicateOnAnotherPageFailsEntireArtifact()
    {
        var pdf = CreatePdf([Link("/Dest (opt:q-001:A)")], secondPageAnnotation: Link("/Dest (opt:q-001:A)"));
        AssertFailed(Extract(pdf), OptionRegionIssueCode.DestinationPageMismatch);
    }

    [Fact]
    public void Extract_AnnotationNameIsNotAnOptionDestination()
    {
        var pdf = CreatePdf([Link("/NM (opt:q-001:A) /Dest (ordinary)")], names: "(ordinary) [3 0 R /Fit]");
        AssertFailed(Extract(pdf), OptionRegionIssueCode.MarkerMissing);
    }

    [Fact]
    public void Extract_ValidColonIdentifiersUseExactBinding()
    {
        var pdf = CreatePdf([Link("/Dest (opt:q:part:A:one)")], names: "(opt:q:part:A:one) [3 0 R /Fit]");
        var result = new OptionRegionExtractor().Extract(new("synthetic", 1, [new("q:part", pdf, ["A:one"])]));
        Assert.True(result.Succeeded, Describe(result));
        Assert.Equal("A:one", result.Artifact!.Questions[0].Options[0].OptionId);
    }

    [Fact]
    public void Extract_AmbiguousConcatenatedIdentifiersFailBeforeReadingPdfs()
    {
        var result = new OptionRegionExtractor().Extract(new("synthetic", 1, [
            new("q:part", Path.Combine(_root, "one.pdf"), ["A"]),
            new("q", Path.Combine(_root, "two.pdf"), ["part:A"])
        ]));
        AssertFailed(result, OptionRegionIssueCode.ExpectedNameCollision);
        Assert.DoesNotContain(result.Issues, issue => issue.Code == OptionRegionIssueCode.PdfUnreadable);
    }

    [Fact]
    public void Extract_LocalCropOverridesInheritedCrop()
    {
        var pdf = CreatePdf([Link("/Dest (opt:q-001:A)")],
            pageAttributes: "/CropBox [0 0 200 100]",
            treeAttributes: "/MediaBox [0 0 200 100] /CropBox [10 10 100 80]");
        var result = Extract(pdf);
        Assert.True(result.Succeeded, Describe(result));
        Assert.Equal(0.1, result.Artifact!.Questions[0].Options[0].X, 9);
        Assert.Equal(0.6, result.Artifact!.Questions[0].Options[0].Y, 9);
    }

    [Fact]
    public void Extract_CropLargerThanMediaUsesIntersection()
    {
        var pdf = CreatePdf([Link("/Dest (opt:q-001:A)")],
            pageAttributes: "/MediaBox [0 0 200 100] /CropBox [-10 -10 300 200]");
        var result = Extract(pdf);
        Assert.True(result.Succeeded, Describe(result));
        Assert.Equal(0.1, result.Artifact!.Questions[0].Options[0].X, 9);
        Assert.Equal(0.6, result.Artifact!.Questions[0].Options[0].Y, 9);
    }

    [Theory]
    [InlineData("/CropBox [210 110 250 150]")]
    [InlineData("/CropBox [0 0 0 100]")]
    [InlineData("/Rotate 45")]
    public void Extract_InvalidDisplayGeometryFails(string pageAttribute)
    {
        var result = Extract(CreatePdf([Link("/Dest (opt:q-001:A)")],
            pageAttributes: "/MediaBox [0 0 200 100] " + pageAttribute));
        AssertFailed(result, OptionRegionIssueCode.PdfUnreadable);
    }

    [Theory]
    [InlineData("/A << /S /GoToR /F (other.pdf) /D (opt:q-001:A) >>")]
    [InlineData("/A << /S /Named /N /NextPage /D (opt:q-001:A) >>")]
    public void Extract_NonLocalGoToMarkerIsNotAccepted(string target)
    {
        AssertFailed(Extract(CreatePdf([Link(target)])), OptionRegionIssueCode.MarkerUnresolved);
    }

    [Theory]
    [InlineData("Output written on \"C:/synthetic dir/sample question.pdf\" (1 page).")]
    [InlineData("Output written on C:/synthetic dir/sample question.pdf (1 page).")]
    [InlineData("Output written on sample question.pdf (1 page).")]
    public void Log_PreservesSpacesAndQuotedPaths(string log)
    {
        var report = XeLaTeXLogReader.Read(log);
        Assert.True(report.HasOutput);
        Assert.Equal("sample question.pdf", report.OutputFileName);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("999999999999999999999999")]
    public void Log_InvalidPageCountsDoNotThrowOrReportSuccess(string pages)
    {
        var report = XeLaTeXLogReader.Read($"Output written on sample.pdf ({pages} pages).");
        Assert.False(report.HasOutput);
        Assert.False(report.IsClean);
    }

    private static string Link(string target, string rect = "[20 20 40 40]") =>
        $"<< /Type /Annot /Subtype /Link /Rect {rect} /Border [0 0 0] {target} >>";

    private static OptionRegionExtractionResult Extract(string pdfPath, string[]? options = null) =>
        new OptionRegionExtractor().Extract(new("synthetic", 1, [new("q-001", pdfPath, options ?? ["A"])]));

    private static string Describe(OptionRegionExtractionResult result) =>
        string.Join("; ", result.Issues.Select(issue => $"{issue.Code}/{issue.OptionId}: {issue.Message}"));

    private static void AssertFailed(OptionRegionExtractionResult result, OptionRegionIssueCode code)
    {
        Assert.False(result.Succeeded);
        Assert.Null(result.Artifact);
        Assert.Contains(result.Issues, issue => issue.Code == code);
    }

    private string CreatePdf(string[] annotations,
        string names = "(opt:q-001:A) [3 0 R /Fit]",
        string pageAttributes = "/MediaBox [0 0 200 100]",
        string treeAttributes = "",
        string[]? extraObjects = null,
        string? secondPageAnnotation = null)
    {
        // 对象 1..4 为 catalog/pages/page/contents；注解从 5 起，后接测试专用间接对象。
        var references = string.Join(" ", Enumerable.Range(5, annotations.Length).Select(number => $"{number} 0 R"));
        var secondPageReference = 5 + annotations.Length + (extraObjects?.Length ?? 0);
        var kids = secondPageAnnotation is null ? "3 0 R" : $"3 0 R {secondPageReference} 0 R";
        var pageCount = secondPageAnnotation is null ? 1 : 2;
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R /Names << /Dests << /Names [{names}] >> >> >>",
            $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} {treeAttributes} >>",
            $"<< /Type /Page /Parent 2 0 R {pageAttributes} /Resources << >> /Contents 4 0 R /Annots [{references}] >>",
            "<< /Length 0 >>\nstream\n\nendstream"
        };
        objects.AddRange(annotations);
        objects.AddRange(extraObjects ?? []);
        if (secondPageAnnotation is not null)
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R /Annots [{secondPageAnnotation}] >>");
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(text.ToString()));
            text.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(text.ToString());
        text.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            text.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        text.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllText(path, text.ToString(), Encoding.ASCII);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
