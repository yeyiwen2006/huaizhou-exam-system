namespace Exam.Server.Runtime;

/// <summary>
/// D05 生产后台重算对账循环：周期性执行一轮对账，直到进程停止。
///
/// 这是「规则发布后重算」的兜底通道：发布入口（C05）可以直接调用同一个调度器获得即时性，
/// 本循环保证即使发布入口尚未接线、或发布后进程重启，缺失/过期成绩仍会被补上。
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
