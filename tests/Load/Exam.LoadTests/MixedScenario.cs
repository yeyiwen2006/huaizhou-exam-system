namespace Exam.LoadTests;

/// <summary>Lifecycle orchestration only: production delegates perform the real network work.</summary>
public static class MixedScenario
{
    public static async Task RunAsync(
        Func<Task, TaskCompletionSource, CancellationToken, Task> commands,
        Func<CancellationToken, Task> steady,
        Func<CancellationToken, Task> downloads,
        Func<CancellationToken, Task> finals,
        CancellationToken ct)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connections = commands(release.Task, ready, ct);
        try
        {
            var first = await Task.WhenAny(ready.Task, connections).WaitAsync(ct);
            if (first == connections && !ready.Task.IsCompleted)
            {
                await connections;
                throw new InvalidOperationException("command-connection-phase-ended-before-ready");
            }
            await ready.Task.WaitAsync(ct);
            // Both phases finish before final submission, preventing deliberate writes after submission.
            await Task.WhenAll(steady(ct), downloads(ct));
            await finals(ct);
        }
        finally
        {
            release.TrySetResult();
            await connections;
        }
    }
}
