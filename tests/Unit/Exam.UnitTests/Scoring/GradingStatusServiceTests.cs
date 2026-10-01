using Exam.Application.Scoring;
using Exam.Contracts.Grading;
using Exam.Domain.Scoring;
using Exam.UnitTests.Importing;

namespace Exam.UnitTests.Scoring;

/// <summary>
/// 计分状态读取：当前规则版本、完整答卷范围与完成情况。
/// 用例守住三件事——未发布规则不得给出就绪结论；未完成的答卷必须带原因报出；
/// 每次调用重新读取范围，不做跨请求缓存。
/// </summary>
public sealed class GradingStatusServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadAsync_WithoutPublishedRules_ReportsNoVersionAndNoReadiness()
    {
        var (service, scope, _) = Create();
        scope.SetScope(ExamId, [new("s-1", null)]);

        var report = await service.ReadAsync(ExamId);

        Assert.Null(report.CurrentGradingVersion);
        Assert.Null(report.Readiness);
        Assert.Equal(1, report.FinalSubmissionCount);
        Assert.Equal(0, report.GradedAtCurrentVersion);
        Assert.Equal(Now, report.ObservedAt);
    }

    [Fact]
    public async Task ReadAsync_WithOutstandingSubmissions_ReportsEachReasonAndNotReady()
    {
        var (service, scope, inputs) = Create();
        Publish(inputs, 3);
        scope.SetScope(ExamId,
        [
            new("up-to-date", 3),
            new("stale", 1),
            new("never-graded", null),
            new("failed", null, GradingFailed: true)
        ]);

        var report = await service.ReadAsync(ExamId);

        Assert.Equal(3, report.CurrentGradingVersion);
        Assert.Equal(4, report.FinalSubmissionCount);
        Assert.Equal(1, report.GradedAtCurrentVersion);
        var readiness = Assert.IsType<GradingReadiness>(report.Readiness);
        Assert.False(readiness.RecalculationReady);
        Assert.Equal(3, readiness.Outstanding.Count);
        Assert.Equal(
            [RegradeReason.NeverGraded, RegradeReason.StaleVersion, RegradeReason.GradingFailed],
            readiness.Outstanding.Select(item => item.Reason).OrderBy(reason => reason));
        Assert.Empty(report.DuplicateSubmissionIds);
    }

    [Fact]
    public async Task ReadAsync_AllGradedAtCurrentVersion_ReportsReady()
    {
        var (service, scope, inputs) = Create();
        Publish(inputs, 3);
        scope.SetScope(ExamId, [new("s-1", 3), new("s-2", 3)]);

        var report = await service.ReadAsync(ExamId);

        var readiness = Assert.IsType<GradingReadiness>(report.Readiness);
        Assert.True(readiness.RecalculationReady);
        Assert.Empty(readiness.Outstanding);
        Assert.Equal(2, report.GradedAtCurrentVersion);
    }

    [Fact]
    public async Task ReadAsync_ResultAheadOfPublishedVersion_IsBlockedRatherThanSilentlyAccepted()
    {
        var (service, scope, inputs) = Create();
        Publish(inputs, 3);
        scope.SetScope(ExamId, [new("s-1", 3), new("ahead", 4)]);

        var report = await service.ReadAsync(ExamId);

        var readiness = Assert.IsType<GradingReadiness>(report.Readiness);
        Assert.False(readiness.RecalculationReady);
        Assert.Contains(readiness.Outstanding, item => item.Reason == RegradeReason.VersionAhead);
        Assert.NotEmpty(readiness.Blockers);
    }

    [Fact]
    public async Task ReadAsync_DuplicateSubmissionIds_AreSurfacedInsteadOfDeduplicated()
    {
        var (service, scope, inputs) = Create();
        Publish(inputs, 3);
        scope.SetScope(ExamId, [new("s-1", 3), new("s-1", 3)]);

        var report = await service.ReadAsync(ExamId);

        Assert.Equal(["s-1"], report.DuplicateSubmissionIds);
        Assert.False(Assert.IsType<GradingReadiness>(report.Readiness).RecalculationReady);
    }

    [Fact]
    public async Task ReadAsync_ReadsTheScopeAgainOnEveryCall_SoSnapshotsDoNotGoStale()
    {
        var (service, scope, inputs) = Create();
        Publish(inputs, 3);
        scope.SetScope(ExamId, [new("s-1", 3)]);
        Assert.True(Assert.IsType<GradingReadiness>((await service.ReadAsync(ExamId)).Readiness).RecalculationReady);

        // 补收一份答卷后，先前的就绪结论必须立即失效。
        scope.SetScope(ExamId, [new("s-1", 3), new("late", null)]);

        var refreshed = await service.ReadAsync(ExamId);
        Assert.False(Assert.IsType<GradingReadiness>(refreshed.Readiness).RecalculationReady);
        Assert.Equal(2, refreshed.FinalSubmissionCount);
    }

    private static string ExamId => ImportedGradingRules.Create(ManifestJson, AnswerKeyJson).ExamId;

    private static void Publish(InMemoryGradingInputs inputs, long version) =>
        inputs.SetRules(ExamId, new PublishedGradingRules(
            ImportedGradingRules.Create(ManifestJson, AnswerKeyJson), version));

    private static (GradingStatusService Service, InMemoryGradingScope Scope, InMemoryGradingInputs Inputs) Create()
    {
        var scope = new InMemoryGradingScope();
        var inputs = new InMemoryGradingInputs();
        return (new GradingStatusService(inputs, scope, new FixedTimeProvider(Now)), scope, inputs);
    }

    private static string ManifestJson =>
        TestRepositoryPaths.ReadText("samples", "synthetic", "three-types", "manifest.json");

    private static string AnswerKeyJson =>
        TestRepositoryPaths.ReadText("samples", "synthetic", "three-types", "server-only", "answer-key.json");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
