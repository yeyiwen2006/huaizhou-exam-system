namespace Exam.Server.Runtime;

/// <summary>
/// D05 生产后台计分循环：重复执行一批后台计分，直到进程停止。
/// 有任务时立即继续取下一批，空闲时按空闲间隔休眠，异常退避重试而不退出循环——
/// 退出循环会让「计分停止」看起来像「没有待计分任务」。实际一批的逻辑见 <see cref="GradingProcessor"/>。
/// </summary>
public sealed class GradingWorker(
    GradingProcessor processor,
    GradingBackgroundOptions options,
    ILogger<GradingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var claimed = await processor.RunOnceAsync(stoppingToken);
                await Task.Delay(claimed > 0 ? options.BusyDelay : options.IdleDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "计分后台批次失败；持久任务保留待重试。");
                await Task.Delay(options.ErrorDelay, stoppingToken);
            }
        }
    }
}
