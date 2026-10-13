using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Domain.Importing;
using Exam.Infrastructure.Archives;
using Exam.Infrastructure.Compilation;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Security;

namespace Exam.UnitTests.Archives;

public sealed class PortableExamResourcesTests
{
    [Fact]
    public async Task Copied_resources_rebind_identity_paths_and_target_certificate_without_changing_the_source()
    {
        using var sourceCertificate = Certificate("source-node");
        using var targetCertificate = Certificate("target-node");
        var encryption = new PackageEncryptionService(4096);
        var manifest = new StudentPaperManifest(2, "source-exam", "原考试", 1,
            [new("s", "科目")], [new("g", "s", "单选", 1)],
            [new(new("q", 1, "s", "g", "single", ["A", "B"]), "questions/原考试_1.pdf", null)], []);
        var regions = new OptionRegionArtifact(1, OptionRegionArtifact.AnchorSchemeName, "source-exam", 1,
            [new("q", [new("A", 0, 0.1, 0.1, 0.1, 0.1), new("B", 0, 0.3, 0.1, 0.1, 0.1)])]);
        var pdf = "%PDF-1.4\nsynthetic question"u8.ToArray();
        var package = Zip(new Dictionary<string, byte[]>
        {
            [StudentPaperPackage.ManifestPath] = JsonSerializer.SerializeToUtf8Bytes(manifest, ImportJson.Create()),
            [OptionRegionArtifact.FileName] = Encoding.UTF8.GetBytes(OptionRegionArtifactJson.Serialize(regions)),
            ["questions/原考试_1.pdf"] = pdf
        });
        using var ciphertext = new MemoryStream();
        var envelope = await encryption.EncryptAsync(new MemoryStream(package), ciphertext, "source-resource", 1,
            [new("source-node", sourceCertificate)]);
        var original = new VersionedEncryptedResource("source-resource", "source-exam", "paper-package", 1,
            ciphertext.ToArray(), envelope, DateTimeOffset.UtcNow, "synthetic-operator");
        var service = new PortableExamResources(new Unwrapper("source-node", sourceCertificate),
            new Provider("target-node", targetCertificate), encryption);
        var portable = await service.ExportAsync(original);
        Assert.Equal(package, portable.Content);
        var imported = await service.RebindAsync(portable, "target-exam", "原考试（导入副本 2）", "target-resource");
        Assert.Equal(new[] { "target-node" }, imported.Resource.Envelope.WrappedKeys.Keys);
        using var decoded = new MemoryStream();
        await encryption.DecryptAsync(new MemoryStream(imported.Resource.EncryptedContent), decoded,
            imported.Resource.Envelope, "target-node", targetCertificate);
        var copiedPackage = decoded.ToArray();
        var copied = StudentPaperPackage.ReadManifest(copiedPackage, "target-exam", 1);
        Assert.Equal("原考试（导入副本 2）", copied.ExamName);
        Assert.Equal("questions/原考试（导入副本 2）_1.pdf", copied.Questions[0].PdfPath);
        Assert.Equal(pdf, StudentPaperPackage.ReadQuestionPdf(copiedPackage, copied, "q"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(copiedPackage)).ToLowerInvariant(), imported.StudentPackageSha256);
        Assert.Equal(package, (await service.ExportAsync(original)).Content);
        Assert.Equal(package, portable.Content);
        Assert.Equal("source-exam", original.ExamId);
    }

    [Fact]
    public void Materials_copy_renames_primary_sources_and_keeps_answers_and_relative_includes()
    {
        const string sourceManifest = """
            {"schemaVersion":1,"examId":"source-exam","examName":"原考试",
             "subjects":[{"subjectId":"s","name":"科目","maxScore":"1"}],
             "groups":[{"groupId":"g","subjectId":"s","name":"单选","type":"single","order":1,"questionCount":1,
                "scoring":{"oneCorrect":{"correct":"1","blank":"0","wrong":"0"}}}],
             "questions":[{"questionId":"q","questionNo":1,"subjectId":"s","groupId":"g",
                "source":"questions/原考试_1.tex","options":["A","B"]}],"resourceRoots":[]}
            """;
        const string answers = """
            {"schemaVersion":1,"examId":"source-exam","answers":[{"questionId":"q","questionNo":1,"correctOptionIds":["B"]}]}
            """;
        var tex = Encoding.UTF8.GetBytes("\\input{fragment.tex}\n合成题干");
        var files = new Dictionary<string, byte[]>
        {
            ["manifest.json"] = Encoding.UTF8.GetBytes(sourceManifest),
            ["server-only/answer-key.json"] = Encoding.UTF8.GetBytes(answers),
            ["questions/原考试_1.tex"] = tex,
            ["questions/fragment.tex"] = Encoding.UTF8.GetBytes("合成公共片段")
        };
        var archive = PaperMaterialsArchive.Build("source-exam", 1, files);
        var row = new PaperMaterialsRow("source-exam", 1, archive, "source-hash", "source-package-hash", 1,
            files.Count, new Dictionary<string, string>(), DateTimeOffset.UtcNow, "synthetic-operator");
        var copy = PortableExamResources.RebindMaterials(row, "target-exam", "导入考试", "target-package-hash");
        var opened = PaperMaterialsArchive.Read(copy.Archive);
        Assert.True(opened.Succeeded, opened.Failure?.Value);
        Assert.Equal("target-exam", opened.Manifest!.ExamId);
        var manifestJson = Encoding.UTF8.GetString(opened.Materials!.Read("manifest.json"));
        Assert.Equal(PortableExamResources.RebindManifestJson(sourceManifest, "target-exam", "导入考试"), manifestJson);
        Assert.True(new ExamManifestValidator().ValidateJson(manifestJson).IsValid);
        var manifest = JsonSerializer.Deserialize<ExamManifest>(manifestJson, ImportJson.Create())!;
        Assert.Equal("questions/导入考试_1.tex", manifest.Questions[0].Source);
        Assert.Equal(tex, opened.Materials.Read(manifest.Questions[0].Source));
        Assert.Equal(tex, opened.Materials.Read("questions/原考试_1.tex"));
        var copiedAnswers = JsonSerializer.Deserialize<AnswerKeyFile>(opened.Materials.Read("server-only/answer-key.json"), ImportJson.Create())!;
        Assert.Equal("target-exam", copiedAnswers.ExamId);
        Assert.Equal(new[] { "B" }, copiedAnswers.Answers[0].CorrectOptionIds);
        Assert.Equal("target-package-hash", copy.PackageContentSha256);
        Assert.Equal(QuestionContentDigest.Compute(manifest, opened.Materials)["q"], copy.QuestionDigests["q"]);
        Assert.Equal(archive, row.Archive);
        Assert.Equal("source-exam", PaperMaterialsArchive.Read(row.Archive).Manifest!.ExamId);
    }

    private static byte[] Zip(IReadOnlyDictionary<string, byte[]> files)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, content) in files)
            {
                using var output = zip.CreateEntry(path).Open();
                output.Write(content);
            }
        return stream.ToArray();
    }

    private static X509Certificate2 Certificate(string name)
    {
        using var key = RSA.Create(2048);
        return new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class Unwrapper(string node, X509Certificate2 certificate) : IPackageKeyUnwrapper
    {
        public byte[] Unwrap(EncryptedPackageEnvelope envelope) => PackageEncryptionService.UnwrapDataKey(envelope, node, certificate);
    }

    private sealed class Provider(string node, X509Certificate2 certificate) : IPublicationCertificateProvider
    {
        public PublicationCertificateLease Acquire() => new([new(node, X509CertificateLoader.LoadCertificate(certificate.RawData))]);
    }
}
