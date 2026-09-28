using System.Text.Json;
using Exam.Contracts.Importing;
using Exam.Infrastructure.Compilation;

namespace Exam.UnitTests.Importing;

public sealed class OptionRegionSerializationBoundaryTests
{
    [Theory]
    [InlineData(0.000249)]
    [InlineData(0.000251)]
    [InlineData(0.000489)]
    [InlineData(0.123456)]
    public void SixDecimalCoordinates_AreNotTruncatedAgainByBinaryRounding(double coordinate)
    {
        var artifact = WithOption(Option() with { X = coordinate });
        var result = OptionRegionArtifactJson.Deserialize(OptionRegionArtifactJson.Serialize(artifact));

        Assert.True(result.Succeeded);
        Assert.Equal(coordinate, result.Artifact!.Questions[0].Options[0].X);
    }

    [Theory]
    [InlineData(0.0002499)]
    [InlineData(0.0002519)]
    [InlineData(0.0004899)]
    public void CanonicalSerialization_IsIdempotent(double coordinate)
    {
        var first = OptionRegionArtifactJson.Serialize(WithOption(Option() with { X = coordinate }));
        var result = OptionRegionArtifactJson.Deserialize(first);

        Assert.True(result.Succeeded);
        Assert.Equal(first, OptionRegionArtifactJson.Serialize(result.Artifact!));
    }

    [Theory]
    [InlineData("artifact-version")]
    [InlineData("scheme")]
    [InlineData("exam")]
    [InlineData("paper-version")]
    [InlineData("empty-questions")]
    [InlineData("null-questions")]
    [InlineData("null-question")]
    [InlineData("question-id")]
    [InlineData("duplicate-question")]
    [InlineData("empty-options")]
    [InlineData("null-options")]
    [InlineData("null-option")]
    [InlineData("option-id")]
    [InlineData("duplicate-option")]
    [InlineData("negative-page")]
    [InlineData("out-of-bounds-before-rounding")]
    [InlineData("negative-position")]
    [InlineData("zero-width")]
    [InlineData("nan")]
    [InlineData("infinity")]
    public void InvalidArtifacts_AreRejectedBeforeQuantizationOrWriting(string defect)
    {
        var artifact = Artifact();
        var question = artifact.Questions[0];
        var invalid = defect switch
        {
            "artifact-version" => artifact with { ArtifactVersion = 2 },
            "scheme" => artifact with { AnchorScheme = "unknown" },
            "exam" => artifact with { ExamId = " \t" },
            "paper-version" => artifact with { PaperVersion = 0 },
            "empty-questions" => artifact with { Questions = [] },
            "null-questions" => artifact with { Questions = null! },
            "null-question" => artifact with { Questions = [null!] },
            "question-id" => artifact with { Questions = [question with { QuestionId = " " }] },
            "duplicate-question" => artifact with { Questions = [question, question] },
            "empty-options" => artifact with { Questions = [question with { Options = [] }] },
            "null-options" => artifact with { Questions = [question with { Options = null! }] },
            "null-option" => artifact with { Questions = [question with { Options = [null!] }] },
            "option-id" => WithOption(Option() with { OptionId = " " }),
            "duplicate-option" => artifact with { Questions = [question with { Options = [Option(), Option()] }] },
            "negative-page" => WithOption(Option() with { PageIndex = -1 }),
            "out-of-bounds-before-rounding" => WithOption(Option() with { X = 0.999999999, Width = 0.0000004 }),
            "negative-position" => WithOption(Option() with { X = -0.1 }),
            "zero-width" => WithOption(Option() with { Width = 0 }),
            "nan" => WithOption(Option() with { X = double.NaN }),
            "infinity" => WithOption(Option() with { Width = double.PositiveInfinity }),
            _ => throw new ArgumentOutOfRangeException(nameof(defect))
        };

        Assert.ThrowsAny<ArgumentException>(() => OptionRegionArtifactJson.Serialize(invalid));
    }

    [Theory]
    [InlineData("\\uD800")]
    [InlineData("\\uDC00")]
    public void InvalidEscapedUnicode_ReturnsFailureInsteadOfThrowing(string invalidText)
    {
        var json = JsonSerializer.Serialize(Artifact(), ImportJson.Create())
            .Replace("synthetic-exam", invalidText, StringComparison.Ordinal);

        var result = OptionRegionArtifactJson.Deserialize(json);

        Assert.False(result.Succeeded);
        Assert.Null(result.Artifact);
    }

    private static OptionRegion Option() => new("A", 0, 0.1, 0.1, 0.1, 0.1);
    private static OptionRegionArtifact WithOption(OptionRegion option) => Artifact() with
    {
        Questions = [new OptionRegionQuestion("q-1", [option])]
    };
    private static OptionRegionArtifact Artifact() => new(
        1, OptionRegionArtifact.AnchorSchemeName, "synthetic-exam", 1,
        [new OptionRegionQuestion("q-1", [Option()])]);
}
