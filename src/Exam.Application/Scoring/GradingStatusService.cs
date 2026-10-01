using Exam.Contracts.Grading;
using Exam.Domain.Scoring;

namespace Exam.Application.Scoring;

/// <summary>
/// 读取某场考试的计分状态：当前已发布规则版本、完整答卷范围与计分完成情况。
///
/// 每次调用都重新读取当前已发布版本与全部最终答卷，**不缓存**：
/// 规则更新或补收答卷都会使上一次的快照过期。就绪判定复用 <see cref="RegradePlanner"/> 的同一套规则，
/// 因此本报告与重算调度、报表门对「完成」的理解一致，不会出现两个口径。
/// </summary>
public sealed class GradingStatusService(
    IGradingInputs inputs,
    IGradingScope scope,
    TimeProvider? timeProvider = null)
{
    private readonly IGradingInputs _inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
    private readonly IGradingScope _scope = scope ?? throw new ArgumentNullException(nameof(scope));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async ValueTask<GradingStatusReport> ReadAsync(
        string examId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);

        var observedAt = _timeProvider.GetUtcNow();
        var states = await _scope.ListStatesAsync(examId, cancellationToken);

        // 未发布任何计分规则：不存在目标版本，不构造就绪结论，避免被当成「已就绪」。
        var published = await _inputs.LoadRulesAsync(examId, cancellationToken);
        if (published is null)
        {
            return new GradingStatusReport(examId, null, states.Count, 0, null, [], observedAt);
        }

        var plan = RegradePlanner.Plan(published.GradingVersion, states);
        return new GradingStatusReport(
            examId,
            published.GradingVersion,
            plan.TotalCount,
            plan.UpToDate.Count,
            RegradePlanner.EvaluateReadiness(plan),
            plan.DuplicateSubmissionIds,
            observedAt);
    }
}
