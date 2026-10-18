using Exam.Contracts.Common;
using Exam.Domain.Common;

namespace Exam.Application.HighAvailability;

/// <summary>Shares a new durability fence among requests already waiting when that fence starts.</summary>
public sealed class BatchedBusinessConfirmation(
    IBusinessConfirmation inner, CancellationToken stoppingToken = default) : IBusinessConfirmation, IDisposable
{
    private readonly object _gate = new();
    private TaskCompletionSource? _pending;
    private bool _running;

    public void Dispose() => (inner as IDisposable)?.Dispose();

    public Task ConfirmAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stoppingToken.ThrowIfCancellationRequested();
        Task confirmation;
        lock (_gate)
        {
            confirmation = (_pending ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            if (!_running)
            {
                _running = true;
                _ = Task.Run(RunAsync);
            }
        }
        // One disconnected caller must not cancel confirmation for other committed requests.
        return confirmation.WaitAsync(cancellationToken);
    }

    private async Task RunAsync()
    {
        while (true)
        {
            TaskCompletionSource batch;
            lock (_gate)
            {
                batch = _pending!;
                _pending = null;
            }
            // Calls arriving after this point go into the NEXT fence. Reusing the in-flight
            // result could acknowledge writes that happened after its WAL boundary.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await inner.ConfirmAsync(deadline.Token).ConfigureAwait(false);
                batch.TrySetResult();
            }
            catch (OperationCanceledException error) when (stoppingToken.IsCancellationRequested)
            { batch.TrySetCanceled(error.CancellationToken); }
            catch (OperationCanceledException)
            {
                // The fence deadline does not undo the committed request. Report a retryable
                // pending confirmation so the caller retains and retries its original request.
                batch.TrySetException(new DomainRuleException(ErrorCodes.ServiceRecovering,
                    "持久确认暂未完成，请保留原请求重试。", retryable: true));
            }
            catch (Exception error) { batch.TrySetException(error); }
            // Observe failures even when every caller has already stopped waiting.
            if (batch.Task.IsFaulted) _ = batch.Task.Exception;
            lock (_gate)
            {
                if (_pending is not null) continue;
                _running = false;
                return;
            }
        }
    }
}
