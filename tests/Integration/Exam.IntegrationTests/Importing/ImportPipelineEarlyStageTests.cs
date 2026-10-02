using System.Text;
using Exam.Contracts.Common;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Postgres.Administration;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.Infrastructure.Security;
using Exam.IntegrationTests.Postgres;

namespace Exam.IntegrationTests.Importing;

/// <summary>
/// C05 导入流水线的 PostgreSQL 集成用例：**失败停在哪个阶段**。
///
/// 这些用例都在编译之前就应当停下（材料不过校验、缺答案、没有加密收件人），
/// 因此不需要 TeX；它们守住的是「不产出部分结果、不把失败当成功」这条行为约定。
/// 完整的校验→编译→组包→入库→发布链路见 D06 的真实材料对照记录与发布模块用例。
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class ImportPipelineEarlyStageTests(PostgresDatabaseFixture fixture) : IDisposable
{
    private const string ExamId = "import-pipeline-exam";
    private const string ExamName = "导入流水线合成考试";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "import-pipeline-" + Guid.NewGuid().ToString("N"));

    // 材料与产物必须分开：产物目录不能位于材料目录之内（流水线在读取材料之前就会核对）。
    private string Materials => Path.Combine(_root, "materials");
    private string Compilation => Path.Combine(_root, "compiled");
    private string Packages => Path.Combine(_root, "packages");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    [Fact]
    public async Task RunAsync_WithoutManifest_StopsAtValidate()
    {
        Directory.CreateDirectory(Materials);

        var result = await Pipeline().RunAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Equal(ImportStage.Validate, result.Stage);
        Assert.Equal("MaterialMissing", Assert.Single(result.Issues).Code);
        Assert.Null(result.ResourceId);
    }

    [Fact]
    public async Task RunAsync_WithManifestFailingValidation_StopsAtValidate()
    {
        WritePackage(sourceFileName: "另一场考试_1.tex");

        var result = await Pipeline().RunAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Equal(ImportStage.Validate, result.Stage);
        Assert.Contains(ManifestRuleIds.QuestionSourceFileName,
            Assert.Single(result.Issues).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WithoutAnswerKey_StopsAtValidate()
    {
        // 缺完整参考答案：即使清单本身合法，也不允许发布成品包。
        WritePackage(sourceFileName: $"{ExamName}_1.tex", withAnswerKey: false);

        var result = await Pipeline().RunAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Equal(ImportStage.Validate, result.Stage);
        Assert.Equal("AnswerKeyIncomplete", Assert.Single(result.Issues).Code);
    }

    [Fact]
    public async Task RunAsync_WithoutConfiguredPublicationCertificate_StopsAtPackageBeforeCompiling()
    {
        // 服务器没有配置发布公钥：没有可用的加密收件人，阶段报 Package，且不产生任何产物目录内容。
        WritePackage(sourceFileName: $"{ExamName}_1.tex");

        var result = await Pipeline(new UnconfiguredCertificates()).RunAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Equal(ImportStage.Package, result.Stage);
        Assert.Equal("RecipientMissing", Assert.Single(result.Issues).Code);
        AssertNoCompileOutput();
    }

    [Fact]
    public async Task RunAsync_WithCertificatesNotCoveringManagedNodes_StopsAtPackageBeforeCompiling()
    {
        // 只有 node-a 的证书，而受管节点要求 node-a 与 node-b：缺一台会让那一台解不开包，必须早失败。
        WritePackage(sourceFileName: $"{ExamName}_1.tex");

        var result = await Pipeline(new TestCertificates([Recipient("node-a")])).RunAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Equal(ImportStage.Package, result.Stage);
        Assert.Equal("RecipientMissing", Assert.Single(result.Issues).Code);
        Assert.Contains("受管主备节点", Assert.Single(result.Issues).Message, StringComparison.Ordinal);
        AssertNoCompileOutput();
    }

    [Fact]
    public async Task RunAsync_WithRelativeOrMissingPaths_StopsAtValidateBeforeReadingMaterials()
    {
        // 相对路径与空目录在**读取材料之前**就被拒：不越界读，也不等到编译完才发现。
        WritePackage(sourceFileName: $"{ExamName}_1.tex");

        var relative = await Pipeline().RunAsync(Request() with { PackageRoot = "relative-package" });
        Assert.Equal(ImportStage.Validate, relative.Stage);
        Assert.Equal("PathInvalid", Assert.Single(relative.Issues).Code);
        AssertNoCompileOutput();

        var missingOutput = await Pipeline().RunAsync(Request() with { PackageOutputDirectory = "  " });
        Assert.Equal(ImportStage.Validate, missingOutput.Stage);
        Assert.Equal("PathInvalid", Assert.Single(missingOutput.Issues).Code);
        AssertNoCompileOutput();
    }

    [Fact]
    public async Task RunAsync_WithProductDirectoryEqualToMaterialDirectory_StopsAtValidate()
    {
        // 产物写回材料目录会把中间文件混进原始材料，必须在读材料之前拒绝。
        WritePackage(sourceFileName: $"{ExamName}_1.tex");

        var result = await Pipeline().RunAsync(Request() with { CompilationOutputDirectory = Materials });

        Assert.Equal(ImportStage.Validate, result.Stage);
        Assert.Equal("PathInvalid", Assert.Single(result.Issues).Code);
        AssertNoCompileOutput();
    }

    [Fact]
    public async Task RunAsync_RevisionWithoutDeclaredChangedQuestions_StopsAtValidateBeforeCompiling()
    {
        // 该考试已有已发布版本（paperVersion=1）：开考后修订必须逐题声明，
        // 漏填要在**证书检查与全量编译之前**就拒绝，不白跑一次整卷编译。
        WritePackage(sourceFileName: $"{ExamName}_1.tex");
        var versioning = new ReadOnlyVersioningService(paperVersion: 1);
        var pipeline = Build(new TestCertificates(CoveringRecipients()), versioning);

        var result = await pipeline.RunAsync(Request() with { ExpectedPaperVersion = 2 });

        Assert.Equal(ImportStage.Validate, result.Stage);
        Assert.Equal("ChangedQuestionsNotDeclared", Assert.Single(result.Issues).Code);
        Assert.Equal(1, versioning.Reads);
        AssertNoCompileOutput();
    }

    [Fact]
    public async Task RunAsync_RevisionDeclaringAQuestionOutsideTheManifest_IsRejected()
    {
        // 声明了本次材料里没有的题号：按清单核对，**在整卷编译之前**就拒绝，不白跑一次编译。
        WritePackage(sourceFileName: $"{ExamName}_1.tex");
        var pipeline = Build(new TestCertificates(CoveringRecipients()), new ReadOnlyVersioningService(paperVersion: 1));

        var result = await pipeline.RunAsync(Request() with { ExpectedPaperVersion = 2, DeclaredChangedQuestionIds = ["q-999"] });

        Assert.Equal(ImportStage.Validate, result.Stage);
        Assert.Equal("ChangedQuestionsUnknown", Assert.Single(result.Issues).Code);
        Assert.Contains("q-999", Assert.Single(result.Issues).Message, StringComparison.Ordinal);
        AssertNoCompileOutput();
    }

    [Fact]
    public async Task RunAsync_WithPackageOutputInsideTheMaterialDirectory_StopsAtValidateBeforeCompiling()
    {
        // 产物目录位于材料目录之内：会在材料目录里留下产物，读材料之前就拒绝。
        WritePackage(sourceFileName: $"{ExamName}_1.tex");

        var result = await Pipeline().RunAsync(Request() with
        {
            PackageOutputDirectory = Path.Combine(Materials, "out")
        });

        Assert.Equal(ImportStage.Validate, result.Stage);
        Assert.Equal("PathInvalid", Assert.Single(result.Issues).Code);
        AssertNoCompileOutput();
    }

    private void AssertNoCompileOutput() =>
        Assert.False(Directory.Exists(Compilation) && Directory.EnumerateFileSystemEntries(Compilation).Any());

    /// <summary>受管节点为 node-a 与 node-b；用例只走到发布之前，不需要真实数据库发布。</summary>
    private static readonly ManagedNodeEncryptionPolicy Policy =
        new(new HashSet<string>(StringComparer.Ordinal) { "node-a", "node-b" });

    /// <summary>默认给出一组覆盖受管节点的合成证书；校验阶段之前就停下的用例用得到它。</summary>
    private ImportPipeline Pipeline(IPublicationCertificateProvider? certificates = null)
        => Build(certificates ?? new TestCertificates(CoveringRecipients()));

    private static IReadOnlyList<NodeKeyRecipient> CoveringRecipients() =>
        [Recipient("node-a"), Recipient("node-b")];

    private ImportPipeline Build(IPublicationCertificateProvider certificates,
        IExamVersioningService? versioning = null)
    {
        var encryption = new PackageEncryptionService(4096, Policy);
        var resources = new PostgresVersionedResourceStore(fixture.DataSource, Policy);
        var publisher = new StudentPackagePublisher(encryption);
        var bundles = new PostgresBundlePublicationService(
            new PostgresExamVersioningService(fixture.DataSource),
            new PostgresVersionedResourceStore(fixture.DataSource, Policy),
            new UnusedKeyUnwrapper(),
            Policy);
        return new ImportPipeline(publisher, resources, versioning ?? new ReadOnlyVersioningService(0), bundles,
            certificates, Policy);
    }

    private ImportPipelineRequest Request() => new(
        Materials, ExamId, 1, Compilation, Packages,
        "synthetic-operator", "合成导入用例");

    /// <summary>合成节点公钥；用例只到发布之前，证书不参与真实加解密。</summary>
    private static NodeKeyRecipient Recipient(string nodeId)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=synthetic-import-node", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        return new NodeKeyRecipient(nodeId, request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1)));
    }

    private sealed class TestCertificates(IReadOnlyList<NodeKeyRecipient> recipients) : IPublicationCertificateProvider
    {
        public PublicationCertificateLease Acquire() => new(recipients);
    }

    private sealed class UnconfiguredCertificates : IPublicationCertificateProvider
    {
        public PublicationCertificateLease Acquire() =>
            throw new System.Security.Cryptography.CryptographicException("尚未配置发布公钥证书。");
    }

    private sealed class UnusedKeyUnwrapper : IPackageKeyUnwrapper
    {
        public byte[] Unwrap(EncryptedPackageEnvelope envelope) => throw new NotSupportedException();
    }

    private void WritePackage(string sourceFileName, bool withAnswerKey = true)
    {
        Directory.CreateDirectory(Path.Combine(Materials, "questions"));
        File.WriteAllText(Path.Combine(Materials, "manifest.json"), $$"""
            {
              "schemaVersion": 1,
              "examId": "{{ExamId}}",
              "examName": "{{ExamName}}",
              "subjects": [{ "subjectId": "s", "name": "科目", "maxScore": "1" }],
              "groups": [{ "groupId": "g", "subjectId": "s", "name": "单选", "type": "single", "order": 1,
                "questionCount": 1, "maxScore": "1",
                "scoring": { "oneCorrect": { "correct": "1", "blank": "0", "wrong": "0" } } }],
              "questions": [{ "questionId": "q-001", "questionNo": 1, "subjectId": "s", "groupId": "g",
                "source": "questions/{{sourceFileName}}", "options": ["A", "B"] }],
              "resourceRoots": []
            }
            """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(Materials, "questions", sourceFileName),
            "\\documentclass{article}\\begin{document}合成题 A B\\end{document}", new UTF8Encoding(false));
        if (!withAnswerKey) return;

        Directory.CreateDirectory(Path.Combine(Materials, "server-only"));
        File.WriteAllText(Path.Combine(Materials, "server-only", "answer-key.json"), $$"""
            {"schemaVersion":1,"examId":"{{ExamId}}","answers":[{"questionId":"q-001","questionNo":1,"correctOptionIds":["A"]}]}
            """, new UTF8Encoding(false));
    }

    /// <summary>
    /// 本组用例都在发布之前停下：只允许读考试状态（用来判定首次发布还是开考后修订），
    /// 任何**写入**都必须没有被调用——用它钉住这一事实。
    /// </summary>
    private sealed class ReadOnlyVersioningService(long paperVersion) : IExamVersioningService
    {
        public long Reads { get; private set; }

        public VersioningResult<ExamVersionSnapshot> GetExam(string examId)
        {
            Reads++;
            return VersioningResult<ExamVersionSnapshot>.Success(new ExamVersionSnapshot(
                examId,
                new VersionVector(1, paperVersion, 0),
                new ScheduleWindow(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(2)),
                ExamLifecycle.Scheduled,
                null, null, null, null,
                new Dictionary<string, ParticipantVersionStatus>(StringComparer.Ordinal)));
        }

        public VersioningResult<ExamVersionSnapshot> CreateExam(CreateVersionedExamRequest request) => throw new NotSupportedException();
        public VersioningResult<ExamVersionSnapshot> UpdateSchedule(UpdateScheduleRequest request) => throw new NotSupportedException();
        public VersioningResult<ScheduleTriggerResult> ExecuteScheduleTrigger(ExecuteScheduleTriggerRequest request) => throw new NotSupportedException();
        public VersioningResult<ExamVersionSnapshot> PublishPaper(PublishPaperRequest request) => throw new NotSupportedException();
        public VersioningResult<ExamVersionSnapshot> PublishGradingRules(PublishGradingRulesRequest request) => throw new NotSupportedException();
        public VersioningResult<ExamVersionSnapshot> RegisterParticipant(RegisterVersionedParticipantRequest request) => throw new NotSupportedException();
        public VersioningResult<ExamVersionSnapshot> ChangeParticipantState(ChangeParticipantStateRequest request) => throw new NotSupportedException();
        public VersioningResult<ExamVersionSnapshot> ReportPaperTransfer(ReportPaperTransferRequest request) => throw new NotSupportedException();
        public VersioningResult<ExamVersionSnapshot> ConfirmPaperReview(ConfirmPaperReviewRequest request) => throw new NotSupportedException();
    }
}
