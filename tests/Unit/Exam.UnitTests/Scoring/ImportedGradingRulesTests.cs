using System.Text.Json.Nodes;
using Exam.Application.Submissions;
using Exam.Application.Versioning;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Domain.Scoring;
using Exam.Domain.Submissions;
using Exam.UnitTests.Importing;

namespace Exam.UnitTests.Scoring;

public sealed class ImportedGradingRulesTests
{
    private static string Manifest => TestRepositoryPaths.ReadText("samples", "synthetic", "three-types", "manifest.json");
    private static string Key => TestRepositoryPaths.ReadText("samples", "synthetic", "three-types", "server-only", "answer-key.json");

    [Fact]
    public async Task ThreeTypes_KeepQuestionIdentityAndAbstainPolicy_ThroughReceiptAndGrading()
    {
        var rules = ImportedGradingRules.Create(Manifest, Key);
        Assert.Equal(9, rules.Questions.Count);
        Assert.True(rules.Structure.Questions.Single(q => q.QuestionId == "q-b-006").AllowAbstain);
        Assert.False(rules.Structure.Questions[0].AllowAbstain);
        var answers = rules.Questions.ToDictionary(q => q.QuestionId, q => AnswerValue.Selected(q.CorrectOptionIds.ToArray()));
        var store = new InMemorySubmissionStore();
        await store.SetAuthorizationStateAsync(new(rules.ExamId, "candidate", "participation", 1, "device", 1,
            ParticipationBusinessState.Answering, 1, 3));
        using var service = new SubmissionService(store, new InMemoryGradingTaskQueue(), paperReader: _ => rules.Structure);
        var request = new SubmitFinalAnswersRequest("request", "submission", rules.ExamId, "candidate", "participation", 1, "device", 1, 1, answers);
        Assert.True((await service.SubmitAsync(request)).IsSuccess);
        var submission = (await store.FindSubmissionAsync(rules.ExamId, "candidate"))!;
        var result = rules.Grade(submission, 3);
        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal("37", result.Grade!.ExactTotalScore);
        Assert.False(rules.Grade(submission with { ExamId = "wrong-exam" }, 3).Succeeded);
        Assert.False(rules.Grade(submission with { PaperVersion = 2 }, 3).Succeeded);
        var replay = await service.SubmitAsync(request with { FrozenAnswers = new Dictionary<string, AnswerValue>() });
        Assert.True(replay.IsReplay);
        Assert.Equal(submission.Receipt, replay.Value);
        Assert.Equal(9, (await store.FindSubmissionAsync(rules.ExamId, "candidate"))!.FrozenAnswers.Count);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("abstain")]
    [InlineData("single-multiple")]
    [InlineData("kind")]
    public async Task NewMalformedSnapshotsAreRejectedBeforePersistence(string defect)
    {
        var rules = ImportedGradingRules.Create(Manifest, Key);
        var answers = rules.Questions.ToDictionary(q => q.QuestionId, _ => AnswerValue.Empty());
        var id = rules.Questions[0].QuestionId;
        switch (defect)
        {
            case "missing": answers.Remove(id); break;
            case "unknown": answers[id] = AnswerValue.Selected("Z"); break;
            case "duplicate": answers[id] = AnswerValue.Selected("A", "A"); break;
            case "abstain": answers[id] = AnswerValue.Abstain(); break;
            case "single-multiple": answers[id] = AnswerValue.Selected("A", "B"); break;
            case "kind": answers[id] = new((AnswerKind)99, []); break;
        }
        var store = new InMemorySubmissionStore();
        await store.SetAuthorizationStateAsync(new(rules.ExamId, "candidate", "participation", 1, "device", 1,
            ParticipationBusinessState.Answering, 1, 1));
        using var service = new SubmissionService(store, new InMemoryGradingTaskQueue(), paperReader: _ => rules.Structure);
        var sync = await service.SyncProgressAsync(new("progress", rules.ExamId, "candidate", "participation", 1,
            "device", 1, 1, 1, answers, null));
        var submit = await service.SubmitAsync(new("request", "submission", rules.ExamId, "candidate", "participation", 1,
            "device", 1, 1, answers));
        Assert.Equal(SubmissionErrorCode.InvalidRequest, sync.ErrorCode);
        Assert.Equal(SubmissionErrorCode.InvalidRequest, submit.ErrorCode);
        Assert.Null(await store.FindProgressAsync("participation"));
        Assert.Null(await store.FindSubmissionAsync(rules.ExamId, "candidate"));
    }

    [Theory]
    [InlineData("partial-key")]
    [InlineData("full-score")]
    [InlineData("wrong-denominator")]
    public void ImportAcceptanceDoesNotBypassPublicationArithmetic(string defect)
    {
        var manifest = JsonNode.Parse(Manifest)!;
        var key = JsonNode.Parse(Key)!;
        if (defect == "partial-key") key["answers"]!.AsArray().RemoveAt(0);
        if (defect == "full-score") manifest["subjects"]![0]!["maxScore"] = "26";
        if (defect == "wrong-denominator") manifest["groups"]![1]!["scoring"]!["multipleCorrect"]!["wrong"] =
            "1/({正确选项总数}-{选择的正确选项数})";
        Assert.Throws<ArgumentException>(() => ImportedGradingRules.Create(manifest.ToJsonString(), key.ToJsonString()));
    }

    [Fact]
    public void PublicationRejectsDifferentStructure_AndKeepsOriginalVersion()
    {
        var rules = ImportedGradingRules.Create(Manifest, Key);
        var service = new InMemoryExamVersioningService();
        var now = DateTimeOffset.UtcNow;
        Assert.True(service.CreateExam(new(rules.ExamId, new(now.AddHours(1), now.AddHours(2)), now)).Succeeded);
        Assert.True(service.PublishPaper(new(rules.ExamId, 0, "artifact", rules.Structure, [], new(true, true, true, true), "A", "合成测试", now)).Succeeded);
        var request = new PublishGradingRulesRequest(rules.ExamId, 0, "rules", new(true, true, true), "A", "合成测试", now,
            ManifestJson: Manifest, AnswerKeyJson: Key);
        Assert.True(service.PublishGradingRules(request).Succeeded);
        var manifest = JsonNode.Parse(Manifest)!;
        manifest["groups"]![0]!["allowAbstain"] = true;
        var rejected = service.PublishGradingRules(request with { ExpectedVersion = 1, ManifestJson = manifest.ToJsonString() });
        Assert.Equal(VersioningErrorCode.StructuralChangeRejected, rejected.ErrorCode);
        Assert.Equal(1, service.GetExam(rules.ExamId).Value!.Versions.GradingVersion);
        Assert.False(service.GetExam(rules.ExamId).Value!.Paper!.Structure.Questions[0].AllowAbstain);
    }
}
