using Exam.Application.Scoring;
using Exam.Domain.Scoring;

namespace Exam.Server.Runtime;

/// <summary>
/// 计分规则发布成功后**及时安排一轮重算**：读当前已发布规则版本，按该版本规划全部最终答卷并入队，
/// 让入口能立刻回报「重算中 / 结果就绪」，而不是等后台循环下一跳。
///
/// 三条边界：
/// - 只在发布**提交之后**调用：重算队列自开连接与事务（见 <see cref="PostgresRecalculationQueue"/>），
///   不能并入发布事务；
/// - 失败只记日志，由后台 <see cref="RecalculationWorker"/> 循环补偿——已经成功的发布不因调度失败而变成失败；
/// - 幂等重放时重复触发无害：同一答卷与版本重复入队由队列去重。
/// </summary>
public sealed class GradingRecalculationTrigger(
    IGradingInputs inputs,
    RecalculationScheduler scheduler,
    ILogger<GradingRecalculationTrigger> logger)
{
    /// <summary>安排一轮重算；尚未发布规则时什么都不做。返回本轮调度结果（失败或未发布时为 null）。</summary>
    public async Task<RecalculationRun?> TryTriggerAsync(string examId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        try
        {
            var published = await inputs.LoadRulesAsync(examId, cancellationToken);
            if (published is null)
            {
                return null;
            }

            var run = await scheduler.RunOnceAsync(examId, published.GradingVersion, cancellationToken);
            if (run.Enqueued > 0)
            {
                logger.LogInformation(
                    "规则发布后已按版本 {Version} 安排重算：本次入队 {Enqueued} 项，范围共 {Total} 份最终答卷。",
                    run.TargetVersion, run.Enqueued, run.SubmissionCount);
            }

            return run;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            logger.LogError(error, "规则发布后的重算调度失败；发布结果不受影响，交给后台重算循环补偿。");
            return null;
        }
    }
}
