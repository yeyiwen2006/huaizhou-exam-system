using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Importing;
using Exam.Contracts.Submissions;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

public sealed class StudentPaperDefinitionTests
{
    [Theory]
    [InlineData("missing-metadata")]
    [InlineData("missing-pdf")]
    [InlineData("duplicate-metadata")]
    [InlineData("case-collision")]
    [InlineData("parent-path")]
    [InlineData("backslash-path")]
    [InlineData("absolute-path")]
    [InlineData("device-name")]
    [InlineData("unknown-file")]
    [InlineData("bad-pdf")]
    [InlineData("unsupported-schema")]
    [InlineData("invalid-json")]
    [InlineData("invalid-utf8")]
    public void InvalidPackagesAreRejectedBeforeCatalogConstruction(string problem)
    {
        var files = Files();
        switch (problem)
        {
            case "missing-metadata": files.RemoveAt(0); break;
            case "missing-pdf": files.RemoveAt(1); break;
            case "duplicate-metadata": files.Add(files[0]); break;
            case "case-collision": files.Add((files[1].Path.ToUpperInvariant(), files[1].Bytes)); break;
            case "parent-path": files.Add(("../outside.pdf", "%PDF-1.4"u8.ToArray())); break;
            case "backslash-path": files.Add(("assets\\image.pdf", "%PDF-1.4"u8.ToArray())); break;
            case "absolute-path": files.Add(("/image.pdf", "%PDF-1.4"u8.ToArray())); break;
            case "device-name": files.Add(("assets/CON.pdf", "%PDF-1.4"u8.ToArray())); break;
            case "unknown-file": files.Add(("other.pdf", "%PDF-1.4"u8.ToArray())); break;
            case "bad-pdf": files[1] = (files[1].Path, "not a PDF"u8.ToArray()); break;
            case "unsupported-schema": files[0] = (files[0].Path, JsonSerializer.SerializeToUtf8Bytes(
                StudentPaperFixture.CreateManifest() with { SchemaVersion = 99 }, ImportJson.Create())); break;
            case "invalid-json": files[0] = (files[0].Path, "{"u8.ToArray()); break;
            case "invalid-utf8": files[0] = (files[0].Path, new byte[] { 255, 254, 255 }); break;
            default: throw new ArgumentOutOfRangeException(nameof(problem));
        }
        Assert.Throws<InvalidDataException>(() => StudentPaperDefinition.Read(Zip(files), "exam"));
    }

    [Fact]
    public void CompressedOversizedMetadataIsRejectedWithoutExpandingIt()
    {
        var files = Files();
        files[0] = (files[0].Path, new byte[StudentPackageLayout.MaxMetadataBytes + 1]);
        var payload = Zip(files);
        Assert.True(payload.Length < StudentPackageLayout.MaxMetadataBytes);
        Assert.Throws<InvalidDataException>(() => StudentPaperDefinition.Read(payload, "exam"));
    }

    [Fact]
    public void TooManyZipEntriesAreRejected()
    {
        var files = Files();
        for (var i = files.Count; i <= StudentPackageLayout.MaxEntryCount; i++)
            files.Add(($"assets/empty-{i}.pdf", []));
        Assert.Throws<InvalidDataException>(() => StudentPaperDefinition.Read(Zip(files), "exam"));
    }

    [Theory]
    [InlineData("assets", "assets/image.png")]
    [InlineData("ASSETS", "assets/image.png")]
    [InlineData(".", "image.png")]
    public void DeclaredResourceRootsMatchProducerNormalization(string root, string path)
    {
        var files = Files(StudentPaperFixture.CreateManifest() with { ResourceRoots = [root] });
        files.Add((path, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        var catalog = StudentPaperDefinition.Read(Zip(files), "exam");
        Assert.Equal(2, catalog.QuestionCount);
    }

    [Theory]
    [InlineData("assets/raw.tex")]
    [InlineData("assets/answer.pdf")]
    [InlineData("server-only/image.pdf")]
    public void DeclaringAResourceRootDoesNotAllowServerOnlyOrNonDisplayFiles(string path)
    {
        var files = Files(StudentPaperFixture.CreateManifest() with { ResourceRoots = ["."] });
        files.Add((path, "%PDF-1.4"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => StudentPaperDefinition.Read(Zip(files), "exam"));
    }

    [Fact]
    public void CatalogOwnsItsValidatedIdentityAndOptions()
    {
        var payload = Zip(Files());
        var catalog = StudentPaperDefinition.Read(payload, "exam");
        Array.Clear(payload);
        Assert.Equal("exam", catalog.ExamId);
        Assert.Equal(2, catalog.QuestionCount);
        Assert.True(catalog.HasCompleteAnswers(catalog.CreateEmptyAnswers()));
        Assert.True(catalog.AllowsAnswer("q1", AnswerValue.Selected("A")));
        Assert.False(catalog.AllowsAnswer("q1", AnswerValue.Selected("A", "B")));
        Assert.False(catalog.AllowsAnswer("q1", AnswerValue.Abstain()));
        Assert.True(catalog.AllowsAnswer("q2", AnswerValue.Abstain()));
        Assert.False(catalog.AllowsAnswer("Q1", AnswerValue.Empty()));
    }

    [Fact]
    public void CancelledReadDoesNotReturnAnAuthorizedCatalog()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => StudentPaperDefinition.Read(Zip(Files()), "exam", cancellation.Token));
    }

    private static List<(string Path, byte[] Bytes)> Files(ExamManifest? manifest = null)
    {
        manifest ??= StudentPaperFixture.CreateManifest();
        var files = new List<(string, byte[])>
        {
            (StudentPackagePlan.MetadataFileName, JsonSerializer.SerializeToUtf8Bytes(manifest, ImportJson.Create()))
        };
        files.AddRange(manifest.Questions.Select(question =>
            (StudentPackageLayout.QuestionPdfPath(question.Source), Encoding.ASCII.GetBytes("%PDF-1.4\n% synthetic test header\n"))));
        return files;
    }

    private static byte[] Zip(IEnumerable<(string Path, byte[] Bytes)> files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, bytes) in files)
            {
                using var entry = zip.CreateEntry(path).Open();
                entry.Write(bytes);
            }
        return stream.ToArray();
    }
}
