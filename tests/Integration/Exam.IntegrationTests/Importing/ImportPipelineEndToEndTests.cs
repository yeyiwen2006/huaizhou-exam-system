using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Domain.Importing;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Postgres.Administration;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.Infrastructure.Security;
using Exam.IntegrationTests.Postgres;

namespace Exam.IntegrationTests.Importing;

/// <summary>
/// C05 导入流水线的端到端用例：**真编译**带标记的合成题 → 组包加密 → 版本资源入库 →
/// A06 **原子整体发布**（试卷版本与计分规则同一事务生效）。
///
/// 它要同时用 XeLaTeX 与 PostgreSQL，因此标记 Category=Latex：CI 的 core/wpf/postgres 三个作业都不跑该类别。
/// 本机执行：`EXAM_TEST_POSTGRES=... dotnet test --filter Category=Latex`。
/// 夹具全部为合成内容，未使用真实试卷材料。
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Latex")]
public sealed class ImportPipelineEndToEndTests(PostgresDatabaseFixture fixture) : IDisposable
{
    private const string ExamIdPrefix = "synthetic-import-e2e";
    // 考试名称必须与题目源文件名前缀一致（D01 规则）：合成选项题_1.tex → 合成选项题。
    private const string ExamName = "合成选项题";
    private const string QuestionId = "q-001";
    private const string SourceRelativePath = "questions/" + ExamName + "_1.tex";

    private static readonly string[] OptionIds = ["A", "B", "C", "D"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "import-e2e-" + Guid.NewGuid().ToString("N"));

    /// <summary>每个用例一场新考试：同一个测试类共享数据库，考试标识不能复用。</summary>
    private readonly string _examId = ExamIdPrefix + "-" + Guid.NewGuid().ToString("N")[..8];

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

        _node.Dispose();
    }

    [Fact]
    public async Task RunAsync_FirstImport_PublishesPaperAndGradingRulesTogether()
    {
        var exam = await CreateExamAsync();

        var result = await Pipeline(exam).RunAsync(Request(targetVersion: 1));

        Assert.True(result.Succeeded, Describe(result));
        Assert.Equal(ImportStage.Completed, result.Stage);
        Assert.Equal(1, result.PublishedPaperVersion);

        // 关键：试卷版本与**计分规则版本**在同一次发布里一起前进。
        // 只发试卷不发布答案规则时，这场考试无法计分——这正是本提交修掉的问题。
        var snapshot = Assert.IsType<ExamVersionSnapshot>(exam.GetExam(_examId).Value);
        Assert.Equal(1, snapshot.Versions.PaperVersion);
        Assert.Equal(1, snapshot.Versions.GradingVersion);
        Assert.NotNull(snapshot.Grading);
        // 首次发布：整卷都是新题，学生被告知需要复核的正是这些题。
        Assert.Equal([QuestionId], Assert.IsType<PaperPublicationInfo>(snapshot.Paper).ChangedQuestionIds);

        // 加密包按约定的标识与种类入库，A06 的整体发布正是按该标识解密核对后发布。
        var resource = await ResourceStore().FindByIdAsync(result.ResourceId!);
        Assert.NotNull(resource);
        Assert.Equal("paper-package", resource.ResourceKind);
        Assert.Equal(1, resource.Version);
    }

    [Fact]
    public async Task RunAsync_RevisionWithoutDeclaredChanges_IsRejectedAndPublishesNothing()
    {
        var exam = await CreateExamAsync();
        var first = await Pipeline(exam).RunAsync(Request(targetVersion: 1));
        Assert.True(first.Succeeded, Describe(first));

        // 已有已发布版本时开考后修订必须逐题声明：不声明直接拒绝，版本一点不动。
        var result = await Pipeline(exam).RunAsync(Request(targetVersion: 2));

        Assert.False(result.Succeeded);
        Assert.Equal(ImportStage.Validate, result.Stage);
        Assert.Equal("ChangedQuestionsNotDeclared", Assert.Single(result.Issues).Code);
        var snapshot = Assert.IsType<ExamVersionSnapshot>(exam.GetExam(_examId).Value);
        Assert.Equal(1, snapshot.Versions.PaperVersion);
        Assert.Equal(1, snapshot.Versions.GradingVersion);
    }

    [Fact]
    public async Task RunAsync_RevisionDeclaringTheChangedQuestion_PublishesExactlyThatQuestion()
    {
        var exam = await CreateExamAsync();
        var first = await Pipeline(exam).RunAsync(Request(targetVersion: 1));
        Assert.True(first.Succeeded, Describe(first));

        var revision = await Pipeline(exam).RunAsync(Request(targetVersion: 2, declared: [QuestionId]));

        Assert.True(revision.Succeeded, Describe(revision));
        Assert.Equal(2, revision.PublishedPaperVersion);
        var snapshot = Assert.IsType<ExamVersionSnapshot>(exam.GetExam(_examId).Value);
        Assert.Equal(2, snapshot.Versions.PaperVersion);
        Assert.Equal(2, snapshot.Versions.GradingVersion);
        // 学生只会被告知复核**声明的这一题**，不是整卷。
        Assert.Equal([QuestionId], Assert.IsType<PaperPublicationInfo>(snapshot.Paper).ChangedQuestionIds);

        // 拿已经过期的目标版本再发一次：早判定就拒绝（版本必须前进），不会把版本覆盖回去，
        // 也不会撞上「同名加密产物已存在」这种看不出原因的失败。
        var stale = await Pipeline(exam).RunAsync(Request(targetVersion: 2, declared: [QuestionId]));
        Assert.False(stale.Succeeded);
        Assert.Equal(ImportStage.Validate, stale.Stage);
        Assert.Equal("VersionConflict", Assert.Single(stale.Issues).Code);
        var after = Assert.IsType<ExamVersionSnapshot>(exam.GetExam(_examId).Value);
        Assert.Equal(2, after.Versions.PaperVersion);
        Assert.Equal(2, after.Versions.GradingVersion);
    }

    private async Task<PostgresExamVersioningService> CreateExamAsync()
    {
        var versions = new PostgresExamVersioningService(fixture.DataSource);
        var now = DateTimeOffset.UtcNow;
        var created = versions.CreateExam(new CreateVersionedExamRequest(
            _examId, new ScheduleWindow(now.AddHours(1), now.AddHours(2)), now, Id()));
        Assert.True(created.Succeeded, created.Error);
        return versions;
    }

    private ImportPipeline Pipeline(PostgresExamVersioningService versions)
    {
        WritePackage();
        var resources = ResourceStore();
        return new ImportPipeline(
            new StudentPackagePublisher(new PackageEncryptionService(4096, Policy)),
            resources,
            versions,
            new PostgresBundlePublicationService(versions, resources, new TestUnwrapper(CloneNode), Policy,
                new TestCertificates(CloneNode)),
            new TestCertificates(CloneNode),
            Policy);
    }

    private PostgresVersionedResourceStore ResourceStore() => new(fixture.DataSource, Policy);

    private ImportPipelineRequest Request(long targetVersion, IReadOnlyList<string>? declared = null) => new(
        Path.Combine(_root, "package"),
        _examId,
        targetVersion,
        Path.Combine(_root, "compiled"),
        Path.Combine(_root, "published"),
        "synthetic-operator",
        "合成端到端用例",
        Id(),
        declared);

    private static string Id() => Guid.NewGuid().ToString("N");

    private static string Describe(ImportPipelineResult result) =>
        string.Join("；", result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));

    private static readonly ManagedNodeEncryptionPolicy Policy =
        new(new HashSet<string>(StringComparer.Ordinal) { "node-a" });

    // 受管节点只有 node-a。证书租约在释放时会释放证书对象，所以每次租用都发一份
    // **同一张证书**的独立副本：DER 相同、指纹一致，解锁时才不会被判定为「证书不一致」。
    private readonly X509Certificate2 _node = CreateNode();

    private X509Certificate2 CloneNode() =>
        X509CertificateLoader.LoadPkcs12(_node.Export(X509ContentType.Pkcs12), null);

    private static X509Certificate2 CreateNode()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=synthetic-import-node", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class TestCertificates(Func<X509Certificate2> create) : IPublicationCertificateProvider
    {
        public PublicationCertificateLease Acquire() => new([new NodeKeyRecipient("node-a", create())]);
    }

    private sealed class TestUnwrapper(Func<X509Certificate2> create) : IPackageKeyUnwrapper
    {
        public byte[] Unwrap(EncryptedPackageEnvelope envelope) =>
            PackageEncryptionService.UnwrapDataKey(envelope, "node-a", create());
    }

    private void WritePackage()
    {
        var packageRoot = Path.Combine(_root, "package");
        Directory.CreateDirectory(Path.Combine(packageRoot, "questions"));
        Directory.CreateDirectory(Path.Combine(packageRoot, "server-only"));
        File.WriteAllText(Path.Combine(packageRoot, "manifest.json"),
            JsonSerializer.Serialize(Manifest(), ImportJson.Create()), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(packageRoot, "server-only", "answer-key.json"), $$"""
            {"schemaVersion":1,"examId":"{{_examId}}","answers":[{"questionId":"{{QuestionId}}","questionNo":1,"correctOptionIds":["A"]}]}
            """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(packageRoot, SourceRelativePath), """
            \documentclass[UTF8]{ctexart}
            \pagestyle{empty}
            \usepackage{hyperref}
            \hypersetup{hidelinks}
            \DeclareRobustCommand{\optmark}[3]{\hypertarget{opt:#1:#2}{}\hyperlink{opt:#1:#2}{#3}}
            \begin{document}
            合成题：下列选项正确的是（\quad）。

            \begin{enumerate}
              \item[\optmark{q-001}{A}{A.}] 甲
              \item[\optmark{q-001}{B}{B.}] 乙
              \item[\optmark{q-001}{C}{C.}] 丙
              \item[\optmark{q-001}{D}{D.}] 丁
            \end{enumerate}
            \end{document}
            """, new UTF8Encoding(false));
    }

    private ExamManifest Manifest() => new(1, _examId, ExamName,
        [new("subject", "合成科目", "1")],
        [new("group", "subject", "单项选择", "single", 1, 1, false, "1",
            new(new("1", "0", "0"), null))],
        [new(QuestionId, 1, "subject", "group", SourceRelativePath, OptionIds, null)], []);
}
