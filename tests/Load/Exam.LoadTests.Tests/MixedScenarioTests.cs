namespace Exam.LoadTests.Tests;

public sealed class MixedScenarioTests
{
    [Fact]
    public async Task KeepsConnectionsUntilDownloadsSteadyAndFinalsFinish_AndNoPostFinalProgress()
    {
        var held = false;
        var writesDone = false;
        var downloadsDone = false;
        var submitted = false;
        var downloadsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await MixedScenario.RunAsync(
            async (release, ready, ct) =>
            {
                held = true;
                ready.SetResult();
                await release.WaitAsync(ct);
                Assert.True(submitted);
                held = false;
            },
            async ct =>
            {
                Assert.True(held);
                await downloadsStarted.Task.WaitAsync(ct);
                Assert.False(submitted);
                writesDone = true;
            },
            _ =>
            {
                Assert.True(held);
                downloadsStarted.SetResult();
                downloadsDone = true;
                return Task.CompletedTask;
            },
            _ =>
            {
                Assert.True(held);
                Assert.True(writesDone);
                Assert.True(downloadsDone);
                submitted = true;
                return Task.CompletedTask;
            }, default);
        Assert.False(held);
    }

    [Fact]
    public async Task FailedWorkReleasesConnectionsAndDoesNotSubmit()
    {
        var released = false;
        var finalCalled = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => MixedScenario.RunAsync(
            async (release, ready, _) => { ready.SetResult(); await release; released = true; },
            _ => Task.FromException(new InvalidDataException("test-only-failure")),
            _ => Task.CompletedTask,
            _ => { finalCalled = true; return Task.CompletedTask; }, default));
        Assert.True(released);
        Assert.False(finalCalled);
    }
}
