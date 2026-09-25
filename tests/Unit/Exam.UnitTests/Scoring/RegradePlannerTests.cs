using Exam.Contracts.Grading;
using Exam.Domain.Scoring;

namespace Exam.UnitTests.Scoring;

/// <summary>
/// 规则版本与重算范围判定的用例。依据 requirements.md：规则修改后对全部已收卷答卷重算、
/// 补收使用最新版本、同表不得混用版本、重算期间旧表格失效。
/// </summary>
public sealed class RegradePlannerTests
{
    [Fact]
    public void Plan_ClassifiesNeverGradedStaleAndFailedSubmissions()
    {
        var plan = RegradePlanner.Plan(
            targetVersion: 3,
            [
                new SubmissionGradingState("submission-never", null),
                new SubmissionGradingState("submission-stale", 2),
                new SubmissionGradingState("submission-failed", 2, GradingFailed: true),
                new SubmissionGradingState("submission-current", 3)
            ]);

        Assert.Equal(3, plan.Pending.Count);
        Assert.Equal(
            RegradeReason.NeverGraded,
            plan.Pending.Single(item => item.SubmissionId == "submission-never").Reason);
        Assert.Equal(
            RegradeReason.StaleVersion,
            plan.Pending.Single(item => item.SubmissionId == "submission-stale").Reason);
        Assert.Equal(
            RegradeReason.GradingFailed,
            plan.Pending.Single(item => item.SubmissionId == "submission-failed").Reason);

        Assert.Equal(["submission-current"], plan.UpToDate);
        Assert.False(plan.IsComplete);
        Assert.Equal(4, plan.TotalCount);
    }

    [Fact]
    public void Plan_AlreadyGradedAtTargetVersion_IsNotRegraded()
    {
        // 按答卷与规则版本去重：规则没变时不会重复计分。
        var plan = RegradePlanner.Plan(
            targetVersion: 5,
            [
                new SubmissionGradingState("submission-1", 5),
                new SubmissionGradingState("submission-2", 5)
            ]);

        Assert.Empty(plan.Pending);
        Assert.True(plan.IsComplete);
        Assert.Equal(2, plan.UpToDate.Count);
    }

    [Fact]
    public void Plan_GradedVersionNewerThanTarget_IsRegradedAsWell()
    {
        // 规则回退时，表里可能留着更新版本的分数：这也属于混用版本，必须按目标版本重算。
        var plan = RegradePlanner.Plan(
            targetVersion: 2,
            [new SubmissionGradingState("submission-newer", 4)]);

        var item = Assert.Single(plan.Pending);
        Assert.Equal(RegradeReason.StaleVersion, item.Reason);
        Assert.Empty(plan.UpToDate);
    }

    [Fact]
    public void Plan_LateSubmissionAfterRuleChange_IsGradedWithTargetVersion()
    {
        // 「后续补收也使用该版本」：补交的答卷从未计分，按当前目标版本计分。
        var plan = RegradePlanner.Plan(
            targetVersion: 4,
            [new SubmissionGradingState("submission-late", null)]);

        Assert.Equal(RegradeReason.NeverGraded, Assert.Single(plan.Pending).Reason);
    }

    [Fact]
    public void Plan_DuplicateSubmission_IsReportedAndNotCountedTwice()
    {
        // 同一答卷出现两次会让成绩重复累计：只规划一次，同时把重复本身报出来。
        var plan = RegradePlanner.Plan(
            targetVersion: 2,
            [
                new SubmissionGradingState("submission-1", 1),
                new SubmissionGradingState("submission-1", 1)
            ]);

        Assert.Single(plan.Pending);
        Assert.Equal(["submission-1"], plan.DuplicateSubmissionIds);
        Assert.Equal(1, plan.TotalCount);
    }

    [Fact]
    public void EvaluateReadiness_AllGradedAtTargetVersion_IsReady()
    {
        var readiness = RegradePlanner.EvaluateReadiness(
            targetVersion: 3,
            [
                new SubmissionGradingState("submission-1", 3),
                new SubmissionGradingState("submission-2", 3)
            ]);

        Assert.True(readiness.RecalculationReady);
        Assert.Empty(readiness.Outstanding);
        Assert.Empty(readiness.Blockers);
    }

    [Fact]
    public void EvaluateReadiness_AnyStaleSubmission_BlocksPublication()
    {
        // 「同表不得混用版本」：只要还有一份答卷没按目标版本计分，就不允许发布。
        var readiness = RegradePlanner.EvaluateReadiness(
            targetVersion: 3,
            [
                new SubmissionGradingState("submission-1", 3),
                new SubmissionGradingState("submission-2", 2)
            ]);

        Assert.False(readiness.RecalculationReady);
        Assert.Equal("submission-2", Assert.Single(readiness.Outstanding).SubmissionId);
        Assert.Contains(readiness.Blockers, text => text.Contains("旧规则版本", StringComparison.Ordinal));
        Assert.Equal(3, readiness.TargetVersion);
    }

    [Fact]
    public void EvaluateReadiness_NeverGradedAndFailedProduceDistinctBlockers()
    {
        var readiness = RegradePlanner.EvaluateReadiness(
            targetVersion: 2,
            [
                new SubmissionGradingState("submission-never", null),
                new SubmissionGradingState("submission-failed", 2, GradingFailed: true)
            ]);

        Assert.False(readiness.RecalculationReady);
        Assert.Contains(readiness.Blockers, text => text.Contains("尚未计分", StringComparison.Ordinal));
        Assert.Contains(readiness.Blockers, text => text.Contains("计分失败", StringComparison.Ordinal));
    }

    [Fact]
    public void EvaluateReadiness_DuplicateSubmission_BlocksPublication()
    {
        var readiness = RegradePlanner.EvaluateReadiness(
            targetVersion: 2,
            [
                new SubmissionGradingState("submission-1", 2),
                new SubmissionGradingState("submission-1", 2)
            ]);

        Assert.False(readiness.RecalculationReady);
        Assert.Contains(readiness.Blockers, text => text.Contains("重复的答卷记录", StringComparison.Ordinal));
    }

    [Fact]
    public void EvaluateReadiness_NoSubmissions_IsReadyForRecalculation()
    {
        // 没有答卷时「需要重算的集合为空」，因此重算就绪；至于能不能出表由报表侧条件决定，不在此处冒充。
        var readiness = RegradePlanner.EvaluateReadiness(targetVersion: 1, []);

        Assert.True(readiness.RecalculationReady);
        Assert.Empty(readiness.Outstanding);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Plan_InvalidTargetVersion_IsRejected(long targetVersion)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RegradePlanner.Plan(targetVersion, []));
    }
}
