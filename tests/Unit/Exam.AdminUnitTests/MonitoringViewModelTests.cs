using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;

namespace Exam.AdminUnitTests;

public sealed class MonitoringViewModelTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();

    [Fact]
    public async Task Load_MapsActualParticipationStatesWithoutInventingConnectionState()
    {
        var states = Enum.GetValues<ParticipationBusinessState>()
            .Where(state => state != ParticipationBusinessState.NotParticipating);
        var participations = states.Select((state, index) =>
            new ParticipationResult($"part-{index}", "exam-1", $"candidate-{index}", state,
                $"device-{index}", 1, 1, 1, null)).ToArray();
        using var handler = new SyntheticHandler(_ => Success(participations));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };

        await viewModel.LoadAsync();

        Assert.Equal(new[] { "候考", "作答中", "已交卷" },
            viewModel.Rows.Select(row => row.BusinessState));
        Assert.All(viewModel.Rows, row => Assert.Equal("—", row.ConnectionState));
        Assert.Contains("不代表离线", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal("已提交", viewModel.Rows[^1].CollectionState);
    }

    [Fact]
    public async Task Load_EmptyResultAndNetworkFailureDoNotManufactureRows()
    {
        var fail = false;
        using var handler = new SyntheticHandler(_ => fail
            ? throw new HttpRequestException("synthetic unavailable")
            : Success(Array.Empty<ParticipationResult>()));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };

        await viewModel.LoadAsync();
        Assert.Empty(viewModel.Rows);
        fail = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => viewModel.LoadAsync());
        Assert.Empty(viewModel.Rows);
    }

    [Fact]
    public void Filter_HandlesThreeHundredRowsAndRapidChanges()
    {
        using var api = new AdminApiClient();
        var viewModel = new MonitoringViewModel(api);
        foreach (var index in Enumerable.Range(1, 300))
        {
            viewModel.Rows.Add(new MonitoringRow($"part-{index:D3}", $"candidate-{index:D3}",
                index % 2 == 0 ? "已交卷" : "候考", $"device-{index:D3}",
                1, 1, 1, index % 2 == 0 ? "已提交" : "未提交", "接口未提供", "接口未提供"));
        }

        for (var index = 1; index <= 300; index++)
        {
            viewModel.FilterText = $"candidate-{index:D3}";
            Assert.Single(viewModel.FilteredRows);
        }
        viewModel.FilterText = string.Empty;
        viewModel.OnlySubmitted = true;
        Assert.Equal(150, viewModel.FilteredRows.Count());
        viewModel.OnlySubmitted = false;
        Assert.Equal(300, viewModel.FilteredRows.Count());
    }

    [Fact]
    public void ChangingExam_ClearsRowsAndCollectionEvents()
    {
        using var api = new AdminApiClient();
        var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };
        viewModel.Rows.Add(new MonitoringRow("part-1", "candidate-1", "候考", "device-1",
            1, 1, 1, "未提交", "接口未提供", "接口未提供"));
        viewModel.CollectionEvents.Add(new CollectionCompletedNotification(
            "event-1", "exam-1", 1, 1, DateTimeOffset.UtcNow));

        viewModel.ExamId = "exam-2";

        Assert.Empty(viewModel.Rows);
        Assert.Empty(viewModel.CollectionEvents);
        Assert.Contains("已切换", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectionEvents_AdvanceCursorAndDeduplicatePerExam()
    {
        var allEvents = Enumerable.Range(1, 101)
            .Select(index => new CollectionCompletedNotification(
                $"event-{index}", "exam-1", index, 300, DateTimeOffset.UtcNow))
            .ToArray();
        var calls = new List<long>();
        using var handler = new SyntheticHandler(request =>
        {
            var query = request.RequestUri!.Query;
            var cursor = long.Parse(query.Split("afterRosterVersion=")[1]);
            calls.Add(cursor);
            return Success(allEvents.Where(item => item.RosterVersion > cursor).Take(100).ToArray());
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };

        await viewModel.RefreshCollectionEventsAsync();
        await viewModel.RefreshCollectionEventsAsync();

        Assert.Equal(new long[] { 0, 100, 101 }, calls);
        Assert.Equal(101, viewModel.CollectionEvents.Count);
        Assert.Contains("重新统计", viewModel.CollectionStatus, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Success<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new ApiEnvelope<T>(
            "synthetic-server", "synthetic-node", 1, "test", value, null), options: Json)
    };

    private sealed class SyntheticHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(route(request));
    }
}
