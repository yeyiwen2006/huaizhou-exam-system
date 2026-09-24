using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;

namespace Exam.UnitTests.Packaging;

public sealed class StudentPaperPackageTests
{
    private static StudentPaperManifest Manifest => new(1, "exam", "合成考试", 3,
        [new("s", "科目")], [new("g", "s", "题组", 1)],
        [new(new("q", 1, "s", "g", "single", ["A", "B"]), "questions/q.pdf", null)], []);

    [Theory]
    [InlineData("missing-pdf")]
    [InlineData("duplicate-file")]
    [InlineData("case-collision")]
    [InlineData("unexpected-file")]
    [InlineData("traversal")]
    [InlineData("exam")]
    [InlineData("version")]
    [InlineData("duplicate-property")]
    [InlineData("extra-property")]
    [InlineData("invalid-pdf")]
    public void CorruptOrMismatchedPackagesNeverBecomeAnswerCatalogs(string defect)
    {
        var manifest = JsonSerializer.Serialize(Manifest, ImportJson.Create());
        if (defect == "duplicate-property") manifest = manifest.Replace("\"schemaVersion\":1", "\"schemaVersion\":2,\"schemaVersion\":1");
        if (defect == "extra-property") manifest = manifest.Replace("\"schemaVersion\":1", "\"serverOnly\":true,\"schemaVersion\":1");
        var files = new List<(string Path, string Text)> { ("manifest.json", manifest) };
        if (defect != "missing-pdf") files.Add(("questions/q.pdf", defect == "invalid-pdf" ? "NOT A PDF" : "%PDF-1.4\nsynthetic"));
        if (defect == "duplicate-file") files.Add(("questions/q.pdf", "%PDF-1.4"));
        if (defect == "case-collision") files.Add(("QUESTIONS/Q.PDF", "%PDF-1.4"));
        if (defect == "unexpected-file") files.Add(("server-only/answer-key.json", "{}"));
        if (defect == "traversal") files.Add(("../q.pdf", "%PDF-1.4"));
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(file.Path).Open(), new UTF8Encoding(false));
                writer.Write(file.Text);
            }
        var error = Record.Exception(() => StudentPaperPackage.ReadManifest(bytes.ToArray(), defect == "exam" ? "other" : "exam", defect == "version" ? 2 : 3));
        Assert.True(error is IOException or InvalidDataException or JsonException, error?.ToString() ?? "Expected package rejection");
    }
}
