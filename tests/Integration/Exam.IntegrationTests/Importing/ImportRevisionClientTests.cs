using System.Text;
using System.Text.Json;

using Exam.Contracts.Exams;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;
using Exam.Domain.Importing;
using Exam.Domain.Scoring;
using Exam.Infrastructure.Packaging;

namespace Exam.IntegrationTests.Importing;

/// <summary>
/// 开考后修订的**客户端**流程：模式判定、按上一版本归档算出的差异提示、声明集与原因，
/// 以及它们在「丢回执重试 / 跨进程重放」下的稳定性。不需要 TeX（用合成编译）。
/// </summary>
public sealed class ImportRevisionClientTests
{
    private static ImportPipeline Pipeline(SyntheticPublicationClient api) => new(api)
    { CompileOverride = SyntheticImportFixture.CompileFixtureAsync };

    [Fact]
    public async Task Revision_ComputesTheHintFromThePreviousArchiveAndDeclaresItByDefault()
    {
        using var source = new SyntheticImportFixture();
        using var client = RevisionClient(source, questionText: "合成题：上一版的说法。");
        using var prepared = await Pipeline(client).PrepareAsync(source.Source, client.Current.ExamId,
            CancellationToken.None, "合成修订：改第一题题干");

        Assert.True(prepared.IsRevision);
        Assert.Equal(["q1"], prepared.ChangedQuestionsHint);
        Assert.Equal(["q1"], prepared.DeclaredChangedQuestions);
        Assert.Equal("合成修订：改第一题题干", prepared.Reason);
        Assert.Equal(2, prepared.TargetPaperVersion);

        await Pipeline(client).PublishAsync(prepared);

        var request = Assert.Single(client.Requests);
        // 修订只把**声明的**这一题下发；首发才整卷。
        Assert.Equal(["q1"], request.Publication.ChangedQuestionIds);
        Assert.Equal("合成修订：改第一题题干", request.Publication.Reason);
    }

    [Fact]
    public async Task FirstPublication_StillDeclaresEveryQuestionWithTheFixedReason()
    {
        using var source = new SyntheticImportFixture();
        using var client = new SyntheticPublicationClient(source);
        using var prepared = await Pipeline(client).PrepareAsync(source.Source, client.Current.ExamId);

        Assert.False(prepared.IsRevision);
        Assert.Equal(["q1"], prepared.DeclaredChangedQuestions);
        Assert.Equal(ImportPipeline.FirstImportReason, prepared.Reason);
        Assert.Empty(prepared.ChangedQuestionsHint);

        await Pipeline(client).PublishAsync(prepared);
        Assert.Equal(["q1"], Assert.Single(client.Requests).Publication.ChangedQuestionIds);
    }

    [Fact]
    public async Task Revision_WithNoContentChange_FailsBeforeUploadingAndPointsToTheRulesEntry()
    {
        using var source = new SyntheticImportFixture();
        // 上一版归档与本次材料逐字相同：没有内容差异，不该推出新试卷版本。
        using var client = RevisionClient(source, questionText: null);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Pipeline(client).PrepareAsync(source.Source, client.Current.ExamId, CancellationToken.None, "合成修订"));

        Assert.Contains("没有内容差异", error.Message, StringComparison.Ordinal);
        Assert.Contains("规则修订入口", error.Message, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Revision_WithAnUnknownDeclaredQuestion_FailsBeforeCompiling()
    {
        using var source = new SyntheticImportFixture();
        using var client = RevisionClient(source, questionText: "合成题：上一版的说法。");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Pipeline(client).PrepareAsync(source.Source, client.Current.ExamId, CancellationToken.None,
                "合成修订", ["q9"]));

        Assert.Contains("不在本次材料清单中", error.Message, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Revision_WithoutAReason_IsRejected()
    {
        using var source = new SyntheticImportFixture();
        using var client = RevisionClient(source, questionText: "合成题：上一版的说法。");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Pipeline(client).PrepareAsync(source.Source, client.Current.ExamId, CancellationToken.None));

        Assert.Contains("修改原因", error.Message, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Revision_WhenTheServerPublishedAnotherVersionFirst_IsRejectedAtPublish()
    {
        using var source = new SyntheticImportFixture();
        using var client = RevisionClient(source, questionText: "合成题：上一版的说法。");
        using var prepared = await Pipeline(client).PrepareAsync(source.Source, client.Current.ExamId,
            CancellationToken.None, "合成修订");

        // 检查之后服务器又推进了一版：不能拿旧预览去发布修订。
        client.Current = client.Current with
        {
            Versions = client.Current.Versions with { PaperVersion = 2, GradingVersion = 2 }
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Pipeline(client).PublishAsync(prepared));

        Assert.Contains("试卷版本已变化", error.Message, StringComparison.Ordinal);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Revision_DeclarationAndReason_SurviveExportAndCrossProcessLoad()
    {
        using var source = new SyntheticImportFixture();
        using var client = RevisionClient(source, questionText: "合成题：上一版的说法。");
        using var prepared = await Pipeline(client).PrepareAsync(source.Source, client.Current.ExamId,
            CancellationToken.None, "合成修订：改第一题题干", ["q1"]);
        var directory = Path.Combine(source.Root, "reviewed");
        await prepared.ExportAsync(directory);

        using var loaded = await PreparedExamImport.LoadAsync(directory, client);

        Assert.True(loaded.IsRevision);
        Assert.Equal(["q1"], loaded.DeclaredChangedQuestions);
        Assert.Equal("合成修订：改第一题题干", loaded.Reason);
        await Pipeline(client).PublishAsync(loaded);
        var request = Assert.Single(client.Requests);
        Assert.Equal(["q1"], request.Publication.ChangedQuestionIds);
        Assert.Equal("合成修订：改第一题题干", request.Publication.Reason);
    }

    /// <summary>
    /// 合成一个「已有已发布试卷」的客户端，并把上一版本的归档交给它：
    /// 归档里是**上一版清单与该题的上一版源文**，客户端据此算出差异提示。
    /// </summary>
    private static SyntheticPublicationClient RevisionClient(SyntheticImportFixture source, string? questionText)
    {
        var client = new SyntheticPublicationClient(source);
        // 上一版归档里的清单是**已绑定**的那一份：客户端在检查时会把考试标识与科目/题组标识绑定到所选考试，
        // 归档记录的正是绑定后的清单，否则逐题摘要会因为结构字段不同而误判为「变了」。
        var (boundManifest, boundAnswers) = BindToExam(source, client.Current.ExamId);
        var rules = ImportedGradingRules.Create(boundManifest, boundAnswers);
        var now = DateTimeOffset.UtcNow;
        client.Current = client.Current with
        {
            Versions = new VersionVector(1, 1, 1),
            Paper = new PaperPublicationInfo(1, "synthetic-artifact-1", rules.Structure, ["q1"], now,
                "synthetic-operator", "首次导入并发布整套试卷"),
            Grading = new GradingPublicationInfo(1, "bundle-rules-synthetic-artifact-1", now,
                "synthetic-operator", "首次导入并发布整套试卷")
        };
        client.PreviousMaterialsArchive = PreviousArchive(source, boundManifest, boundAnswers,
            questionText ?? File.ReadAllText(Path.Combine(source.Source, "questions", source.Name + "_1.tex")));
        return client;
    }

    /// <summary>按客户端检查时的同一顺序绑定考试标识与目录设置，得到归档里应有的清单与答案。</summary>
    private static (string Manifest, string Answers) BindToExam(SyntheticImportFixture source, string examId)
    {
        var (manifest, answers) = ImportPipeline.BindPackageToSelectedExam(
            File.ReadAllText(source.ManifestPath), File.ReadAllText(source.AnswerPath), examId, source.Name);
        return (ImportCatalogBinding.Bind(manifest, answers, source.Settings), answers);
    }

    private static byte[] PreviousArchive(SyntheticImportFixture source, string manifestJson, string answerJson,
        string questionText) =>
        PaperMaterialsArchive.Build(source.Manifest.ExamId, 1,
        [
            new("manifest.json", Encoding.UTF8.GetBytes(manifestJson)),
            new("server-only/answer-key.json", Encoding.UTF8.GetBytes(answerJson)),
            new($"questions/{source.Name}_1.tex", Encoding.UTF8.GetBytes(questionText))
        ]);
}
