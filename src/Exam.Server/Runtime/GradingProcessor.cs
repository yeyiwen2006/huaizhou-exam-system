using Exam.Application.Scoring;

namespace Exam.Server.Runtime;

/// <summary>
/// 一批生产后台计分：仅在本机是活动主节点时领取持久任务，交给 精确计分 协调器完成
/// 「outbox → 冻结答卷与最新已发布规则 → 精确计分 → 结果落库 → 结项」。
///
/// 与循环分离，便于确定性地验证主备写门、批次与失败处理；后台循环见 <see cref="GradingWorker"/>。
/// 已领取未完成的任务保留在库中由租约到期恢复；本处理器不删除任务、不降版本计分。
/// </summary>
public sealed class GradingProcessor(
    GradingCoordinator coordinator,
    ActiveNodeGuard activeNode,
    ILogger<GradingProcessor> logger)
{
    /// <summary>
    /// 执行一批并返回领取、成功和失败数；本机不是活动主节点时不触碰任何任务。
    /// </summary>
    public async Task<GradingBatchReport> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        if (!(await activeNode.CheckAsync(cancellationToken)).Allowed)
        {
            return GradingBatchReport.Empty;
        }

        var report = await coordinator.RunOnceAsync(cancellationToken);
        if (report.Failed > 0)
        {
            logger.LogWarning(
                "计分批次领取 {Claimed} 项，失败 {Failed} 项；任务保留在库中等待重试。",
                report.Claimed, report.Failed);
        }

        return report;
    }
}
