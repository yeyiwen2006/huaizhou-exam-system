using System.Text;
using System.Text.Json;
using Exam.Application.Scoring;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Infrastructure.Packaging;
using Exam.Infrastructure.Postgres.Scoring;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.Infrastructure.Security;
using Exam.Domain.Importing;
using Exam.Domain.Scoring;
using Exam.IntegrationTests.Postgres;

namespace Exam.IntegrationTests.Importing;

/// <summary>
/// E01-C49 规则修订入口的 PostgreSQL 集成用例。这条入口只读材料包里的清单与答案、
/// 发布计分规则版本并按新版本触发重算，**不编译也不组包**，因此不需要 TeX。
///
/// 它守住三件事：规则版本必须前进、规则发布与重算完成分开报告、缺答案或考试对不上直接拒绝。
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class GradingRuleRevisionTests(PostgresDatabaseFixture fixture) : IDisposable
{
    private const string ExamName = "合成规则考试";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rule-revision-" + Guid.NewGuid().ToString("N"));
    private readonly string _examId = "synthetic-rule-revision-" + Guid.NewGuid().ToString("N")[..8];

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
    public async Task ReviseAsync_FirstRules_PublishesGradingVersionAndReportsTheRecalculationScope()
    {
        var exam = await CreateExamAsync();
        WritePackage();
        // 答案必须对应一份已发布的试卷：先发试卷（不需要 TeX），再改规则。
        await PublishPaper(exam);

        var result = await Service(exam).ReviseAsync(_examId, Request(), "synthetic-operator");

        Assert.True(result.Succeeded, Describe(result));
        Assert.Equal(1, result.PublishedGradingVersion);
        // 规则已发布：那是**规则**版本前进，不需要也不应该顺带换试卷版本。
        var snapshot = Assert.IsType<ExamVersionSnapshot>(exam.GetExam(_examId).Value);
        Assert.Equal(1, snapshot.Versions.GradingVersion);
        // 只改规则：试卷版本保持不动。
        Assert.Equal(1, snapshot.Versions.PaperVersion);
        Assert.NotNull(snapshot.Grading);
        // 重算范围与就绪单独报告，不把「规则已换」说成「成绩已重算完」。
        Assert.Null(result.RecalculationError);
        Assert.NotNull(result.Readiness);
        Assert.Equal(1, result.Readiness!.TargetVersion);
        Assert.Equal(0, result.SubmissionCount);
    }

    [Fact]
    public async Task ReviseAsync_WithAStaleTargetVersion_IsRejectedAndKeepsThePublishedRules()
    {
        var exam = await CreateExamAsync();
        WritePackage();
        await PublishPaper(exam);
        Assert.True((await Service(exam).ReviseAsync(_examId, Request(), "synthetic-operator")).Succeeded);

        // 目标版本没有前进：拒绝，已有规则保持不变（不降版、不覆盖）。
        var stale = await Service(exam).ReviseAsync(_examId, Request() with { GradingVersion = 1 }, "synthetic-operator");

        Assert.False(stale.Succeeded);
        Assert.Equal("VersionConflict", Assert.Single(stale.Issues).Code);
        Assert.Null(stale.PublishedGradingVersion);
        Assert.Equal(1, exam.GetExam(_examId).Value!.Versions.GradingVersion);
    }

    [Fact]
    public async Task ReviseAsync_BeforeAPaperIsPublished_IsRejectedWithAReasonInsteadOfAStructureMismatch()
    {
        // 尚无已发布试卷：答案没有可对应的试卷，明确报出原因，而不是让 A06 以「结构不一致」收场。
        var exam = await CreateExamAsync();
        WritePackage();

        var result = await Service(exam).ReviseAsync(_examId, Request(), "synthetic-operator");

        Assert.False(result.Succeeded);
        Assert.Equal("PaperNotPublished", Assert.Single(result.Issues).Code);
        Assert.Equal(0, exam.GetExam(_examId).Value!.Versions.GradingVersion);
    }

    [Fact]
    public async Task ReviseAsync_WithoutACompleteAnswerKey_IsRejectedAndPublishesNothing()
    {
        var exam = await CreateExamAsync();
        WritePackage(withAnswerKey: false);

        var result = await Service(exam).ReviseAsync(_examId, Request(), "synthetic-operator");

        Assert.False(result.Succeeded);
        Assert.Equal("AnswerKeyIncomplete", Assert.Single(result.Issues).Code);
        Assert.Equal(0, exam.GetExam(_examId).Value!.Versions.GradingVersion);
    }

    [Fact]
    public async Task ReviseAsync_WithAManifestForAnotherExam_IsRejected()
    {
        var exam = await CreateExamAsync();
        WritePackage(manifestExamId: _examId + "-other");

        var result = await Service(exam).ReviseAsync(_examId, Request(), "synthetic-operator");

        Assert.False(result.Succeeded);
        Assert.Equal("ExamMismatch", Assert.Single(result.Issues).Code);
        Assert.Equal(0, exam.GetExam(_examId).Value!.Versions.GradingVersion);
    }

    [Fact]
    public async Task ReviseAsync_WithAnInvalidRequest_IsRejectedWithoutTouchingTheExam()
    {
        var exam = await CreateExamAsync();
        WritePackage();

        var noReason = await Service(exam).ReviseAsync(_examId, Request() with { Reason = " " }, "synthetic-operator");
        Assert.Equal("InvalidRequest", Assert.Single(noReason.Issues).Code);

        var relative = await Service(exam).ReviseAsync(_examId, Request() with { PackageRoot = "relative" }, "synthetic-operator");
        Assert.Equal("InvalidRequest", Assert.Single(relative.Issues).Code);

        var missing = await Service(exam).ReviseAsync(_examId, Request() with { PackageRoot = Path.Combine(_root, "nope") }, "synthetic-operator");
        Assert.Equal("MaterialMissing", Assert.Single(missing.Issues).Code);

        Assert.Equal(0, exam.GetExam(_examId).Value!.Versions.GradingVersion);
    }

    private async Task<PostgresExamVersioningService> CreateExamAsync()
    {
        var versions = new PostgresExamVersioningService(fixture.DataSource);
        var now = DateTimeOffset.UtcNow;
        var created = versions.CreateExam(new CreateVersionedExamRequest(
            _examId, new ScheduleWindow(now.AddHours(1), now.AddHours(2)), now, Guid.NewGuid().ToString("N")));
        Assert.True(created.Succeeded, created.Error);
        return versions;
    }

    private GradingRuleRevisionService Service(PostgresExamVersioningService versions) => new(
        versions,
        new RecalculationScheduler(new PostgresGradingScope(fixture.DataSource),
            new PostgresRecalculationQueue(fixture.DataSource)));

    /// <summary>
    /// 预置一份已发布试卷：只改规则需要它，构造试卷结构不需要 TeX。
    /// A06 要求试卷版本发布前加密包已**持久入库**，所以先放一份占位资源（本组用例不校验密文内容）。
    /// </summary>
    private async Task PublishPaper(PostgresExamVersioningService versions)
    {
        var artifactId = "synthetic-paper-" + _examId;
        await new PostgresVersionedResourceStore(fixture.DataSource).SaveAsync(new VersionedEncryptedResource(
            artifactId, _examId, "paper-package", 1, [1, 2, 3],
            new EncryptedPackageEnvelope(artifactId, 1, 4096, "AAAAAAAA",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["node-a"] = "synthetic" },
                new Dictionary<string, string>(StringComparer.Ordinal) { ["node-a"] = "synthetic" }),
            DateTimeOffset.UtcNow, "synthetic-operator"));

        var manifest = JsonSerializer.Deserialize<ExamManifest>(
            File.ReadAllText(Path.Combine(_root, "package", "manifest.json")), ImportJson.Create())!;
        var structure = ImportedGradingRules.BuildStructure(manifest);
        var published = versions.PublishPaper(new PublishPaperRequest(
            _examId, 0, artifactId, structure,
            structure.Questions.Select(question => question.QuestionId).ToList(),
            new PaperPublicationValidation(true, true, true, true),
            "synthetic-operator", "合成预置试卷", DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N")));
        Assert.True(published.Succeeded, published.Error);
    }

    private GradingRuleRevisionRequest Request() => new(
        Path.Combine(_root, "package"), 0, "合成规则修订");

    private static string Describe(GradingRuleRevisionResult result) =>
        string.Join("；", result.Issues.Select(issue => $"{issue.Code}: {issue.Message}"));

    private void WritePackage(bool withAnswerKey = true, string? manifestExamId = null)
    {
        var packageRoot = Path.Combine(_root, "package");
        Directory.CreateDirectory(Path.Combine(packageRoot, "server-only"));
        File.WriteAllText(Path.Combine(packageRoot, "manifest.json"), $$"""
            {
              "schemaVersion": 1,
              "examId": "{{manifestExamId ?? _examId}}",
              "examName": "{{ExamName}}",
              "subjects": [{ "subjectId": "s", "name": "科目", "maxScore": "1" }],
              "groups": [{ "groupId": "g", "subjectId": "s", "name": "单选", "type": "single", "order": 1,
                "questionCount": 1, "maxScore": "1",
                "scoring": { "oneCorrect": { "correct": "1", "blank": "0", "wrong": "0" } } }],
              "questions": [{ "questionId": "q-001", "questionNo": 1, "subjectId": "s", "groupId": "g",
                "source": "questions/{{ExamName}}_1.tex", "options": ["A", "B"] }],
              "resourceRoots": []
            }
            """, new UTF8Encoding(false));
        if (withAnswerKey)
        {
            File.WriteAllText(Path.Combine(packageRoot, "server-only", "answer-key.json"), $$"""
                {"schemaVersion":1,"examId":"{{manifestExamId ?? _examId}}","answers":[{"questionId":"q-001","questionNo":1,"correctOptionIds":["A"]}]}
                """, new UTF8Encoding(false));
        }
    }
}
