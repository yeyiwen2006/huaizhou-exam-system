using System.Text;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Postgres.Resources;
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
        Directory.CreateDirectory(_root);

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
    public async Task RunAsync_WithoutRecipients_StopsAtPackageBeforeCompiling()
    {
        // 没有可用收件人就不必编译：阶段报 Package，且不产生任何产物目录内容。
        WritePackage(sourceFileName: $"{ExamName}_1.tex");

        var result = await Pipeline().RunAsync(Request(recipients: []));

        Assert.False(result.Succeeded);
        Assert.Equal(ImportStage.Package, result.Stage);
        Assert.Equal("RecipientMissing", Assert.Single(result.Issues).Code);
        Assert.False(Directory.Exists(Path.Combine(_root, "compiled")) &&
            Directory.EnumerateFileSystemEntries(Path.Combine(_root, "compiled")).Any());
    }

    private ImportPipeline Pipeline()
    {
        var encryption = new PackageEncryptionService(4096);
        var resources = new PostgresVersionedResourceStore(fixture.DataSource);
        var publisher = new StudentPackagePublisher(encryption);
        return new ImportPipeline(publisher, resources, new UnusedVersioningService());
    }

    private ImportPipelineRequest Request(IReadOnlyList<PackageRecipient>? recipients = null) => new(
        _root, ExamId, 1,
        Path.Combine(_root, "compiled"), Path.Combine(_root, "package"),
        recipients ?? [Recipient()], "synthetic-operator", "合成导入用例");

    /// <summary>给流水线一个可用的加密收件人，使用例能走到收件人检查之后的阶段。</summary>
    private static PackageRecipient Recipient()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=synthetic-import-node", rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        return new PackageRecipient("node-a", certificate.Export(
            System.Security.Cryptography.X509Certificates.X509ContentType.Cert));
    }

    private void WritePackage(string sourceFileName, bool withAnswerKey = true)
    {
        Directory.CreateDirectory(Path.Combine(_root, "questions"));
        File.WriteAllText(Path.Combine(_root, "manifest.json"), $$"""
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
        File.WriteAllText(Path.Combine(_root, "questions", sourceFileName),
            "\\documentclass{article}\\begin{document}合成题 A B\\end{document}", new UTF8Encoding(false));
        if (!withAnswerKey) return;

        Directory.CreateDirectory(Path.Combine(_root, "server-only"));
        File.WriteAllText(Path.Combine(_root, "server-only", "answer-key.json"), $$"""
            {"schemaVersion":1,"examId":"{{ExamId}}","answers":[{"questionId":"q-001","questionNo":1,"correctOptionIds":["A"]}]}
            """, new UTF8Encoding(false));
    }

    /// <summary>本组用例都在发布之前停下，版本服务不会被调用；用它钉住这一事实。</summary>
    private sealed class UnusedVersioningService : IExamVersioningService
    {
        public VersioningResult<ExamVersionSnapshot> CreateExam(CreateVersionedExamRequest request) => throw new NotSupportedException();
        public VersioningResult<ExamVersionSnapshot> GetExam(string examId) => throw new NotSupportedException();
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
