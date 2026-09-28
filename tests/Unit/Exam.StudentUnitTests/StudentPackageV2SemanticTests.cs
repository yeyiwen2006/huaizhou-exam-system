using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

public sealed class StudentPackageV2SemanticTests
{
    [Theory]
    [InlineData("empty-object")]
    [InlineData("exam")]
    [InlineData("version")]
    [InlineData("artifact-version")]
    [InlineData("scheme")]
    [InlineData("missing-questions")]
    [InlineData("missing-coordinate")]
    [InlineData("duplicate-question")]
    [InlineData("duplicate-option")]
    [InlineData("unknown-question")]
    [InlineData("unknown-option")]
    [InlineData("null-questions")]
    [InlineData("null-question")]
    [InlineData("null-options")]
    [InlineData("null-option")]
    [InlineData("negative-page")]
    [InlineData("out-of-bounds")]
    [InlineData("zero-width")]
    [InlineData("non-finite")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-property")]
    [InlineData("invalid-unicode")]
    public void InvalidV2Coordinates_AreRejectedBeforeReturningTheManifest(string defect)
    {
        var json = JsonSerializer.Serialize(Artifact(), ImportJson.Create());
        var root = JsonNode.Parse(json)!.AsObject();
        var questions = root["questions"]!.AsArray();
        var question = questions[0]!.AsObject();
        var options = question["options"]!.AsArray();
        var option = options[0]!.AsObject();
        switch (defect)
        {
            case "exam": root["examId"] = "other-synthetic-exam"; break;
            case "version": root["paperVersion"] = 2; break;
            case "artifact-version": root["artifactVersion"] = 2; break;
            case "scheme": root["anchorScheme"] = "unknown"; break;
            case "missing-questions": root.Remove("questions"); break;
            case "missing-coordinate": option.Remove("x"); break;
            case "duplicate-question": questions.Add(question.DeepClone()); break;
            case "duplicate-option": options[1] = option.DeepClone(); break;
            case "unknown-question": question["questionId"] = "unknown"; break;
            case "unknown-option": option["optionId"] = "unknown"; break;
            case "null-questions": root["questions"] = null; break;
            case "null-question": questions[0] = null; break;
            case "null-options": question["options"] = null; break;
            case "null-option": options[0] = null; break;
            case "negative-page": option["pageIndex"] = -1; break;
            case "out-of-bounds": option["x"] = 0.99; option["width"] = 0.2; break;
            case "zero-width": option["width"] = 0; break;
            case "unknown-field": option["unexpectedField"] = true; break;
        }
        json = root.ToJsonString();
        json = defect switch
        {
            "empty-object" => "{}",
            "non-finite" => json.Replace("\"x\":0.1", "\"x\":1e999", StringComparison.Ordinal),
            "duplicate-property" => "{\"artifactVersion\":1," + json[1..],
            "invalid-unicode" => json.Replace("\"exam\"", "\"\\uD800\"", StringComparison.Ordinal),
            _ => json
        };

        Assert.Throws<InvalidDataException>(() => StudentPaperPackage.ReadManifest(Package(json), "exam", 1));
    }

    [Fact]
    public void ValidV2Package_PassesBothPackageAndStudentCoordinateValidation()
    {
        var bytes = Package(JsonSerializer.Serialize(Artifact(), ImportJson.Create()));
        var manifest = StudentPaperPackage.ReadManifest(bytes, "exam", 1);

        Assert.Equal(2, manifest.SchemaVersion);
        var map = OptionRegionMap.TryReadFromPackage(bytes, manifest);
        Assert.NotNull(map);
        foreach (var question in manifest.Questions)
            Assert.Equal(question.Structure.OptionIds.Order(), map.ForQuestion(question.Structure.QuestionId)!.Keys.Order());
    }

    private static StudentPaperManifest Manifest() => StudentPaperFixture.CreateStudentManifest() with { SchemaVersion = 2 };

    private static OptionRegionArtifact Artifact() => new(1, OptionRegionArtifact.AnchorSchemeName, "exam", 1,
        Manifest().Questions.Select(question => new OptionRegionQuestion(question.Structure.QuestionId,
            question.Structure.OptionIds.Select(id => new Exam.Contracts.Importing.OptionRegion(id, 0, 0.1, 0.1, 0.1, 0.1)).ToArray())).ToArray());

    private static byte[] Package(string json)
    {
        var manifest = Manifest();
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(StudentPaperPackage.ManifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, ImportJson.Create()));
            Write(OptionRegionArtifact.FileName, Encoding.UTF8.GetBytes(json));
            foreach (var question in manifest.Questions)
                Write(question.PdfPath, "%PDF-1.4\n% Synthetic header-only boundary fixture.\n"u8.ToArray());
            void Write(string path, byte[] content)
            {
                using var entry = archive.CreateEntry(path).Open();
                entry.Write(content);
            }
        }
        return stream.ToArray();
    }
}
