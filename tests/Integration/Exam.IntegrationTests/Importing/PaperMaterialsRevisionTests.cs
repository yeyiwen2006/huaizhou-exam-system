using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Administration;
using Exam.Contracts.Exams;
using Exam.Contracts.Importing;
using Exam.Contracts.Resources;
using Exam.Contracts.Versioning;
using Exam.Domain.Importing;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Postgres.Administration;
using Exam.Infrastructure.Postgres.Exams;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.Infrastructure.Security;
using Exam.IntegrationTests.Postgres;
using Npgsql;

namespace Exam.IntegrationTests.Importing;

/// <summary>
/// 开考后修订的服务器核对（A 要求「服务器负责核对实际变更题号，漏报与乱报都拒绝」）。
///
/// 用例直接驱动服务层：造两个版本的材料（第二个版本改动指定的文件），按不同声明发布，
/// 断言拒绝码与「拒绝后什么都没留下」。不需要 TeX——学生包与归档都在用例里合成并真实加密。
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PaperMaterialsRevisionTests(PostgresDatabaseFixture database)
{
    private const string ExamNamePrefix = "合成修订考试";
    private const string SharedStyle = "styles/optmarks.sty";

    [Fact]
    public async Task Revision_DeclaringTheActuallyChangedQuestion_IsAcceptedAndKeepsTheBaselineChain()
    {
        using var f = await Fixture.CreateAsync(database.DataSource);
        var v1 = await f.PublishAsync(1, MaterialFiles(f.ExamName, questionOneStem: "题干一"), []);
        Assert.True(v1.Succeeded, v1.Error);

        // 只改第一题的题干，并如实声明第一题。
        var v2 = await f.PublishAsync(2, MaterialFiles(f.ExamName, questionOneStem: "题干一改"), ["q1"]);

        Assert.True(v2.Succeeded, v2.Error);
        Assert.Equal(2, v2.Value!.Versions.PaperVersion);
        // 学生只会被告知复核第一题。
        Assert.Equal(["q1"], v2.Value.Paper!.ChangedQuestionIds);
        // 每个已发布版本都有自己的归档与摘要，供下一次修订比对。
        var baseline = await f.Materials.FindBaselineAsync(f.ExamId, 2);
        Assert.NotNull(baseline);
        Assert.Equal(QuestionContentDigest.CurrentFormatVersion, baseline!.FormatVersion);
        Assert.Equal(2, baseline.QuestionDigests.Count);
    }

    [Fact]
    public async Task Revision_MissingAChangedQuestion_IsRejectedAndLeavesNothingBehind()
    {
        using var f = await Fixture.CreateAsync(database.DataSource);
        Assert.True((await f.PublishAsync(1, MaterialFiles(f.ExamName), [])).Succeeded);

        // 实际改了第一题却声称没有内容变化之外的声明：漏报必须拒绝。
        var rejected = await f.PublishAsync(2, MaterialFiles(f.ExamName, questionOneStem: "题干一改"), ["q2"]);

        Assert.False(rejected.Succeeded);
        Assert.Equal(VersioningErrorCode.ChangedQuestionIdsMissing, rejected.ErrorCode);
        await f.AssertUnchangedAfterRejection();
    }

    [Fact]
    public async Task Revision_DeclaringAnUnchangedQuestion_IsRejected()
    {
        using var f = await Fixture.CreateAsync(database.DataSource);
        Assert.True((await f.PublishAsync(1, MaterialFiles(f.ExamName), [])).Succeeded);

        // 只有第一题变了，却把第二题也报成变更：会逼考生复核没变的题，同样拒绝。
        var rejected = await f.PublishAsync(2, MaterialFiles(f.ExamName, questionOneStem: "题干一改"), ["q1", "q2"]);

        Assert.False(rejected.Succeeded);
        Assert.Equal(VersioningErrorCode.ChangedQuestionIdsUnexpected, rejected.ErrorCode);
        await f.AssertUnchangedAfterRejection();
    }

    [Fact]
    public async Task Revision_WithNoContentChange_IsRejectedInFavourOfTheRulesEntry()
    {
        using var f = await Fixture.CreateAsync(database.DataSource);
        Assert.True((await f.PublishAsync(1, MaterialFiles(f.ExamName), [])).Succeeded);

        // 材料一模一样：不该推出新试卷版本（只改答案请走规则修订入口）。
        var rejected = await f.PublishAsync(2, MaterialFiles(f.ExamName), []);

        Assert.False(rejected.Succeeded);
        Assert.Equal(VersioningErrorCode.NoContentChange, rejected.ErrorCode);
        await f.AssertUnchangedAfterRejection();
    }

    [Fact]
    public async Task Revision_ChangingASharedStyle_MustDeclareEveryQuestionThatDependsOnIt()
    {
        using var f = await Fixture.CreateAsync(database.DataSource);
        Assert.True((await f.PublishAsync(1, MaterialFiles(f.ExamName), [])).Succeeded);

        // 共享样式变了：两题的渲染都变了，只声明一题按漏报拒绝。
        var partial = await f.PublishAsync(2, MaterialFiles(f.ExamName, sharedStyle: "% 共享样式改了一处"), ["q1"]);
        Assert.False(partial.Succeeded);
        Assert.Equal(VersioningErrorCode.ChangedQuestionIdsMissing, partial.ErrorCode);
        await f.AssertUnchangedAfterRejection();

        // 声明两题则接受：共享资源的保守方向是「所有依赖它的题都算变更」。
        var accepted = await f.PublishAsync(2, MaterialFiles(f.ExamName, sharedStyle: "% 共享样式改了一处"), ["q1", "q2"]);
        Assert.True(accepted.Succeeded, accepted.Error);
        Assert.Equal(["q1", "q2"], accepted.Value!.Paper!.ChangedQuestionIds);
    }

    [Fact]
    public async Task Revision_WithoutThePreviousArchive_FailsClosedInsteadOfBecomingAFirstPublication()
    {
        using var f = await Fixture.CreateAsync(database.DataSource);
        Assert.True((await f.PublishAsync(1, MaterialFiles(f.ExamName), [])).Succeeded);
        // 删掉基线归档：这不是「首次发布」，而是无从核对——必须拒绝，否则删一行就能绕过核对。
        await f.Sql("DELETE FROM exam.paper_materials WHERE exam_id=$1 AND paper_version=1", f.ExamId);

        var rejected = await f.PublishAsync(2, MaterialFiles(f.ExamName, questionOneStem: "题干一改"), ["q1"]);

        Assert.False(rejected.Succeeded);
        Assert.Equal(VersioningErrorCode.MaterialsBaselineMissing, rejected.ErrorCode);
        Assert.Equal(1, f.Versions.GetExam(f.ExamId).Value!.Versions.PaperVersion);
    }

    [Fact]
    public async Task Revision_TamperedArchive_IsRejectedBeforeAnyDatabaseWrite()
    {
        using var f = await Fixture.CreateAsync(database.DataSource);
        Assert.True((await f.PublishAsync(1, MaterialFiles(f.ExamName), [])).Succeeded);

        // 声明哈希与实际归档不符：服务器按收到的字节重算，不信任声明。
        var rejected = await f.PublishAsync(2, MaterialFiles(f.ExamName, questionOneStem: "题干一改"), ["q1"], tamperArchive: true);

        Assert.False(rejected.Succeeded);
        Assert.Equal(VersioningErrorCode.MaterialsArchiveInvalid, rejected.ErrorCode);
        await f.AssertUnchangedAfterRejection();
    }

    private static IReadOnlyList<KeyValuePair<string, string>> MaterialFiles(string examName,
        string questionOneStem = "题干一", string sharedStyle = "% 共享样式")
    {
        Assert.NotNull(questionOneStem);
        return
        [
            new($"questions/{examName}_1.tex", QuestionBody(1, questionOneStem)),
            new($"questions/{examName}_2.tex", QuestionBody(2, "题干二")),
            new(SharedStyle, sharedStyle + "\n\\newcommand{\\qimg}[1]{\\includegraphics{#1}}"),
            new("figures/q1.png", "图片一"),
            new("figures/q2.png", "图片二")
        ];
    }

    private static string QuestionBody(int number, string stem) => string.Join('\n',
        "\\documentclass{ctexart}",
        "\\usepackage{" + SharedStyle + "}",
        "\\graphicspath{{figures/}}",
        "\\begin{document}",
        stem,
        "\\includegraphics{figures/q" + number + ".png}",
        "\\end{document}");

    private sealed class Fixture : IDisposable
    {
        private readonly NpgsqlDataSource _source;
        private readonly X509Certificate2 _certificate;
        private static readonly ManagedNodeEncryptionPolicy Policy = new(new HashSet<string>(["node-a"], StringComparer.Ordinal));

        private Fixture(NpgsqlDataSource source, X509Certificate2 certificate, string examId, string examName,
            string manifestJson, string answerJson, PostgresExamVersioningService versions)
        {
            _source = source;
            _certificate = certificate;
            ExamId = examId;
            ExamName = examName;
            ManifestJson = manifestJson;
            AnswerJson = answerJson;
            Versions = versions;
        }

        public string ExamId { get; }
        public string ExamName { get; }
        public string ManifestJson { get; }
        public string AnswerJson { get; }
        public PostgresExamVersioningService Versions { get; }
        public PostgresPaperMaterialStore Materials => new(_source);
        public IReadOnlyList<string> QuestionIds => ["q1", "q2"];

        public static async Task<Fixture> CreateAsync(NpgsqlDataSource source)
        {
            var requestedId = "revision-test-" + Guid.NewGuid().ToString("N");
            var name = ExamNamePrefix + "-" + requestedId[^8..];
            var now = DateTimeOffset.UtcNow;
            var versions = new PostgresExamVersioningService(source);
            // 经考试目录创建：发布事务内会按目录设置精确核对清单，两边必须一致。
            var catalog = new PostgresExamCatalog(source, versions, TimeProvider.System);
            var created = await catalog.CreateAsync(new(Guid.NewGuid().ToString("N"), name,
                new(now.AddYears(80), now.AddYears(80).AddHours(2)),
                new ExamSettings([new ExamSubjectSettings("subject", "科目", "2",
                    [new ExamGroupSettings("group", "单选", "single", 1, 2, "2", false,
                        new ManifestOneCorrect("1", "0", "0"), null)])])), "synthetic-operator");
            var exam = created.Summary.ExamId;
            var manifest = new ExamManifest(1, exam, name,
                [new("subject", "科目", "2")],
                [new("group", "subject", "单选", "single", 1, 2, false, "2",
                    new ManifestGroupScoring(new ManifestOneCorrect("1", "0", "0"), null))],
                [
                    new("q1", 1, "subject", "group", $"questions/{name}_1.tex", ["A", "B"], null),
                    new("q2", 2, "subject", "group", $"questions/{name}_2.tex", ["A", "B"], null)
                ],
                []);
            var key = new AnswerKeyFile(1, exam, [new("q1", 1, ["A"]), new("q2", 2, ["B"])]);
            using var rsa = RSA.Create(2048);
            var certificate = new CertificateRequest("CN=synthetic-revision-node", rsa, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1).CreateSelfSigned(now.AddMinutes(-1), now.AddDays(1));
            await Task.CompletedTask;
            return new Fixture(source, certificate, exam, name,
                JsonSerializer.Serialize(manifest, ImportJson.Create()),
                JsonSerializer.Serialize(key, ImportJson.Create()), versions);
        }

        /// <summary>按目标版本造齐三样东西并上传：学生包密文、源材料归档密文与发布请求。</summary>
        public async Task<VersioningResult<ExamVersionSnapshot>> PublishAsync(long targetVersion,
            IReadOnlyList<KeyValuePair<string, string>> materials, IReadOnlyList<string> declared,
            bool tamperArchive = false)
        {
            // 归档内容 = 清单 + 独立答案 + 题目与共享资源；路径相对材料包根。
            var files = new List<KeyValuePair<string, byte[]>>
            {
                new("manifest.json", Encoding.UTF8.GetBytes(ManifestJson)),
                new("server-only/answer-key.json", Encoding.UTF8.GetBytes(AnswerJson))
            };
            files.AddRange(materials.Select(pair =>
                new KeyValuePair<string, byte[]>(pair.Key, Encoding.UTF8.GetBytes(pair.Value))));
            var archive = PaperMaterialsArchive.Build(ExamId, targetVersion, files);
            var archiveSha256 = Convert.ToHexString(SHA256.HashData(archive));
            if (tamperArchive)
            {
                // 声明一个不存在的哈希：服务器按收到的字节重算，必须认出来。
                archiveSha256 = new string('A', 64);
            }

            // 资源标识与包标识必须一致：服务器按资源标识核对解密出来的学生包。
            // versioned_resources 的主键只有 resource_id 一列，因此资源标识必须按考试区分。
            var resourceId = $"synthetic-resource-{ExamId}-{targetVersion}";
            var (packageCipher, packageEnvelope) = await EncryptAsync(StudentPackage(targetVersion), targetVersion, resourceId);
            var (materialsCipher, materialsEnvelope) = await EncryptAsync(archive, targetVersion, resourceId + "-materials");
            var request = new PublishExamBundleApiRequest(Guid.NewGuid().ToString("N"), targetVersion - 1,
                targetVersion - 1, resourceId, ManifestJson, AnswerJson, declared,
                "合成修订用例", true, archiveSha256);
            var upload = new UploadExamBundleRequest(request,
                new(packageEnvelope.PackageId, packageEnvelope.PaperVersion, packageEnvelope.ChunkSize,
                    packageEnvelope.NoncePrefix, packageEnvelope.WrappedKeys, packageEnvelope.CertificateThumbprints),
                new(materialsEnvelope.PackageId, materialsEnvelope.PaperVersion, materialsEnvelope.ChunkSize,
                    materialsEnvelope.NoncePrefix, materialsEnvelope.WrappedKeys, materialsEnvelope.CertificateThumbprints));
            return await Service().PublishUploadedAsync(ExamId, upload, packageCipher, materialsCipher, "synthetic-operator");
        }

        public async Task AssertUnchangedAfterRejection()
        {
            var snapshot = Versions.GetExam(ExamId).Value!;
            Assert.Equal(1, snapshot.Versions.PaperVersion);
            Assert.Null(await Materials.FindAsync(ExamId, 2));
        }

        public async Task Sql(string sql, params object[] values)
        {
            await using var command = _source.CreateCommand(sql);
            foreach (var value in values)
            {
                command.Parameters.AddWithValue(value);
            }

            await command.ExecuteNonQueryAsync();
        }

        private PostgresBundlePublicationService Service()
        {
            var unwrapper = new Unwrapper(_certificate);
            return new PostgresBundlePublicationService(Versions, new(_source, Policy), unwrapper, Policy,
                new PaperMaterialsVerifier(unwrapper, Policy, new PostgresPaperMaterialStore(_source)),
                new Certificates(_certificate));
        }

        private byte[] StudentPackage(long version)
        {
            var rules = ImportedGradingRules.Create(ManifestJson, AnswerJson);
            // 每道题一份自己的 PDF：学生包读取器要求每题路径唯一且在 questions/ 下。
            var questions = rules.Structure.Questions
                .Select(question => new StudentPaperQuestion(question, $"questions/{question.QuestionId}.pdf", null))
                .ToArray();
            var display = new StudentPaperManifest(2, ExamId, ExamName, version,
                [new("subject", "科目")], [new("group", "subject", "单选", 1)], questions, []);
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                Add(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(display, ImportJson.Create()));
                foreach (var question in questions)
                {
                    Add(zip, question.PdfPath, MinimalPdf());
                }
                Add(zip, OptionRegionArtifact.FileName, JsonSerializer.SerializeToUtf8Bytes(
                    new OptionRegionArtifact(1, OptionRegionArtifact.AnchorSchemeName, ExamId, version,
                        rules.Structure.Questions.Select(question => new OptionRegionQuestion(question.QuestionId,
                            [new("A", 0, .1, .2, .05, .03), new("B", 0, .1, .3, .05, .03)])).ToArray()),
                    ImportJson.Create()));
            }

            return buffer.ToArray();
        }

        private static void Add(ZipArchive zip, string path, byte[] content)
        {
            using var stream = zip.CreateEntry(path).Open();
            stream.Write(content);
        }

        private async Task<(byte[] Cipher, EncryptedPackageEnvelope Envelope)> EncryptAsync(byte[] payload,
            long version, string packageId)
        {
            using var input = new MemoryStream(payload, writable: false);
            using var encrypted = new MemoryStream();
            var envelope = await new PackageEncryptionService(4096, Policy).EncryptAsync(input, encrypted,
                packageId, version, [new("node-a", _certificate)]);
            return (encrypted.ToArray(), envelope);
        }

        private static byte[] MinimalPdf() => Encoding.ASCII.GetBytes(
            "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\ntrailer\n<< /Size 3 /Root 1 0 R >>\n%%EOF\n");

        public void Dispose() => _certificate.Dispose();

        private sealed class Unwrapper(X509Certificate2 certificate) : IPackageKeyUnwrapper
        {
            public byte[] Unwrap(EncryptedPackageEnvelope envelope) =>
                PackageEncryptionService.UnwrapDataKey(envelope, "node-a", certificate);
        }

        private sealed class Certificates(X509Certificate2 certificate) : IPublicationCertificateProvider
        {
            public PublicationCertificateLease Acquire() =>
                new([new("node-a", X509CertificateLoader.LoadCertificate(certificate.RawData))]);
        }
    }
}
