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
}
