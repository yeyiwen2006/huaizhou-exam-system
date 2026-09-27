using System.IO;
using System.IO.Compression;
using System.Text;
using Exam.Contracts.Resources;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

/// <summary>
/// B03 选项坐标（D03 契约 4.4 option-regions.json）学生侧读取与严格校验。
/// 任何缺失、多余、越界或版本不符均整包失败；文件缺失（v1 包）返回 null 不猜测。
/// </summary>
public sealed partial class OptionRegionMapTests
{
    private static StudentPaperManifest CreateManifest() => new(
        1, "exam", "合成考试", 3,
        [new("math", "数学")],
        [new("g1", "math", "一、单项选择题", 1)],
        [
            new(new("q1", 1, "math", "g1", "single", ["A", "B"], AllowAbstain: false), "questions/合成考试_1.pdf", null),
            new(new("q2", 2, "math", "g1", "multiple", ["A", "B", "C"], AllowAbstain: true), "questions/合成考试_2.pdf", null),
        ],
        []);

    private const string ValidJson = """
        {
          "artifactVersion": 1,
          "anchorScheme": "pageTopLeftNormalizedV1",
          "examId": "exam",
          "paperVersion": 3,
          "questions": [
            { "questionId": "q1", "options": [
              { "optionId": "A", "pageIndex": 0, "x": 0.2239, "y": 0.19, "width": 0.0197, "height": 0.0212 },
              { "optionId": "B", "pageIndex": 0, "x": 0.2239, "y": 0.22, "width": 0.0197, "height": 0.0212 } ] },
            { "questionId": "q2", "options": [
              { "optionId": "A", "pageIndex": 0, "x": 0.2, "y": 0.3, "width": 0.02, "height": 0.02 },
              { "optionId": "B", "pageIndex": 1, "x": 0.2, "y": 0.4, "width": 0.02, "height": 0.02 },
              { "optionId": "C", "pageIndex": 1, "x": 0.2, "y": 0.5, "width": 0.02, "height": 0.02 } ] } ]
        }
        """;

    [Fact]
    public void ValidRegionFileBindsEveryManifestQuestionAndOption()
    {
        var map = OptionRegionMap.Parse(Encoding.UTF8.GetBytes(ValidJson), CreateManifest());

        Assert.Equal("exam", map.ExamId);
        Assert.Equal(3, map.PaperVersion);
        var q1 = map.ForQuestion("q1");
        Assert.NotNull(q1);
        Assert.Equal(2, q1.Count);
        Assert.Equal(new OptionRegion(0, 0.2239, 0.19, 0.0197, 0.0212), q1["A"]);
        var q2 = map.ForQuestion("q2");
        Assert.NotNull(q2);
        Assert.Equal(3, q2.Count);
        // 多页题目：pageIndex 相对逐题 PDF 从 0 起，跨页保留（契约 4.3）。
        Assert.Equal(0, q2["A"].PageIndex);
        Assert.Equal(1, q2["B"].PageIndex);
        Assert.Equal(1, q2["C"].PageIndex);
        Assert.Null(map.ForQuestion("unknown"));
    }

    [Theory]
    [InlineData("\"optionId\": \"A\", \"pageIndex\": 0, \"x\": 0.9, \"y\": 0.1, \"width\": 0.2, \"height\": 0.02")]
    [InlineData("\"optionId\": \"A\", \"pageIndex\": 0, \"x\": -0.01, \"y\": 0.1, \"width\": 0.02, \"height\": 0.02")]
    [InlineData("\"optionId\": \"A\", \"pageIndex\": 0, \"x\": 0.1, \"y\": 0.1, \"width\": 0, \"height\": 0.02")]
    [InlineData("\"optionId\": \"A\", \"pageIndex\": -1, \"x\": 0.1, \"y\": 0.1, \"width\": 0.02, \"height\": 0.02")]
    [InlineData("\"optionId\": \"A\", \"pageIndex\": 0, \"x\": 1e999, \"y\": 0.1, \"width\": 0.02, \"height\": 0.02")]
    [InlineData("\"optionId\": \"A\", \"pageIndex\": 0, \"x\": 0.1, \"y\": 0.9, \"width\": 0.02, \"height\": 0.3}")]
    public void InvalidRegionValuesFailTheWholePackage(string optionA)
    {
        var json = RegionJsonWithQ1OptionA(optionA);
        Assert.Throws<InvalidDataException>(() =>
            OptionRegionMap.Parse(Encoding.UTF8.GetBytes(json), CreateManifest()));
    }

    private static string RegionJsonWithQ1OptionA(string optionA) => $$"""
        {
          "artifactVersion": 1,
          "anchorScheme": "pageTopLeftNormalizedV1",
          "examId": "exam",
          "paperVersion": 3,
          "questions": [
            { "questionId": "q1", "options": [
              { {{optionA}} },
              { "optionId": "B", "pageIndex": 0, "x": 0.2239, "y": 0.22, "width": 0.0197, "height": 0.0212 } ] },
            { "questionId": "q2", "options": [
              { "optionId": "A", "pageIndex": 0, "x": 0.2, "y": 0.3, "width": 0.02, "height": 0.02 },
              { "optionId": "B", "pageIndex": 1, "x": 0.2, "y": 0.4, "width": 0.02, "height": 0.02 },
              { "optionId": "C", "pageIndex": 1, "x": 0.2, "y": 0.5, "width": 0.02, "height": 0.02 } ] } ]
        }
        """;

    [Fact]
    public void MissingOptionOrExtraOptionFails()
    {
        var manifest = CreateManifest();

        // q1 缺 B 选项坐标：清单选项必须恰好各出现一次。
        var missing = RegionJsonWithQ1Options(
            "{ \"optionId\": \"A\", \"pageIndex\": 0, \"x\": 0.2239, \"y\": 0.19, \"width\": 0.0197, \"height\": 0.0212 }");
        Assert.Throws<InvalidDataException>(() =>
            OptionRegionMap.Parse(Encoding.UTF8.GetBytes(missing), manifest));

        // q1 多出清单外选项 Z。
        var extra = RegionJsonWithQ1Options(
            "{ \"optionId\": \"A\", \"pageIndex\": 0, \"x\": 0.2239, \"y\": 0.19, \"width\": 0.0197, \"height\": 0.0212 }, " +
            "{ \"optionId\": \"B\", \"pageIndex\": 0, \"x\": 0.2239, \"y\": 0.22, \"width\": 0.0197, \"height\": 0.0212 }, " +
            "{ \"optionId\": \"Z\", \"pageIndex\": 0, \"x\": 0.3, \"y\": 0.3, \"width\": 0.02, \"height\": 0.02 }");
        Assert.Throws<InvalidDataException>(() =>
            OptionRegionMap.Parse(Encoding.UTF8.GetBytes(extra), manifest));
    }

    private static string RegionJsonWithQ1Options(string q1Options) => $$"""
        {
          "artifactVersion": 1,
          "anchorScheme": "pageTopLeftNormalizedV1",
          "examId": "exam",
          "paperVersion": 3,
          "questions": [
            { "questionId": "q1", "options": [ {{q1Options}} ] },
            { "questionId": "q2", "options": [
              { "optionId": "A", "pageIndex": 0, "x": 0.2, "y": 0.3, "width": 0.02, "height": 0.02 },
              { "optionId": "B", "pageIndex": 1, "x": 0.2, "y": 0.4, "width": 0.02, "height": 0.02 },
              { "optionId": "C", "pageIndex": 1, "x": 0.2, "y": 0.5, "width": 0.02, "height": 0.02 } ] } ]
        }
        """;

    [Fact]
    public void ExtraQuestionOrDuplicateEntriesFail()
    {
        var manifest = CreateManifest();
        var extraQuestion = ValidJson.TrimEnd(']', ' ', '\n', '\r') + @",
            { ""questionId"": ""q3"", ""options"": [
              { ""optionId"": ""A"", ""pageIndex"": 0, ""x"": 0.1, ""y"": 0.1, ""width"": 0.02, ""height"": 0.02 } ] } ] }";
        Assert.Throws<InvalidDataException>(() =>
            OptionRegionMap.Parse(Encoding.UTF8.GetBytes(extraQuestion), manifest));

        var duplicate = ValidJson.Replace(
            "{ \"optionId\": \"B\", \"pageIndex\": 0, \"x\": 0.2239, \"y\": 0.22, \"width\": 0.0197, \"height\": 0.0212 }",
            "{ \"optionId\": \"B\", \"pageIndex\": 0, \"x\": 0.2239, \"y\": 0.22, \"width\": 0.0197, \"height\": 0.0212 }, " +
            "{ \"optionId\": \"B\", \"pageIndex\": 0, \"x\": 0.3, \"y\": 0.3, \"width\": 0.02, \"height\": 0.02 }");
        Assert.Throws<InvalidDataException>(() =>
            OptionRegionMap.Parse(Encoding.UTF8.GetBytes(duplicate), manifest));
    }

    [Theory]
    [InlineData("exam-x", 3)]
    [InlineData("exam", 2)]
    public void ExamOrVersionMismatchFails(string examId, long paperVersion)
    {
        var json = ValidJson
            .Replace("\"examId\": \"exam\"", $"\"examId\": \"{examId}\"")
            .Replace("\"paperVersion\": 3", $"\"paperVersion\": {paperVersion}");
        Assert.Throws<InvalidDataException>(() =>
            OptionRegionMap.Parse(Encoding.UTF8.GetBytes(json), CreateManifest()));
    }

    [Fact]
    public void UnsupportedArtifactVersionFails()
    {
        var json = ValidJson.Replace("\"artifactVersion\": 1", "\"artifactVersion\": 2");
        Assert.Throws<InvalidDataException>(() =>
            OptionRegionMap.Parse(Encoding.UTF8.GetBytes(json), CreateManifest()));
    }

    [Fact]
    public void UnsupportedAnchorSchemeFails()
    {
        var json = ValidJson.Replace("pageTopLeftNormalizedV1", "bottomLeftNormalizedV1");
        Assert.Throws<InvalidDataException>(() =>
            OptionRegionMap.Parse(Encoding.UTF8.GetBytes(json), CreateManifest()));
    }

    [Fact]
    public void DuplicateJsonKeysFail()
    {
        var json = ValidJson.Replace(
            "\"examId\": \"exam\",",
            "\"examId\": \"exam\", \"examId\": \"exam\",");
        Assert.Throws<InvalidDataException>(() =>
            OptionRegionMap.Parse(Encoding.UTF8.GetBytes(json), CreateManifest()));
    }

    [Fact]
    public void InvalidUtf8OrJsonFails()
    {
        Assert.Throws<InvalidDataException>(() =>
            OptionRegionMap.Parse("{ not json"u8.ToArray(), CreateManifest()));
        Assert.Throws<InvalidDataException>(() =>
            OptionRegionMap.Parse(new byte[] { 255, 254, 255 }, CreateManifest()));
    }

    [Fact]
    public void PackageWithoutRegionEntryReturnsNullInsteadOfGuessing()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry("questions/合成考试_1.pdf").Open())
        {
            entry.Write("%PDF-1.4"u8.ToArray());
        }
        var payload = stream.ToArray();

        Assert.Null(OptionRegionMap.TryReadFromPackage(payload, CreateManifest()));
    }

    [Fact]
    public void PackageWithRegionEntryReadsAndValidatesIt()
    {
        var payload = ZipWithRegions(Encoding.UTF8.GetBytes(ValidJson));
        var map = OptionRegionMap.TryReadFromPackage(payload, CreateManifest());
        Assert.NotNull(map);
        Assert.NotNull(map.ForQuestion("q1"));

        var broken = Encoding.UTF8.GetBytes(ValidJson.Replace("\"paperVersion\": 3", "\"paperVersion\": 9"));
        Assert.Throws<InvalidDataException>(() =>
            OptionRegionMap.TryReadFromPackage(ZipWithRegions(broken), CreateManifest()));
    }

    [Fact]
    public void PageCountValidationReportsTheFirstViolation()
    {
        var map = OptionRegionMap.Parse(Encoding.UTF8.GetBytes(ValidJson), CreateManifest());
        var q2 = map.ForQuestion("q2");
        Assert.Null(OptionRegionMap.ValidatePageCount(q2, pageCount: 2, "q2"));
        var error = OptionRegionMap.ValidatePageCount(q2, pageCount: 1, "q2");
        Assert.NotNull(error);
        Assert.Contains("q2", error);
        Assert.Contains("B", error);
    }

    private static byte[] ZipWithRegions(byte[] regions)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry(OptionRegionMap.EntryPath).Open())
        {
            entry.Write(regions);
        }
        return stream.ToArray();
    }
}
