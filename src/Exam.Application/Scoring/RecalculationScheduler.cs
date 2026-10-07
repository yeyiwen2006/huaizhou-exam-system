using Exam.Contracts.Grading;
using Exam.Domain.Scoring;

namespace Exam.Application.Scoring;

/// <summary>单次范围快照和入队统计，不是可跨请求缓存使用的报表发布授权。</summary>
public sealed record RecalculationRun(string ExamId, long TargetVersion, int SubmissionCount, int Enqueued,
    RegradePlan Plan, GradingReadiness Readiness);

/// <summary>
/// 版本服务 发布新规则后，用当前已发布版本规划全部最终答卷并交给重算专用队列。
/// 实际计分由 精确计分 协调器执行。重复范围、过期目标不执行任何调度写入。
/// 生产调用方仍须经过主备写门；报表查看/导出必须重新核对当前版本和收卷集合，
/// 不能把本快照当成 版本服务 规则发布前置门，否则首次发布/重算会形成循环依赖。
/// </summary>
public sealed class RecalculationScheduler(IGradingScope scope, IRecalculationQueue queue)
{
    private readonly IGradingScope _scope = scope ?? throw new ArgumentNullException(nameof(scope));
    private readonly IRecalculationQueue _queue = queue ?? throw new ArgumentNullException(nameof(queue));

    public async ValueTask<RecalculationRun> RunOnceAsync(string examId, long targetVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetVersion, 1);
        cancellationToken.ThrowIfCancellationRequested();
        var states = await _scope.ListStatesAsync(examId, cancellationToken);
        var plan = RegradePlanner.Plan(targetVersion, states);
        var readiness = RegradePlanner.EvaluateReadiness(plan);
        if (plan.DuplicateSubmissionIds.Count > 0 || plan.Pending.Any(item => item.Reason == RegradeReason.VersionAhead))
            return new(examId, targetVersion, plan.TotalCount, 0, plan, readiness);

        var enqueued = 0;
        foreach (var item in plan.Pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _queue.EnsureQueuedAsync(examId, item.SubmissionId, targetVersion, cancellationToken)) enqueued++;
        }
        // 入队完成不代表计分完成；就绪结果仍表示本次读取的同一范围快照。
        return new(examId, targetVersion, plan.TotalCount, enqueued, plan, readiness);
    }
}
