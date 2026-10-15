namespace Exam.Application.HighAvailability;

/// <summary>Shares a new durability fence among requests already waiting when that fence starts.</summary>
public sealed class BatchedBusinessConfirmation(
    IBusinessConfirmation inner, CancellationToken stoppingToken = default) : IBusinessConfirmation
{
    private readonly object _gate = new();
    private TaskCompletionSource? _pending;
    private bool _running;

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
            catch (OperationCanceledException error) { batch.TrySetCanceled(error.CancellationToken); }
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
