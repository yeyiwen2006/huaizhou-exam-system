namespace Exam.Application.Submissions;

/// <summary>Bounded, cancellable FIFO queues. Final answers take priority over queued progress;
/// writes already executing are never interrupted. No answer payload is held by this gate.</summary>
public sealed class SubmissionWriteAdmission : IDisposable
{
    private readonly object _gate = new();
    private readonly LinkedList<TaskCompletionSource<bool>> _final = new();
    private readonly LinkedList<TaskCompletionSource<bool>> _progress = new();
    private readonly int _limit;
    private readonly int _queueLimit;
    private readonly TimeSpan _wait;
    private int _active;
    private bool _disposed;

    public SubmissionWriteAdmission(int limit, int queueLimit, TimeSpan wait)
    {
        if (limit < 1 || queueLimit < 0 || wait <= TimeSpan.Zero || wait > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(limit));
        _limit = limit;
        _queueLimit = queueLimit;
        _wait = wait;
    }

    public async ValueTask<bool> EnterAsync(bool final, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LinkedListNode<TaskCompletionSource<bool>> node;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_active < _limit)
            {
                _active++;
                return true;
            }
            // Separate bounds reserve final-queue space even when progress is saturated.
            var queue = final ? _final : _progress;
            if (queue.Count >= _queueLimit) return false;
            node = queue.AddLast(new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_wait);
        try { return await node.Value.Task.WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (node.List is { } list) list.Remove(node);
                // A release may have transferred a slot at exactly the cancellation boundary.
                else if (node.Value.Task.IsCompletedSuccessfully && node.Value.Task.Result) ReleaseCore();
            }
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
    }

    public void Release()
    {
        lock (_gate) ReleaseCore();
    }

    private void ReleaseCore()
    {
        if (_active <= 0) throw new InvalidOperationException("No active write slot to release.");
        _active--;
        var queue = _final.Count > 0 ? _final : _progress;
        if (!_disposed && queue.First is { } next)
        {
            queue.RemoveFirst();
            _active++;
            next.Value.SetResult(true);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var pending in _final.Concat(_progress)) pending.TrySetResult(false);
            _final.Clear();
            _progress.Clear();
        }
    }
}
