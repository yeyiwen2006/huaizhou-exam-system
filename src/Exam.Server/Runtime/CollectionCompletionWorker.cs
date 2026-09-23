using Exam.Domain.Submissions;

namespace Exam.Server.Runtime;

public sealed class CollectionCompletionWorker(
    IAuthoritativeCollectionCompletionStore store,
    ActiveNodeGuard activeNode,
    ILogger<CollectionCompletionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if ((await activeNode.CheckAsync(stoppingToken)).Allowed)
                    await store.ProcessPendingAsync(100, stoppingToken);
                await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogError(exception, "Collection checks failed; durable pending work will be retried.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
}
