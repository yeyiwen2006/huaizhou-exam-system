using Exam.Application.Scoring;
using Exam.Domain.Scoring;

namespace Exam.Server.Runtime;

/// <summary>
/// 一轮重算对账：按**当前已发布计分版本**逐场核对，把缺失或过期的成绩交重算专用队列。
///
/// 范围由实际成绩版本与已发布版本共同决定，不看旧 outbox 状态；重复对账不产生重复任务，
/// 发现更高版本成绩时阻断本批而不降版覆盖。仅在本机是活动主节点时写入。
/// 与循环分离，便于确定性验证；后台循环见 <see cref="RecalculationWorker"/>。
/// </summary>
public sealed class RecalculationProcessor(
    IPublishedGradingDirectory published,
    RecalculationScheduler scheduler,
    ActiveNodeGuard activeNode,
    ILogger<RecalculationProcessor> logger)
{
    /// <summary>
    /// 执行一轮对账，返回本轮入队数；本机不是活动主节点时返回 0，且不写任何队列。
    /// </summary>
    public async Task<int> ReconcileOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!(await activeNode.CheckAsync(cancellationToken)).Allowed)
        {
            return 0;
        }

        var enqueued = 0;
        foreach (var exam in await published.ListPublishedAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = await scheduler.RunOnceAsync(exam.ExamId, exam.GradingVersion, cancellationToken);
            if (run.Enqueued > 0)
            {
                logger.LogInformation(
                    "考试 {ExamId} 已按版本 {Version} 重算入队 {Enqueued} 项，范围共 {Total} 份最终答卷。",
                    exam.ExamId, exam.GradingVersion, run.Enqueued, run.SubmissionCount);
                enqueued += run.Enqueued;
            }
        }

        return enqueued;
    }
}
