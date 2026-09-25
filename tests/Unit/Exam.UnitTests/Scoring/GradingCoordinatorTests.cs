using Exam.Application.Scoring;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Domain.Scoring;

namespace Exam.UnitTests.Scoring;

/// <summary>
/// 后台计分协调器的用例。用内存实现驱动，不需要 PostgreSQL。
/// 覆盖：正常计分落库结项、规则未发布与答卷缺失的保留重试、单条失败隔离、
/// 旧版本任务按最新规则计分并按最新版本标记、重复运行的按答卷去重。
/// </summary>
public sealed class GradingCoordinatorTests
{
    private const string ExamId = "coordinator-exam";
    private const string CandidateId = "coordinator-candidate";
    private const string SubmissionId = "coordinator-submission";

    private const string ManifestJson = """
        {
          "schemaVersion": 1,
          "examId": "coordinator-exam",
          "examName": "协调器合成考试",
          "subjects": [{ "subjectId": "subject-1", "name": "科目甲", "maxScore": "3" }],
          "groups": [
            {
              "groupId": "group-1",
              "subjectId": "subject-1",
              "name": "一、单项选择题",
              "type": "single",
              "order": 1,
              "questionCount": 1,
              "scoring": { "oneCorrect": { "correct": "3", "blank": "0", "wrong": "-0.75" } }
            }
          ],
          "questions": [
            {
              "questionId": "q-001",
              "questionNo": 1,
              "subjectId": "subject-1",
              "groupId": "group-1",
              "source": "questions/协调器合成考试_1.tex",
              "options": ["A", "B", "C", "D"]
            }
          ],
          "resourceRoots": []
        }
        """;

    private const string AnswerKeyJson = """
        {
          "schemaVersion": 1,
          "examId": "coordinator-exam",
          "answers": [{ "questionId": "q-001", "questionNo": 1, "correctOptionIds": ["A"] }]
        }
        """;

    private static readonly DateTimeOffset Now = new(2026, 9, 25, 13, 0, 0, TimeSpan.Zero);

    /// <summary>另一个考试的合法规则源，用于验证「规则与答卷考试不一致」必须报错。</summary>
    private const string AlternateManifestJson = """
        {
          "schemaVersion": 1,
          "examId": "another-exam",
          "examName": "另一考试",
          "subjects": [{ "subjectId": "subject-1", "name": "科目甲", "maxScore": "3" }],
          "groups": [
            {
              "groupId": "group-1",
              "subjectId": "subject-1",
              "name": "一、单项选择题",
              "type": "single",
              "order": 1,
              "questionCount": 1,
              "scoring": { "oneCorrect": { "correct": "3", "blank": "0", "wrong": "0" } }
            }
          ],
          "questions": [
            {
              "questionId": "q-001",
              "questionNo": 1,
              "subjectId": "subject-1",
              "groupId": "group-1",
              "source": "questions/另一考试_1.tex",
              "options": ["A", "B", "C", "D"]
            }
          ],
          "resourceRoots": []
        }
        """;

    private const string AlternateAnswerKeyJson = """
        {
          "schemaVersion": 1,
          "examId": "another-exam",
          "answers": [{ "questionId": "q-001", "questionNo": 1, "correctOptionIds": ["A"] }]
        }
        """;

    [Fact]
    public async Task RunOnceAsync_GradesPendingTaskAndCompletesIt()
    {
        var (coordinator, tasks, results, inputs) = Create();
        inputs.SetSubmission(Submission());
        inputs.SetRules(ExamId, Rules());
        tasks.Enqueue(SubmissionId, 1);

        var report = await coordinator.RunOnceAsync();

        Assert.Equal(1, report.Claimed);
        Assert.True(report.Graded == 1, $"应计分 1 份，实际 {report.Graded}；原因：{Describe(report)}");
        Assert.Empty(report.FailureMessages);

        var stored = await results.FindAsync(SubmissionId);
        Assert.NotNull(stored);
        Assert.Equal(1, stored!.GradingVersion);
        Assert.Equal("3", stored.Grade.ExactTotalScore);

        // 结项后不再被领取。
        Assert.Empty(await tasks.ClaimAsync(10, Now.AddMinutes(10), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task RunOnceAsync_RulesNotPublished_KeepsTaskPendingWithReason()
    {
        var (coordinator, tasks, results, inputs) = Create();
        inputs.SetSubmission(Submission());
        tasks.Enqueue(SubmissionId, 1);

        var report = await coordinator.RunOnceAsync();

        Assert.Equal(0, report.Graded);
        Assert.True(report.Failed == 1, $"应失败 1 条，实际 {report.Failed}；原因：{Describe(report)}");
        Assert.Contains(report.FailureMessages, text => text.Contains("尚未发布计分规则", StringComparison.Ordinal));

        // 任务不得删除：可再次领取并重试。
        Assert.Single(tasks.Snapshot());
        Assert.Null(await results.FindAsync(SubmissionId));
        Assert.Single(await tasks.ClaimAsync(10, Now.AddMinutes(10), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task RunOnceAsync_MissingSubmission_KeepsTaskWithReason()
    {
        var (coordinator, tasks, results, inputs) = Create();
        inputs.SetRules(ExamId, Rules());
        tasks.Enqueue(SubmissionId, 1);

        var report = await coordinator.RunOnceAsync();

        Assert.True(report.Failed == 1, $"应失败 1 条，实际 {report.Failed}；原因：{Describe(report)}");
        Assert.Contains(report.FailureMessages, text => text.Contains("找不到对应的最终答卷", StringComparison.Ordinal));
        Assert.Single(tasks.Snapshot());
        Assert.Null(await results.FindAsync(SubmissionId));
    }

    [Fact]
    public async Task RunOnceAsync_OneFailingTaskDoesNotBlockOthers()
    {
        var (coordinator, tasks, results, inputs) = Create();
        inputs.SetSubmission(Submission());
        // 一份答卷的答案里含未声明选项：严格计分必须失败，且不影响同批另一条任务。
        inputs.SetSubmission(Submission("coordinator-submission-bad", """{"q-001":{"kind":"selected","optionIds":["Z"]}}"""));
        inputs.SetRules(ExamId, Rules());
        tasks.Enqueue(SubmissionId, 1);
        tasks.Enqueue("coordinator-submission-bad", 1);

        var report = await coordinator.RunOnceAsync();

        Assert.Equal(2, report.Claimed);
        Assert.True(report.Graded == 1 && report.Failed == 1,
            $"应 1 成 1 败，实际成 {report.Graded} 败 {report.Failed}；原因：{Describe(report)}");
        Assert.NotNull(await results.FindAsync(SubmissionId));
        Assert.Null(await results.FindAsync("coordinator-submission-bad"));
        Assert.Single(tasks.Snapshot(), task => task.SubmissionId == "coordinator-submission-bad");
    }

    [Fact]
    public async Task RunOnceAsync_StaleTaskVersion_GradesWithLatestRulesAndStampsLatestVersion()
    {
        // 需求：判分仍采用最新有效参考答案及规则 —— 提交时记录的版本是 1，已发布版本是 3。
        var (coordinator, tasks, results, inputs) = Create();
        inputs.SetSubmission(Submission());
        inputs.SetRules(ExamId, Rules(gradingVersion: 3));
        tasks.Enqueue(SubmissionId, 1);

        var report = await coordinator.RunOnceAsync();

        Assert.True(report.Graded == 1, $"应计分 1 份，实际 {report.Graded}；原因：{Describe(report)}");
        var stored = await results.FindAsync(SubmissionId);
        Assert.Equal(3, stored!.GradingVersion);

        // 旧版本任务在产出最新版本结果后结项，不再重复计分。
        Assert.Empty(await tasks.ClaimAsync(10, Now.AddMinutes(10), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task RunOnceAsync_CompletedTaskIsNotRegraded_AndResultStaysSingle()
    {
        // 完成的任务不会被重复领取；即使再收到同一答卷的重复投递，结果表按答卷去重，分数不会翻倍。
        var (coordinator, tasks, results, inputs) = Create();
        inputs.SetSubmission(Submission());
        inputs.SetRules(ExamId, Rules());
        tasks.Enqueue(SubmissionId, 1);

        var first = await coordinator.RunOnceAsync();
        Assert.True(first.Graded == 1, $"首次应计分 1 份，实际 {first.Graded}；原因：{Describe(first)}");

        var second = await coordinator.RunOnceAsync();
        Assert.Equal(0, second.Claimed);

        // 重复投递同一答卷（例如备用节点重放）：仍然只有一行结果。
        await results.SaveAsync(new StoredGradingResult((await results.FindAsync(SubmissionId))!.Grade, Now));

        Assert.Single(await results.ListAsync(ExamId, 1));
        var stored = await results.FindAsync(SubmissionId);
        Assert.Equal("3", stored!.Grade.ExactTotalScore);
    }

    [Fact]
    public async Task RunOnceAsync_RulesForAnotherExam_IsReported()
    {
        var (coordinator, tasks, results, inputs) = Create();
        inputs.SetSubmission(Submission());
        // 键是答卷的考试，但取回的规则属于另一个考试：必须报错，不得按错误规则计分。
        inputs.SetRules(ExamId, Rules(AlternateManifestJson, AlternateAnswerKeyJson));
        tasks.Enqueue(SubmissionId, 1);

        var report = await coordinator.RunOnceAsync();

        Assert.True(report.Failed == 1, $"应失败 1 条，实际 {report.Failed}；原因：{Describe(report)}");
        Assert.Contains(report.FailureMessages, text => text.Contains("与答卷的考试", StringComparison.Ordinal));
        Assert.Null(await results.FindAsync(SubmissionId));
    }

    [Fact]
    public async Task RunOnceAsync_EmptyQueue_ReturnsEmptyReport()
    {
        var (coordinator, _, _, _) = Create();

        var report = await coordinator.RunOnceAsync();

        Assert.Equal(GradingBatchReport.Empty, report);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(GradingCoordinator.MaxBatchSize + 1)]
    public void Construct_InvalidBatchSize_IsRejected(int batchSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GradingCoordinator(
            new InMemoryGradingTaskSource(),
            new InMemoryGradingResultStore(),
            new InMemoryGradingInputs(),
            batchSize));
    }

    [Fact]
    public async Task RunOnceAsync_RespectsBatchBound()
    {
        var (coordinator, tasks, _, inputs) = Create(batchSize: 2);
        for (var index = 0; index < 5; index++)
        {
            var id = $"coordinator-batch-{index}";
            inputs.SetSubmission(Submission(id));
            tasks.Enqueue(id, 1);
        }

        inputs.SetRules(ExamId, Rules());
        var first = await coordinator.RunOnceAsync();
        var second = await coordinator.RunOnceAsync();

        Assert.Equal(2, first.Claimed);
        Assert.Equal(2, second.Claimed);
    }

    private static string Describe(GradingBatchReport report) =>
        report.FailureMessages.Count == 0 ? "无失败信息" : string.Join(" | ", report.FailureMessages);

    private static (GradingCoordinator Coordinator, InMemoryGradingTaskSource Tasks,
        InMemoryGradingResultStore Results, InMemoryGradingInputs Inputs) Create(int batchSize = 32)
    {
        var tasks = new InMemoryGradingTaskSource();
        var results = new InMemoryGradingResultStore();
        var inputs = new InMemoryGradingInputs();
        var coordinator = new GradingCoordinator(
            tasks,
            results,
            inputs,
            batchSize,
            TimeSpan.FromMinutes(1),
            new FixedTimeProvider(Now));

        return (coordinator, tasks, results, inputs);
    }

    private static PublishedGradingRules Rules(
        string manifestJson = ManifestJson,
        string answerKeyJson = AnswerKeyJson,
        long gradingVersion = 1) =>
        new(ImportedGradingRules.Create(manifestJson, answerKeyJson), gradingVersion);

    private static FinalSubmission Submission(
        string submissionId = SubmissionId,
        string frozenAnswersJson = """{"q-001":{"kind":"selected","optionIds":["A"]}}""") =>
        new(
            submissionId,
            ExamId,
            CandidateId,
            "coordinator-participation",
            "coordinator-device",
            1,
            1,
            System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, AnswerValue>>(
                frozenAnswersJson,
                JsonDefaults.Create())!,
            Now,
            new SubmissionReceipt(
                "coordinator-receipt",
                submissionId,
                ExamId,
                CandidateId,
                "coordinator-participation",
                Now,
                1,
                1));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
