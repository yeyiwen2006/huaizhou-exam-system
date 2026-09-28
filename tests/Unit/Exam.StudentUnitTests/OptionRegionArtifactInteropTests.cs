using System.Text;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Contracts.Versioning;
using Exam.Infrastructure.Compilation;
using Exam.Student.Core;
using ArtifactOption = Exam.Contracts.Importing.OptionRegion;

namespace Exam.StudentUnitTests;

public sealed class OptionRegionArtifactInteropTests
{
    [Fact]
    public void D03SerializedArtifact_IsReadByB03ByIdentifier_NotArrayPosition()
    {
        var artifact = Artifact();
        var json = OptionRegionArtifactJson.Serialize(artifact);

        var map = OptionRegionMap.Parse(Encoding.UTF8.GetBytes(json), Manifest());

        Assert.Equal(artifact.ExamId, map.ExamId);
        Assert.Equal(artifact.PaperVersion, map.PaperVersion);
        var first = map.ForQuestion("题目:一")!;
        Assert.Equal(0, first["A:甲"].PageIndex);
        Assert.Equal(0.000249, first["A:甲"].X);
        Assert.Equal(1, first["B"].PageIndex);
        Assert.Equal(0.0000004, first["B"].Width);
        Assert.Equal(0.3, map.ForQuestion("q-2")!["only"].Y);
        Assert.Equal(json, OptionRegionArtifactJson.Serialize(OptionRegionArtifactJson.Deserialize(json).Artifact!));
    }

    [Theory]
    [InlineData("exam")]
    [InlineData("version")]
    [InlineData("option")]
    public void B03RejectsSerializedArtifact_WhenItDoesNotMatchTheManifest(string mismatch)
    {
        var artifact = Artifact();
        artifact = mismatch switch
        {
            "exam" => artifact with { ExamId = "different-synthetic-exam" },
            "version" => artifact with { PaperVersion = 4 },
            "option" => artifact with
            {
                Questions = [artifact.Questions[0], artifact.Questions[1] with
                {
                    Options = [new ArtifactOption("unknown", 0, 0.1, 0.1, 0.1, 0.1)]
                }]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        var json = Encoding.UTF8.GetBytes(OptionRegionArtifactJson.Serialize(artifact));

        Assert.Throws<System.IO.InvalidDataException>(() => OptionRegionMap.Parse(json, Manifest()));
    }

    private static OptionRegionArtifact Artifact() => new(1, OptionRegionArtifact.AnchorSchemeName,
        "合成互操作考试", 3,
        [
            new OptionRegionQuestion("q-2", [new ArtifactOption("only", 0, 0.2, 0.3, 0.1, 0.1)]),
            new OptionRegionQuestion("题目:一",
            [
                new ArtifactOption("B", 1, 0.9999996, 0.2, 0.0000004, 0.1),
                new ArtifactOption("A:甲", 0, 0.0002499, 0.1, 0.1, 0.1)
            ])
        ]);

    private static StudentPaperManifest Manifest() => new(1, "合成互操作考试", "合成互操作考试", 3,
        [new StudentPaperSubject("s", "合成科目")],
        [new StudentPaperGroup("g", "s", "合成题组", 1)],
        [
            new StudentPaperQuestion(new PaperQuestionStructure("题目:一", 1, "s", "g", "single", ["A:甲", "B"]),
                "questions/q-1.pdf", null),
            new StudentPaperQuestion(new PaperQuestionStructure("q-2", 2, "s", "g", "single", ["only"]),
                "questions/q-2.pdf", null)
        ], []);
}
