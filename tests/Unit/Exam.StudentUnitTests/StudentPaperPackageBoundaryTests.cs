using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Infrastructure.Compilation;

namespace Exam.StudentUnitTests;

public sealed class StudentPaperPackageBoundaryTests
{
    [Theory]
    [InlineData("missing-metadata")]
    [InlineData("missing-pdf")]
    [InlineData("duplicate-metadata")]
    [InlineData("case-collision")]
    [InlineData("parent-path")]
    [InlineData("backslash-path")]
    [InlineData("absolute-path")]
    [InlineData("unknown-file")]
    [InlineData("bad-pdf")]
    [InlineData("unsupported-schema")]
    [InlineData("invalid-json")]
    [InlineData("invalid-utf8")]
    [InlineData("v2-without-option-regions")]
    [InlineData("v1-with-option-regions")]
    [InlineData("v2-invalid-option-regions")]
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
            case "unknown-file": files.Add(("other.pdf", "%PDF-1.4"u8.ToArray())); break;
            case "bad-pdf": files[1] = (files[1].Path, "not a PDF"u8.ToArray()); break;
            case "unsupported-schema": files[0] = (files[0].Path, JsonSerializer.SerializeToUtf8Bytes(
                StudentPaperFixture.CreateStudentManifest() with { SchemaVersion = 99 }, ImportJson.Create())); break;
            case "invalid-json": files[0] = (files[0].Path, "{"u8.ToArray()); break;
            case "invalid-utf8": files[0] = (files[0].Path, new byte[] { 255, 254, 255 }); break;
            // 声明 v2 就必须在、声明 v1 就不许夹带：否则同一份清单会有两种解释。
            case "v2-without-option-regions":
                files = Files(StudentPaperFixture.CreateStudentManifest() with { SchemaVersion = 2 });
                break;
            case "v1-with-option-regions":
                files.Add((OptionRegionArtifact.FileName, ValidOptionRegions()));
                break;
            case "v2-invalid-option-regions":
                files = Files(StudentPaperFixture.CreateStudentManifest() with { SchemaVersion = 2 });
                files.Add((OptionRegionArtifact.FileName, "{"u8.ToArray()));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(problem));
        }
        AssertRejected(Zip(files));
    }

    [Fact]
    public void CompressedOversizedMetadataIsRejectedWithoutExpandingIt()
    {
        var files = Files();
        files[0] = (files[0].Path, new byte[StudentPackageLayout.MaxMetadataBytes + 1]);
        var payload = Zip(files);
        Assert.True(payload.Length < StudentPackageLayout.MaxMetadataBytes);
        AssertRejected(payload);
    }

    [Fact]
    public void TooManyZipEntriesAreRejected()
    {
        var files = Files();
        for (var i = files.Count; i <= StudentPackageLayout.MaxEntryCount; i++) files.Add(($"assets/empty-{i}.pdf", []));
        AssertRejected(Zip(files));
    }

    [Theory]
    [InlineData("assets/image.png")]
    [InlineData("ASSETS/image.png")]
    [InlineData("image.png")]
    public void ExplicitDisplayResourcesRetainTheirExactPublishedPaths(string path)
    {
        var files = Files(StudentPaperFixture.CreateStudentManifest() with { DisplayResources = [path] });
        files.Add((path, new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        var manifest = StudentPaperPackage.ReadManifest(Zip(files), "exam", 1);
        Assert.Equal(2, manifest.Questions.Count);
        Assert.Equal(path, Assert.Single(manifest.DisplayResources));
    }

    [Theory]
    [InlineData("assets/raw.tex")]
    [InlineData("assets/answer.pdf")]
    [InlineData("server-only/image.pdf")]
    [InlineData("assets/CON.pdf")]
    [InlineData("assets/COM1.pdf")]
    [InlineData("assets/CON .pdf")]
    [InlineData("assets/LPT¹.pdf")]
    public void DeclaringAResourceDoesNotMakeForbiddenFilesDistributable(string path)
    {
        var files = Files(StudentPaperFixture.CreateStudentManifest() with { DisplayResources = [path] });
        files.Add((path, "%PDF-1.4"u8.ToArray()));
        AssertRejected(Zip(files));
    }

    [Theory]
    [InlineData("assets/image.pdf")]
    [InlineData("assets/image.png")]
    [InlineData("assets/image.jpg")]
    public void DeclaredDisplayResourcesMustHaveTheProducerRequiredFileHeader(string path)
    {
        var files = Files(StudentPaperFixture.CreateStudentManifest() with { DisplayResources = [path] });
        files.Add((path, "invalid display content"u8.ToArray()));
        AssertRejected(Zip(files));
    }

    [Fact]
    public void ValidPdfHeaderDoesNotConcealATruncatedBody()
    {
        var files = Files();
        var payload = Zip(files);
        var pdfPath = files[1].Path;
        var patched = false;
        for (var offset = 0; offset + 46 < payload.Length; offset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset)) != 0x02014b50) continue;
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset + 28));
            if (offset + 46 + nameLength > payload.Length ||
                Encoding.UTF8.GetString(payload, offset + 46, nameLength) != pdfPath) continue;
            var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset + 24));
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 24), declaredLength + 100);
            patched = true;
            break;
        }
        Assert.True(patched);
        AssertRejected(payload);
    }

    [Fact]
    public void ValidatedCatalogCannotBeMutatedThroughItsCollectionInterfaces()
    {
        var payload = Zip(Files());
        var manifest = StudentPaperPackage.ReadManifest(payload, "exam", 1);
        Array.Clear(payload);
        var options = Assert.IsAssignableFrom<IList<string>>(manifest.Questions[0].Structure.OptionIds);
        Assert.Throws<NotSupportedException>(() => options[0] = "unknown");
        var questions = Assert.IsAssignableFrom<IList<StudentPaperQuestion>>(manifest.Questions);
        Assert.Throws<NotSupportedException>(() => questions.Clear());
        var resources = Assert.IsAssignableFrom<IList<string>>(manifest.DisplayResources);
        Assert.Throws<NotSupportedException>(() => resources.Add("other.pdf"));
        var structure = Assert.IsAssignableFrom<IList<Exam.Contracts.Versioning.PaperQuestionStructure>>(manifest.Structure.Questions);
        Assert.Throws<NotSupportedException>(() => structure.Clear());
        Assert.Equal(2, manifest.Questions.Count);
        Assert.Equal("A", manifest.Questions[0].Structure.OptionIds[0]);
    }

    [Fact]
    public void CancelledManifestReadDoesNotReturnACatalog()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => StudentPaperPackage.ReadManifest(
            Zip(Files()), "exam", 1, cancellation.Token));
    }

    [Fact]
    public void CancelledPdfReadDoesNotReturnADisplayCopy()
    {
        var payload = Zip(Files());
        var manifest = StudentPaperPackage.ReadManifest(payload, "exam", 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => StudentPaperPackage.ReadQuestionPdf(
            payload, manifest, "q1", cancellation.Token));
    }

    [Fact]
    public void V2PackageWithValidOptionRegions_IsAccepted()
    {
        // 两个独立定义的坐标文件名必须一致：组包/契约侧与 B03 读取侧。
        Assert.Equal(Exam.Student.Core.OptionRegionMap.EntryPath, OptionRegionArtifact.FileName);

        var files = Files(StudentPaperFixture.CreateStudentManifest() with { SchemaVersion = 2 });
        files.Add((OptionRegionArtifact.FileName, ValidOptionRegions()));

        var manifest = StudentPaperPackage.ReadManifest(Zip(files), "exam", 1);

        Assert.Equal(StudentPaperPackage.OptionRegionSchemaVersion, manifest.SchemaVersion);
    }

    private static byte[] ValidOptionRegions()
    {
        var studentManifest = StudentPaperFixture.CreateStudentManifest();
        var artifact = new OptionRegionArtifact(
            OptionRegionArtifact.CurrentArtifactVersion,
            OptionRegionArtifact.AnchorSchemeName,
            studentManifest.ExamId,
            studentManifest.PaperVersion,
            studentManifest.Questions.Select(question => new OptionRegionQuestion(
                question.Structure.QuestionId,
                question.Structure.OptionIds
                    .Select(optionId => new Exam.Contracts.Importing.OptionRegion(optionId, 0, 0.1, 0.1, 0.02, 0.02))
                    .ToList()))
                .ToList());
        return Encoding.UTF8.GetBytes(OptionRegionArtifactJson.Serialize(artifact));
    }

    private static void AssertRejected(byte[] payload)
    {
        var exception = Record.Exception(() => StudentPaperPackage.ReadManifest(payload, "exam", 1));
        Assert.True(exception is IOException or InvalidDataException or JsonException,
            exception?.ToString() ?? "Invalid ZIP content was accepted.");
    }

    private static List<(string Path, byte[] Bytes)> Files(StudentPaperManifest? manifest = null)
    {
        manifest ??= StudentPaperFixture.CreateStudentManifest();
        var files = new List<(string, byte[])>
        {
            (StudentPackagePlan.MetadataFileName, JsonSerializer.SerializeToUtf8Bytes(manifest, ImportJson.Create()))
        };
        files.AddRange(manifest.Questions.Select(question =>
            (question.PdfPath, Encoding.ASCII.GetBytes("%PDF-1.4\n% synthetic header, not rendering acceptance\n"))));
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
