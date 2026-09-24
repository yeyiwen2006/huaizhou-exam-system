using System.IO;
using System.Reflection;
using Exam.Student.Core;
using Exam.Contracts.Submissions;
using Microsoft.Data.Sqlite;

namespace Exam.StudentUnitTests;

public sealed class SqliteLifecycleRegressionTests
{
    [Fact]
    public async Task SimultaneousColdOpensShareInitializationAndReleaseThePersistentLock()
    {
        var root = Path.Combine(Path.GetTempPath(), "sqlite-cold-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.db");
        var stores = Enumerable.Range(0, 8).Select(_ => new SqliteLocalStateStore(path)).ToArray();
        try
        {
            var states = await Task.WhenAll(stores.Select(s => s.LoadAnswerStateAsync(new("server", "exam", "candidate"))));
            Assert.All(states, Assert.Null);
            Assert.True(File.Exists(path + ".identity"));
            Assert.True(File.Exists(path + ".initializing"));
            // Presence is not ownership; all initializers released their exclusive handles.
            using var guard = new FileStream(path + ".initializing", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            foreach (var store in stores) store.Dispose();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task EveryConcurrentDisposeWaitsForAcceptedWorkAndConnectionClose()
    {
        var root = Path.Combine(Path.GetTempPath(), "sqlite-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "state.db");
        var key = new LocalStateKey("server", "exam", "candidate");
        var store = new SqliteLocalStateStore(path);
        Task? firstDispose = null; Task? secondDispose = null;
        Task<StoreWriteResult>? write = null;
        using var raw = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        SqliteTransaction? blocker = null;
        try
        {
            Assert.Null(await store.LoadAnswerStateAsync(key));
            raw.Open(); blocker = raw.BeginTransaction();
            var state = new AnswerStateRecord("participation", 1, 1, 1, 1, "q", DateTimeOffset.UtcNow,
                new Dictionary<string, AnswerValue> { ["q"] = AnswerValue.Empty() }, new HashSet<string>(), "device");
            write = store.SaveAnswerStateAsync(key, state, new("exam", "candidate", "participation", 1, "device", 1, 1, 1,
                state.Answers, "q", state.UpdatedAt, "request"));
            firstDispose = Task.Factory.StartNew(store.Dispose, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            // Observe shutdown admission, not wall-clock scheduling, before starting the second caller.
            var field = typeof(SqliteLocalStateStore).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var until = DateTime.UtcNow.AddSeconds(5);
            while (!(bool)field.GetValue(store)! && DateTime.UtcNow < until) await Task.Delay(10);
            Assert.True((bool)field.GetValue(store)!);
            using var secondEntered = new ManualResetEventSlim();
            secondDispose = Task.Factory.StartNew(() => { secondEntered.Set(); store.Dispose(); }, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            Assert.True(secondEntered.Wait(TimeSpan.FromSeconds(5)));
            await Task.Delay(100);
            Assert.False(secondDispose.IsCompleted, "A concurrent disposer returned while the accepted write was still locked.");
        }
        finally
        {
            blocker?.Dispose(); raw.Close();
            if (firstDispose is not null) await firstDispose;
            if (secondDispose is not null) await secondDispose;
            store.Dispose();
        }
        try
        {
            Assert.True((await write!).Succeeded);
            // No live worker or connection may remain after either Dispose returns.
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { Directory.Delete(root, true); }
    }
}
