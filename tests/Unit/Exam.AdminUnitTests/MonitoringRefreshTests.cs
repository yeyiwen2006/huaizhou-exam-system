using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Administration;
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
        await UntilAsync(() => viewModel.Status.Contains("自动刷新失败", StringComparison.Ordinal));
        // 自动刷新失败不得清空列表：显示的是上一次可信结果，并标明读取时间。
        Assert.Single(viewModel.Rows);
        Assert.Contains("读取的状态", viewModel.Status, StringComparison.Ordinal);
        Assert.NotEmpty(viewModel.AsOfText);

        failing = false;
        await UntilAsync(() => viewModel.Status.Contains("自动刷新已恢复", StringComparison.Ordinal));
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
