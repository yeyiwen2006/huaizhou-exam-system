using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;

namespace Exam.AdminUnitTests;

public sealed partial class CollectionPollingTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();

    [Fact]
    public async Task Polling_PagesRecoversAfterFailureAndDoesNotChangeRosterRows()
    {
        var events = Enumerable.Range(1, 102).Select(index => Event("exam-a", index)).ToArray();
        var cursors = new List<long>();
        var failOnce = true;
        using var handler = new Handler((request, _) =>
        {
            var cursor = Cursor(request);
            lock (cursors) cursors.Add(cursor);
            if (cursor == 100 && failOnce)
            {
                failOnce = false;
                throw new HttpRequestException("synthetic outage");
            }
            return Task.FromResult(Success(events.Where(item => item.RosterVersion > cursor).Take(100).ToArray()));
        });
        using var api = Configured(handler);
        using var vm = new MonitoringViewModel(api, TimeSpan.FromMilliseconds(30)) { ExamId = "exam-a" };
        vm.Rows.Add(new MonitoringRow("part", "candidate", "候考", "device", 1, 1, 1,
            "未提交", "接口未提供", "接口未提供"));

        vm.IsPageActive = true;
        vm.ConnectionReady = true;
        await UntilAsync(() => vm.CollectionEvents.Count == 102);

        Assert.Equal(102, vm.CollectionEvents.Select(item => item.EventId).Distinct().Count());
        Assert.Equal("未提交", Assert.Single(vm.Rows).CollectionState);
        lock (cursors) Assert.Equal(new long[] { 0, 100, 100 }, cursors.Take(3));
    }

    [Fact]
    public async Task Polling_KeepsRemindingOffPageAndStopsOnDisconnectAndDispose()
    {
        var calls = 0;
        using var handler = new Handler((request, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Success(new[] { Event("exam-a", 1) }));
        });
        using var api = Configured(handler);
        var vm = new MonitoringViewModel(api, TimeSpan.FromMilliseconds(25))
        {
            ExamId = "exam-a", IsPageActive = true, ConnectionReady = true
        };
        await UntilAsync(() => Volatile.Read(ref calls) >= 2);
        // 收齐提醒不依赖页面是否在前台：离开监测页仍要继续轮询（需求要求任意页面都能收到收齐提醒）。
        var beforeExit = Volatile.Read(ref calls);
        vm.IsPageActive = false;
        await UntilAsync(() => Volatile.Read(ref calls) > beforeExit);
        var stopped = Volatile.Read(ref calls);
        api.Disconnect();
        stopped = Volatile.Read(ref calls);
        await Task.Delay(85);
        Assert.Equal(stopped, Volatile.Read(ref calls));
        Assert.False(vm.ConnectionReady);
        Assert.Empty(vm.CollectionEvents);

        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        vm.ConnectionReady = true;
        await UntilAsync(() => Volatile.Read(ref calls) > stopped);
        vm.Dispose();
        stopped = Volatile.Read(ref calls);
        await Task.Delay(85);
        Assert.Equal(stopped, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Polling_ContinuesAfterTenPageBatchWithoutSkippingTheNextPage()
    {
        var all = Enumerable.Range(1, 1001).Select(index => Event("exam-a", index)).ToArray();
        var cursors = new List<long>();
        var nextPollStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextPollResponse = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler((request, token) =>
        {
            var cursor = Cursor(request);
            lock (cursors) cursors.Add(cursor);
            if (cursor == 1001)
            {
                nextPollStarted.TrySetResult();
                return nextPollResponse.Task.WaitAsync(token);
            }
            return Task.FromResult(Success(all.Where(item => item.RosterVersion > cursor).Take(100).ToArray()));
        });
        using var api = Configured(handler);
        using var vm = new MonitoringViewModel(api, TimeSpan.FromMilliseconds(20))
        {
            ExamId = "exam-a"
        };
        var completed = new TaskCompletionSource<(long[] Versions, string Status)>(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.CollectionCompleted += notification =>
        {
            if (notification.RosterVersion != 1001) return;
            // Capture this completed batch before the next timer tick changes the live status.
            completed.TrySetResult((vm.CollectionEvents.Select(item => item.RosterVersion).ToArray(), vm.CollectionStatus));
        };
        vm.IsPageActive = true;
        vm.ConnectionReady = true;

        var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // A new automatic read must preserve the last completed summary instead of flickering.
        await nextPollStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(Enumerable.Range(502, 500).Select(value => (long)value),
            result.Versions);
        lock (cursors) Assert.Equal(Enumerable.Range(0, 11).Select(page => page * 100L), cursors.Take(11));
        Assert.Contains("最近 500 条", result.Status, StringComparison.Ordinal);
        Assert.Equal(result.Status, vm.CollectionStatus);
        Assert.Equal(result.Versions, vm.CollectionEvents.Select(item => item.RosterVersion));
        lock (cursors) Assert.Equal(1001L, cursors[11]);
    }

    [Fact]
    public async Task SwitchingAtoBtoA_DropsLateAResponseAndRestartsEachExamCursor()
    {
        var lateA = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstA = true;
        var calls = new List<(string Exam, long Cursor)>();
        using var handler = new Handler((request, _) =>
        {
            var exam = ExamId(request);
            var cursor = Cursor(request);
            lock (calls) calls.Add((exam, cursor));
            if (exam == "exam-a" && firstA)
            {
                firstA = false;
                return lateA.Task; // Simulate a transport that ignores cancellation.
            }
            return Task.FromResult(Success(new[] { Event(exam, exam == "exam-b" ? 3 : 2) }));
        });
        using var api = Configured(handler);
        using var vm = new MonitoringViewModel(api, TimeSpan.FromSeconds(1))
        {
            ExamId = "exam-a", IsPageActive = true, ConnectionReady = true
        };
        await UntilAsync(() => { lock (calls) return calls.Count >= 1; });
        vm.ExamId = "exam-b";
        await UntilAsync(() => vm.CollectionEvents.Any(item => item.ExamId == "exam-b"));
        lateA.SetResult(Success(new[] { Event("exam-a", 1) }));
        await Task.Delay(30);
        Assert.All(vm.CollectionEvents, item => Assert.Equal("exam-b", item.ExamId));

        vm.ExamId = "exam-a";
        await UntilAsync(() => vm.CollectionEvents.Any(item => item.ExamId == "exam-a"));
        Assert.All(vm.CollectionEvents, item => Assert.Equal("exam-a", item.ExamId));
        lock (calls) Assert.Equal(new[] { ("exam-a", 0L), ("exam-b", 0L), ("exam-a", 0L) }, calls.Take(3));
    }

    [Fact]
    public async Task SwitchingAndReconnecting_RedisplaysHistoryWithoutRepeatedReminderOnTheSameServer()
    {
        var serverId = "synthetic-server-a";
        var includeNewAEvent = false;
        using var handler = new Handler((request, _) =>
        {
            var exam = ExamId(request);
            var cursor = Cursor(request);
            var available = exam == "exam-a"
                ? includeNewAEvent ? new[] { Event(exam, 1), Event(exam, 2) } : new[] { Event(exam, 1) }
                : new[] { Event(exam, 1) };
            return Task.FromResult(Success(available.Where(item => item.RosterVersion > cursor).ToArray(), serverId));
        });
        using var api = Configured(handler);
        using var vm = new MonitoringViewModel(api) { ExamId = "exam-a" };
        var reminders = new List<CollectionCompletedNotification>();
        vm.CollectionCompleted += reminders.Add;

        await vm.RefreshCollectionEventsAsync();
        vm.ExamId = "exam-b";
        await vm.RefreshCollectionEventsAsync();
        vm.ExamId = "exam-a";
        await vm.RefreshCollectionEventsAsync();

        Assert.Equal(new[] { "exam-a", "exam-b" }, reminders.Select(item => item.ExamId));
        Assert.Equal("exam-a", Assert.Single(vm.CollectionEvents).ExamId);

        includeNewAEvent = true;
        await vm.RefreshCollectionEventsAsync();
        Assert.Equal(2, vm.CollectionEvents.Count);
        Assert.Equal(2, reminders[^1].RosterVersion);
        Assert.Equal(3, reminders.Count);

        api.Disconnect();
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        await vm.RefreshCollectionEventsAsync();
        Assert.Equal(3, reminders.Count); // Same logical server, same persisted history.

        serverId = "synthetic-server-b";
        api.Disconnect();
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        await vm.RefreshCollectionEventsAsync();
        Assert.Equal(5, reminders.Count); // 两个名单版本在另一服务器各自提醒一次。
        Assert.Equal(2, reminders[^1].RosterVersion);
    }

    [Fact]
    public async Task ManualRefresh_DeduplicatesIdenticalOutOfOrderRecordsAndRejectsConflicts()
    {
        var calls = new List<long>();
        var conflict = false;
        using var handler = new Handler((request, _) =>
        {
            var cursor = Cursor(request);
            calls.Add(cursor);
            return Task.FromResult(Success(conflict
                ? new[] { Event("exam-a", 4), Event("exam-a", 4) with { EventId = "other" } }
                : cursor == 0
                    ? new[] { Event("exam-a", 2), Event("exam-a", 1), Event("exam-a", 2) }
                    : Array.Empty<CollectionCompletedNotification>()));
        });
        using var api = Configured(handler);
        using var vm = new MonitoringViewModel(api) { ExamId = "exam-a" };
        await vm.RefreshCollectionEventsAsync();
        Assert.Equal(new long[] { 1, 2 }, vm.CollectionEvents.Select(item => item.RosterVersion));
        conflict = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => vm.RefreshCollectionEventsAsync());
        Assert.Equal(2, calls[^1]);
        Assert.Equal(2, vm.CollectionEvents.Count);
        conflict = false;
        await vm.RefreshCollectionEventsAsync();
        Assert.Equal(2, calls[^1]);
    }

    [Fact]
    public async Task ManualRefresh_CancellationAndEmptyResultDoNotAdvanceCursor()
    {
        var calls = new List<long>();
        using var handler = new Handler((request, token) =>
        {
            calls.Add(Cursor(request));
            return Task.FromResult(Success(Array.Empty<CollectionCompletedNotification>()));
        });
        using var api = Configured(handler);
        using var vm = new MonitoringViewModel(api) { ExamId = "exam-a" };
        await vm.RefreshCollectionEventsAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vm.RefreshCollectionEventsAsync(cancelled.Token));
        await vm.RefreshCollectionEventsAsync();
        Assert.All(calls, cursor => Assert.Equal(0, cursor));
        Assert.Empty(vm.CollectionEvents);
    }

    private static CollectionCompletedNotification Event(string exam, long version) =>
        new($"event-{exam}-{version}", exam, version, 300, DateTimeOffset.UnixEpoch.AddMinutes(version));

    private static string ExamId(HttpRequestMessage request) =>
        Uri.UnescapeDataString(request.RequestUri!.Query.Split("examId=")[1].Split('&')[0]);

    private static long Cursor(HttpRequestMessage request) =>
        long.Parse(request.RequestUri!.Query.Split("afterRosterVersion=")[1]);

    private static AdminApiClient Configured(HttpMessageHandler handler)
    {
        var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        return api;
    }

    private static HttpResponseMessage Success<T>(T data, string serverId = "synthetic-server") => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new ApiEnvelope<T>(serverId, "synthetic-node", 1,
            "test", data, null), options: Json)
    };

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Synthetic polling did not reach the expected state.");
            await Task.Delay(10);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            route(request, token);
    }

    [Fact]
    public async Task CollectionReminder_ArrivesWhileTheMonitoringPageIsNotOpen()
    {
        using var handler = new Handler((request, _) =>
            Task.FromResult(Success(new[] { Event("exam-a", 1) })));
        using var api = Configured(handler);
        using var vm = new MonitoringViewModel(api, TimeSpan.FromMilliseconds(25)) { ExamId = "exam-a" };
        var reminded = new List<CollectionCompletedNotification>();
        vm.CollectionCompleted += reminded.Add;

        // 连接就绪才开始轮询：没有打开监测页（IsPageActive=false）也要收到收齐提醒。
        vm.ConnectionReady = true;
        await UntilAsync(() => reminded.Count == 1);

        Assert.False(vm.IsPageActive);
        Assert.Single(vm.CollectionEvents);
        Assert.Equal("exam-a", reminded[0].ExamId);
    }

}
