using System.IO.Compression;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

public sealed partial class OptionRegionMapTests
{
    [Theory]
    [InlineData("pageIndex")]
    [InlineData("x")]
    [InlineData("y")]
    public void B03Review_MissingZeroCapableFieldsCannotSilentlyDefault(string field)
    {
        var root = JsonNode.Parse(ValidJson)!;
        root["questions"]![0]!["options"]![0]!.AsObject().Remove(field);
        Assert.Throws<InvalidDataException>(() =>
            OptionRegionMap.Parse(Encoding.UTF8.GetBytes(root.ToJsonString()), CreateManifest()));
    }

    [Fact]
    public void B03Review_ValidatedCoordinatesCannotBeMutatedThroughTheReadOnlyInterface()
    {
        var map = OptionRegionMap.Parse(Encoding.UTF8.GetBytes(ValidJson), CreateManifest());
        var regions = map.ForQuestion("q1")!;
        var original = regions["A"];
        if (regions is IDictionary<string, OptionRegion> writable)
            Assert.Throws<NotSupportedException>(() => writable["A"] = new(99, 0, 0, 1, 1));
        Assert.Equal(original, regions["A"]);
    }
    [Fact]
    public void B03Review_DuplicateZipCoordinateArtifactsFailInsteadOfChoosingTheFirst()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < 2; index++)
            {
                using var entry = zip.CreateEntry(OptionRegionMap.EntryPath).Open();
                entry.Write(Encoding.UTF8.GetBytes(ValidJson));
            }
        }
        Assert.Throws<InvalidDataException>(() => OptionRegionMap.TryReadFromPackage(stream.ToArray(), CreateManifest()));
    }

    [Theory]
    [InlineData(-1, 0, 0, .1, .1)]
    [InlineData(0, -.0000000001, 0, .1, .1)]
    [InlineData(0, 0, 0, double.NaN, .1)]
    [InlineData(0, 0, 0, .1, double.PositiveInfinity)]
    public void B03Review_RenderBoundaryAlsoRejectsUnvalidatedRegions(int page, double x, double y, double width, double height)
    {
        var regions = new Dictionary<string, OptionRegion> { ["A"] = new(page, x, y, width, height) };
        Assert.NotNull(OptionRegionMap.ValidatePageCount(regions, 1, "synthetic-q"));
    }

}
