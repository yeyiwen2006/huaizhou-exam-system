using Exam.Contracts.Grading;
using Exam.Domain.Scoring;
using Exam.Domain.Submissions;

namespace Exam.Application.Scoring;

/// <summary>
/// 一轮重算调度的结果。供考务端显示进度（还有多少答卷未对齐）与阻断原因，
/// 并作为发布门里 RecalculationReady 的依据。
/// </summary>
public sealed record RecalculationRun(
    string ExamId,
    long TargetVersion,
    int SubmissionCount,
    int Enqueued,
    RegradePlan Plan,
    GradingReadiness Readiness);

/// <summary>
/// 规则更新后的重算调度：读取范围 → 按答卷与规则版本判定需要重算的答卷 → 按**目标版本**入队，
/// 由后台计分协调器实际执行；本类型只排队与判定，不做计分、不写结果。
///
/// 依据 requirements.md：「修改参考答案或计分标准须校验并建立新版本，对全部已收卷答卷重算，
/// 后续补收也使用该版本」、「重算期间旧表格失效并暂停查看、导出，完成后统一更新」。
///
/// 幂等：入队走既有 outbox 的「按（答卷，规则版本）去重」，因此反复调度不会产生重复任务，
/// 也不会重复计分；已对齐目标版本的答卷根本不会被入队。
/// </summary>
public sealed class RecalculationScheduler
{
    private readonly IGradingScope _scope;
    private readonly IGradingTaskQueue _queue;

    public RecalculationScheduler(IGradingScope scope, IGradingTaskQueue queue)
    {
        _scope = scope ?? throw new ArgumentNullException(nameof(scope));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public async ValueTask<RecalculationRun> RunOnceAsync(
        string examId,
        long targetVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetVersion, 1);

        var states = await _scope.ListStatesAsync(examId, cancellationToken);
        var plan = RegradePlanner.Plan(targetVersion, states);

        var enqueued = 0;
        foreach (var item in plan.Pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 入队使用**目标版本**：补交与旧版本答卷都按最新规则计分（需求：补收也使用该版本）。
            if (await _queue.EnqueueOnceAsync(item.SubmissionId, targetVersion, cancellationToken))
            {
                enqueued++;
            }
        }

        var readiness = RegradePlanner.EvaluateReadiness(targetVersion, states);
        return new RecalculationRun(examId, targetVersion, states.Count, enqueued, plan, readiness);
    }
}
