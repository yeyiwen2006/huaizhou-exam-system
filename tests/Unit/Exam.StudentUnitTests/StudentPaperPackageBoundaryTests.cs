using System.IO;
using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Importing;
using Exam.Contracts.Submissions;
using Exam.Contracts.Resources;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

public sealed class StudentPaperPackageBoundaryTests
{
    [Fact]
    public void DeclaredImageWithInvalidSignatureCannotBecomeUsableContent()
    {
        var files = Files(StudentPaperFixture.CreateDisplayManifest() with { DisplayResources = ["assets/image.png"] });
        files.Add(("assets/image.png", "not a PNG"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => StudentPaperPackage.ReadManifest(Zip(files), "exam", 1));
    }

    [Fact]
    public void ValidPdfHeaderDoesNotHideAnIncompleteBody()
    {
        var payload = Zip(Files());
        var changed = false;
        for (var offset = 0; offset + 46 <= payload.Length; offset++)
        {
            if (!payload.AsSpan(offset, 4).SequenceEqual(new byte[] { 0x50, 0x4b, 0x01, 0x02 })) continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset + 28, 2));
            if (offset + 46 + nameLength > payload.Length) continue;
            var name = Encoding.UTF8.GetString(payload, offset + 46, nameLength);
            if (!name.StartsWith("questions/", StringComparison.Ordinal)) continue;
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset + 24, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 24, 4), declared + 1);
            changed = true;
            break;
        }
        Assert.True(changed);
        Assert.Throws<InvalidDataException>(() => StudentPaperPackage.ReadManifest(payload, "exam", 1));
    }

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
                StudentPaperFixture.CreateDisplayManifest() with { SchemaVersion = 99 }, ImportJson.Create())); break;
            case "invalid-json": files[0] = (files[0].Path, "{"u8.ToArray()); break;
            case "invalid-utf8": files[0] = (files[0].Path, new byte[] { 255, 254, 255 }); break;
            default: throw new ArgumentOutOfRangeException(nameof(problem));
        }
        Assert.Throws<InvalidDataException>(() => StudentPaperPackage.ReadManifest(Zip(files), "exam", 1));
    }

    [Fact]
    public void CompressedOversizedMetadataIsRejectedWithoutExpandingIt()
    {
        var files = Files();
        files[0] = (files[0].Path, new byte[StudentPackageLayout.MaxMetadataBytes + 1]);
        var payload = Zip(files);
        Assert.True(payload.Length < StudentPackageLayout.MaxMetadataBytes);
        Assert.Throws<InvalidDataException>(() => StudentPaperPackage.ReadManifest(payload, "exam", 1));
    }

    [Fact]
    public void TooManyZipEntriesAreRejected()
    {
        var files = Files();
        for (var i = files.Count; i <= StudentPackageLayout.MaxEntryCount; i++)
            files.Add(($"assets/empty-{i}.pdf", []));
        Assert.Throws<InvalidDataException>(() => StudentPaperPackage.ReadManifest(Zip(files), "exam", 1));
    }

    [Theory]
    [InlineData("assets/image.png")]
    [InlineData("ASSETS/image.png")]
    [InlineData("image.png")]
    public void ExplicitDisplayPathsArePreserved(string path)
    {
        var files = Files(StudentPaperFixture.CreateDisplayManifest() with { DisplayResources = [path] });
        files.Add((path, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        var catalog = StudentPaperPackage.ReadManifest(Zip(files), "exam", 1);
        Assert.Equal(2, catalog.Questions.Count);
        Assert.Equal(path, Assert.Single(catalog.DisplayResources));
    }

    [Theory]
    [InlineData("assets/raw.tex")]
    [InlineData("assets/answer.pdf")]
    [InlineData("server-only/image.pdf")]
    public void ExplicitManifestCannotAllowServerOnlyOrNonDisplayFiles(string path)
    {
        var files = Files(StudentPaperFixture.CreateDisplayManifest() with { DisplayResources = [path] });
        files.Add((path, "%PDF-1.4"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => StudentPaperPackage.ReadManifest(Zip(files), "exam", 1));
    }

    [Fact]
    public void CatalogOwnsItsValidatedIdentityAndOptions()
    {
        var payload = Zip(Files());
        var catalog = StudentPaperPackage.ReadManifest(payload, "exam", 1);
        Array.Clear(payload);
        Assert.Equal("exam", catalog.ExamId);
        Assert.Equal(2, catalog.Questions.Count);
        var structure = catalog.Structure;
        var answers = structure.Questions.ToDictionary(q => q.QuestionId, _ => AnswerValue.Empty());
        Assert.Null(AnswerSheetContract.Validate(structure, answers));
        Assert.Null(AnswerSheetContract.ValidateAnswer(structure.Questions[0], AnswerValue.Selected("A")));
        Assert.NotNull(AnswerSheetContract.ValidateAnswer(structure.Questions[0], AnswerValue.Selected("A", "B")));
        Assert.NotNull(AnswerSheetContract.ValidateAnswer(structure.Questions[0], AnswerValue.Abstain()));
        Assert.Null(AnswerSheetContract.ValidateAnswer(structure.Questions[1], AnswerValue.Abstain()));
        Assert.DoesNotContain(structure.Questions, q => q.QuestionId == "Q1");
        var options = Assert.IsAssignableFrom<IList<string>>(structure.Questions[0].OptionIds);
        Assert.Throws<NotSupportedException>(() => options[0] = "Z");
        var questions = Assert.IsAssignableFrom<IList<StudentPaperQuestion>>(catalog.Questions);
        Assert.Throws<NotSupportedException>(() => questions.Clear());
    }

    [Fact]
    public void CancelledReadDoesNotReturnAnAuthorizedCatalog()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => StudentPaperPackage.ReadManifest(Zip(Files()), "exam", 1, cancellation.Token));
    }

    private static List<(string Path, byte[] Bytes)> Files(StudentPaperManifest? manifest = null)
    {
        manifest ??= StudentPaperFixture.CreateDisplayManifest();
        var files = new List<(string, byte[])>
        {
            (StudentPackagePlan.MetadataFileName, JsonSerializer.SerializeToUtf8Bytes(manifest, ImportJson.Create()))
        };
        files.AddRange(manifest.Questions.Select(question =>
            (question.PdfPath, Encoding.ASCII.GetBytes("%PDF-1.4\n% synthetic test header\n"))));
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
