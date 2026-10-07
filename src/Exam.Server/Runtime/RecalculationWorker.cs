namespace Exam.Server.Runtime;

/// <summary>
/// 评分处理 生产后台重算对账循环：周期性执行一轮对账，直到进程停止。
///
/// 这是「规则发布后重算」的兜底通道：发布入口（试题修订）可以直接调用同一个调度器获得即时性，
/// 发布后进程重启或处理被中断时，本循环补齐缺失或过期的成绩。
/// 一轮的逻辑见 <see cref="RecalculationProcessor"/>。
/// </summary>
public sealed class RecalculationWorker(
    RecalculationProcessor processor,
    GradingBackgroundOptions options,
    ILogger<RecalculationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await processor.ReconcileOnceAsync(stoppingToken);
                await Task.Delay(options.IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "重算对账失败；下轮继续，已入队任务不受影响。");
                await Task.Delay(options.ErrorDelay, stoppingToken);
            }
        }
    }
}
