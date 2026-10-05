using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Administration;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;

namespace Exam.AdminUnitTests;

/// <summary>
/// C04 第 12 项中与传输机制无关的部分：监测页在前台时按周期自动读取权威状态，
/// 失败保留上一次可信结果并标注陈旧，且不覆盖考务的操作反馈。
/// 现场时延与多机并发仍由 T01/T02 核对。
/// </summary>
public sealed class MonitoringRefreshTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();

    [Fact]
    public async Task AutoRefresh_ReadsAuthoritativeSnapshotWhileThePageIsActive()
    {
        var calls = 0;
        using var handler = new SyntheticHandler(_ =>
        {
            var index = Interlocked.Increment(ref calls);
            return Success(MonitoringFixture.Snapshot("exam-1", Enumerable.Range(1, Math.Min(index, 3))
                .Select(number => MonitoringFixture.Participant(
                    participationId: $"part-{number}", candidateId: $"candidate-{number}"))
                .ToArray()));
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api,
            collectionPollInterval: TimeSpan.FromMinutes(5), monitoringRefreshInterval: TimeSpan.FromMilliseconds(25))
        { ExamId = "exam-1" };

        viewModel.ConnectionReady = true;
        viewModel.IsPageActive = true;
        await UntilAsync(() => Volatile.Read(ref calls) >= 3);
        await UntilAsync(() => viewModel.Rows.Count == 3);

        // 页面在前台时无需手动刷新：列表跟随权威快照自动更新。
        Assert.True(Volatile.Read(ref calls) >= 3);
        Assert.Equal(3, viewModel.Rows.Count);
        Assert.Equal("实际参试 3 人；已回收 0 份；未回收 3 份。", viewModel.CountsText);
    }

    [Fact]
    public async Task AutoRefresh_StopsOnPageExitDisconnectAndDispose()
    {
        var calls = 0;
        using var handler = new SyntheticHandler(_ =>
        {
            Interlocked.Increment(ref calls);
            return Success(MonitoringFixture.Default());
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        var viewModel = new MonitoringViewModel(api,
            collectionPollInterval: TimeSpan.FromMinutes(5), monitoringRefreshInterval: TimeSpan.FromMilliseconds(25))
        { ExamId = "exam-1", ConnectionReady = true, IsPageActive = true };
        await UntilAsync(() => Volatile.Read(ref calls) >= 2);

        viewModel.IsPageActive = false;
        var stopped = Volatile.Read(ref calls);
        await Task.Delay(120);
        Assert.Equal(stopped, Volatile.Read(ref calls));

        viewModel.IsPageActive = true;
        await UntilAsync(() => Volatile.Read(ref calls) > stopped);
        api.Disconnect();
        stopped = Volatile.Read(ref calls);
        await Task.Delay(120);
        Assert.Equal(stopped, Volatile.Read(ref calls));

        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        viewModel.ConnectionReady = true;
        await UntilAsync(() => Volatile.Read(ref calls) > stopped);
        viewModel.Dispose();
        stopped = Volatile.Read(ref calls);
        await Task.Delay(120);
        Assert.Equal(stopped, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task AutoRefresh_KeepsLastTrustedRowsAndMarksThemStaleOnFailure()
    {
        var failing = false;
        using var handler = new SyntheticHandler(_ => failing
            ? throw new HttpRequestException("synthetic outage")
            : Success(MonitoringFixture.Default()));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api,
            collectionPollInterval: TimeSpan.FromMinutes(5), monitoringRefreshInterval: TimeSpan.FromMilliseconds(25))
        { ExamId = "exam-1" };

        viewModel.ConnectionReady = true;
        viewModel.IsPageActive = true;
        await UntilAsync(() => viewModel.Rows.Count == 1);

        failing = true;
        await UntilAsync(() => viewModel.RefreshNote.Contains("自动刷新失败", StringComparison.Ordinal));
        // 自动刷新失败不得清空列表：显示的是上一次可信结果，并标明读取时间。
        Assert.Single(viewModel.Rows);
        Assert.Contains("读取的状态", viewModel.RefreshNote, StringComparison.Ordinal);
        Assert.NotEmpty(viewModel.AsOfText);

        failing = false;
        await UntilAsync(() => viewModel.RefreshNote.Length == 0);
        Assert.Single(viewModel.Rows);
    }

    [Fact]
    public async Task AutoRefresh_DoesNotOverlapSlowResponses()
    {
        using var handler = new SlowMonitoringHandler(TimeSpan.FromMilliseconds(150));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api,
            collectionPollInterval: TimeSpan.FromMinutes(5), monitoringRefreshInterval: TimeSpan.FromMilliseconds(20))
        { ExamId = "exam-1", ConnectionReady = true, IsPageActive = true };

        await Task.Delay(600);

        // 慢响应不得叠加请求：上一轮结束前不发下一轮。
        Assert.Equal(1, handler.MaxConcurrent);
        Assert.True(handler.Calls >= 2, $"自动刷新应至少完成两轮，实际 {handler.Calls} 轮。");
    }

    [Fact]
    public async Task AutoRefresh_DoesNotOverwriteOperatorFeedback()
    {
        var snapshot = MonitoringFixture.Snapshot("exam-1", MonitoringFixture.Participant(
            conflicts: [MonitoringFixture.Conflict("device-part-1", authorized: true)]));
        using var handler = new SyntheticHandler(request => request.Method == HttpMethod.Post
            ? Success(new DeviceDecisionResult("part-1", "device-part-1", "device-9", 2, 4,
                "operator-1", "合成示例原因", DateTimeOffset.UtcNow))
            : Success(snapshot));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api,
            collectionPollInterval: TimeSpan.FromMinutes(5), monitoringRefreshInterval: TimeSpan.FromMilliseconds(30))
        {
            ExamId = "exam-1", ConnectionReady = true, IsPageActive = true,
            ParticipationId = "part-1", SelectedDeviceId = "device-part-1", ExpectedVersionText = "3",
            DecisionReason = "合成示例原因",
        };
        await UntilAsync(() => viewModel.Rows.Count == 1);

        await viewModel.DecideDeviceAsync();
        Assert.Contains("已保留设备", viewModel.Status, StringComparison.Ordinal);

        // 自动刷新继续跑，但不得把操作反馈改写成计数汇总。
        await Task.Delay(150);
        Assert.Contains("已保留设备", viewModel.Status, StringComparison.Ordinal);
        Assert.Single(viewModel.Rows);
    }

    [Fact]
    public async Task AutoRefresh_KeepsTheOperatorsSelectionAndInputs()
    {
        using var handler = new SyntheticHandler(_ => Success(MonitoringFixture.Default()));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api,
            collectionPollInterval: TimeSpan.FromMinutes(5), monitoringRefreshInterval: TimeSpan.FromMilliseconds(25))
        { ExamId = "exam-1", ConnectionReady = true, IsPageActive = true };
        await UntilAsync(() => viewModel.Rows.Count == 1);

        viewModel.SelectedRow = viewModel.Rows[0];
        viewModel.DecisionReason = "合成原因";
        var selected = viewModel.SelectedRow;
        await Task.Delay(200);

        // 自动刷新重建行对象后仍要保住选中与操作者填写的输入，否则补传/裁决会被每 2 秒一次的刷新打断。
        Assert.NotNull(viewModel.SelectedRow);
        Assert.Equal(selected!.ParticipationId, viewModel.SelectedRow!.ParticipationId);
        Assert.Equal(selected.ParticipationId, viewModel.ParticipationId);
        Assert.Equal("合成原因", viewModel.DecisionReason);
    }

    [Fact]
    public async Task AutoRefresh_DoesNotDiscardAResendResponseArrivingAfterATick()
    {
        var released = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = MonitoringFixture.Snapshot("exam-1", MonitoringFixture.Participant(
            commands: [MonitoringFixture.Command(CommandEventType.End, canResend: true, eventId: "end-1")]));
        using var handler = new AsyncHandler(request => request.Method == HttpMethod.Post
            ? released.Task
            : Task.FromResult(Success(snapshot)));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api,
            collectionPollInterval: TimeSpan.FromMinutes(5), monitoringRefreshInterval: TimeSpan.FromMilliseconds(20))
        { ExamId = "exam-1", ConnectionReady = true, IsPageActive = true };
        await UntilAsync(() => viewModel.Rows.Count == 1);
        viewModel.SelectedRow = viewModel.Rows[0];

        var pending = viewModel.ResendAsync();
        // 回执到达之前至少发生一次自动刷新；命令已经送达，考务必须看到成功提示。
        await Task.Delay(100);
        released.SetResult(Success(new CommandEventView("end-1", "exam-1", "part-1", 1, CommandEventType.End, 1,
            "device-part-1", 2, null, MonitoringFixture.At, false, 2)));
        await pending;

        Assert.Contains("已重发指令", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoRefresh_RecoveryNoticeDoesNotOverwriteOperatorFeedback()
    {
        var failing = false;
        var snapshot = MonitoringFixture.Snapshot("exam-1", MonitoringFixture.Participant(
            commands: [MonitoringFixture.Command(CommandEventType.End, canResend: true, eventId: "end-1")]));
        using var handler = new AsyncHandler(request => Task.FromResult(request.Method == HttpMethod.Post
            ? Success(new CommandEventView("end-1", "exam-1", "part-1", 1, CommandEventType.End, 1,
                "device-part-1", 2, null, MonitoringFixture.At, false, 2))
            : failing ? throw new HttpRequestException("synthetic outage") : Success(snapshot)));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api,
            collectionPollInterval: TimeSpan.FromMinutes(5), monitoringRefreshInterval: TimeSpan.FromMilliseconds(25))
        { ExamId = "exam-1", ConnectionReady = true, IsPageActive = true };
        await UntilAsync(() => viewModel.Rows.Count == 1);
        failing = true;
        await UntilAsync(() => viewModel.RefreshNote.Contains("自动刷新失败", StringComparison.Ordinal));

        // 刷新失败期间做一次补传：失败与恢复都只写新鲜度提示，状态栏的操作反馈必须保留。
        viewModel.SelectedRow = viewModel.Rows[0];
        await viewModel.RequestReuploadAsync();
        Assert.Contains("已重发指令", viewModel.Status, StringComparison.Ordinal);
        Assert.Contains("自动刷新失败", viewModel.RefreshNote, StringComparison.Ordinal);
        failing = false;
        await UntilAsync(() => viewModel.RefreshNote.Length == 0);

        Assert.Contains("已重发指令", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoRefresh_KeepsSelectedCommandAndConflictDeviceAcrossRefreshes()
    {
        var snapshot = MonitoringFixture.Snapshot("exam-1", MonitoringFixture.Participant(
            commands:
            [
                MonitoringFixture.Command(CommandEventType.Start, canResend: false, eventId: "start-1",
                    confirmation: "已执行", acknowledgedAt: MonitoringFixture.At),
                MonitoringFixture.Command(CommandEventType.End, canResend: true, eventId: "end-1"),
            ],
            conflicts:
            [
                MonitoringFixture.Conflict("device-part-1", authorized: true),
                MonitoringFixture.Conflict("synthetic-device-17", authorized: false,
                    connectionState: DeviceConnectionState.Offline, answerVersion: 2, page: "交卷确认"),
            ]));
        using var handler = new SyntheticHandler(_ => Success(snapshot));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api,
            collectionPollInterval: TimeSpan.FromMinutes(5), monitoringRefreshInterval: TimeSpan.FromMilliseconds(25))
        { ExamId = "exam-1", ConnectionReady = true, IsPageActive = true };
        await UntilAsync(() => viewModel.Rows.Count == 1);
        viewModel.SelectedRow = viewModel.Rows[0];
        viewModel.SelectedCommand = viewModel.SelectedCommands.Single(item => item.Command.EventId == "end-1");
        viewModel.SelectedConflictDevice = viewModel.SelectedConflictDevices
            .Single(item => item.DeviceId == "synthetic-device-17");
        await Task.Delay(150);

        // 自动刷新只换行对象：已选中的指令与对照设备要选回新实例，
        // 否则下拉框与对照表会变空，而 EventId/SelectedDeviceId 仍是“已武装”状态。
        Assert.NotNull(viewModel.SelectedCommand);
        Assert.Equal("end-1", viewModel.SelectedCommand!.Command.EventId);
        Assert.True(viewModel.SelectedCommand.IsSelected);
        Assert.NotNull(viewModel.SelectedConflictDevice);
        Assert.Equal("synthetic-device-17", viewModel.SelectedConflictDevice!.DeviceId);
        Assert.True(viewModel.SelectedConflictDevice.IsSelected);
        Assert.Equal("end-1", viewModel.EventId);
        Assert.Equal("synthetic-device-17", viewModel.SelectedDeviceId);
    }

    [Fact]
    public async Task AutoRefresh_ReplacesThePlaceholderStatusOnceDataArrives()
    {
        using var handler = new SyntheticHandler(_ => Success(MonitoringFixture.Default()));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api,
            collectionPollInterval: TimeSpan.FromMinutes(5), monitoringRefreshInterval: TimeSpan.FromMilliseconds(25))
        { ExamId = "exam-1" };

        // 进入考试后状态栏还是占位说明，自动刷新读到数据就要换成计数汇总，不能继续叫考务点“刷新”。
        viewModel.ConnectionReady = true;
        viewModel.IsPageActive = true;
        await UntilAsync(() => viewModel.Rows.Count == 1);

        Assert.DoesNotContain("请点击", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal("共 1 名实际参试考生；已回收 0 份；未回收 1 份。", viewModel.Status);
    }

    [Fact]
    public async Task Load_RejectedSnapshotExplainsTheKeptRowsInTheRefreshNote()
    {
        using var handler = new SyntheticHandler(_ => Success(MonitoringFixture.Default() with { ActualCount = 2 }));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };
        viewModel.Rows.Add(new MonitoringRow("existing", "candidate-existing", "候考", "device-existing", 1, 1, 1,
            "未回收", "暂无记录", "—"));

        await Assert.ThrowsAsync<AdminApiException>(() => viewModel.LoadAsync());

        // “已保留上一次结果”写在新鲜度提示里：命令的错误处理只写状态栏，不会把它覆盖掉。
        Assert.Contains("已保留上一次结果", viewModel.RefreshNote, StringComparison.Ordinal);
        Assert.Equal("existing", Assert.Single(viewModel.Rows).ParticipationId);
    }

    [Fact]
    public async Task AutoRefresh_SurvivesAnOutOfRangeFocusDuration()
    {
        // 设备上报的失焦时长只校验过非负：极大值只能影响该列，不能把整页拖成失败。
        using var handler = new SyntheticHandler(_ => Success(MonitoringFixture.Snapshot("exam-1",
            MonitoringFixture.Participant(focusLossCount: 1, focusLossMilliseconds: long.MaxValue))));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        using var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };

        await viewModel.LoadAsync();

        Assert.StartsWith("1 次 / 共 ", Assert.Single(viewModel.Rows).FocusSummary, StringComparison.Ordinal);
        Assert.Equal("实际参试 1 人；已回收 0 份；未回收 1 份。", viewModel.CountsText);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            Interlocked.CompareExchange(ref target, value, current);
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 600 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), "等待自动刷新条件超时。");
    }

    private static HttpResponseMessage Success<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new ApiEnvelope<T>(
            "synthetic-server", "synthetic-node", 1, "test", value, null), options: Json),
    };

    private sealed class SyntheticHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(route(request));
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => route(request);
    }

    /// <summary>监测快照响应很慢的处理器：用于验证自动刷新不会叠加在途请求。</summary>
    private sealed class SlowMonitoringHandler(TimeSpan delay) : HttpMessageHandler
    {
        private int _inFlight;
        public int MaxConcurrent { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/monitoring", StringComparison.Ordinal))
                return Success(Array.Empty<CollectionCompletedNotification>());
            var current = Interlocked.Increment(ref _inFlight);
            if (current > MaxConcurrent) MaxConcurrent = current;
            Calls++;
            try
            {
                await Task.Delay(delay, cancellationToken);
                return Success(MonitoringFixture.Default());
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
