using System.Net.Http;
using System.Text.Json;
using Exam.Admin.ViewModels;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;

namespace Exam.AdminUnitTests;

public sealed partial class ReviewRegressionTests
{
    [Fact]
    public async Task C04Review_DuplicateCandidateRowsAreNotAuthoritative()
    {
        using var handler = new Handler(_ => Success(MonitoringFixture.Snapshot(new[]
        {
            Participation("exam-1"), Participation("exam-1") with { ParticipationId = "other-part" }
        })));
        using var api = Configured(handler);
        var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.LoadAsync());
        Assert.Empty(viewModel.Rows);
    }

    [Fact]
    public async Task C04Review_WithdrawnCandidateCannotAppearInActualParticipationResponse()
    {
        using var handler = new Handler(_ => Success(MonitoringFixture.Snapshot(new[]
        {
            Participation("exam-1") with { BusinessState = ParticipationBusinessState.NotParticipating }
        })));
        using var api = Configured(handler);
        var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.LoadAsync());
        Assert.Empty(viewModel.Rows);
    }

    [Fact]
    public async Task C04Review_LostDecisionResponseReusesTheOriginalRequestIdentity()
    {
        var requests = new List<DeviceDecisionRequest>();
        using var handler = new AsyncHandler(async message =>
        {
            if (message.Method == HttpMethod.Get) return Success(MonitoringFixture.Snapshot(new[] { Participation("exam-1") }));
            var request = JsonSerializer.Deserialize<DeviceDecisionRequest>(
                await message.Content!.ReadAsStringAsync(), JsonDefaults.Create())!;
            requests.Add(request);
            if (requests.Count == 1) throw new HttpRequestException("synthetic lost response");
            return Success(new DeviceDecisionResult(request.ParticipationId, request.SelectedDeviceId, "device-1", 2, 4,
                request.OperatorId, request.Reason, DateTimeOffset.UtcNow));
        });
        using var api = Configured(handler);
        var viewModel = DecisionViewModel(api);
        await Assert.ThrowsAsync<HttpRequestException>(() => viewModel.DecideDeviceAsync());
        await viewModel.DecideDeviceAsync();
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests[0], requests[1]);
        Assert.Equal("4", viewModel.ExpectedVersionText);
    }

    [Fact]
    public async Task C04Review_DecisionResponseCannotOverwriteAnotherSelectedCandidatesVersion()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshes = 0;
        using var handler = new AsyncHandler(message =>
        {
            if (message.Method == HttpMethod.Get)
            {
                refreshes++;
                return Task.FromResult(Success(MonitoringFixture.Snapshot(new[] { Participation("exam-1") })));
            }
            return release.Task;
        });
        using var api = Configured(handler);
        var viewModel = DecisionViewModel(api);
        var pending = viewModel.DecideDeviceAsync();
        var selected = new MonitoringRow("part-new", "candidate-new", "候考", "device-new", 1, 7, 9,
            "未提交", "接口未提供", "接口未提供");
        viewModel.SelectedRow = selected;
        release.SetResult(Success(new DeviceDecisionResult("part-1", "device-2", "device-1", 2, 4,
            "operator-1", "synthetic reason", DateTimeOffset.UtcNow)));
        await pending;
        Assert.Equal("9", viewModel.ExpectedVersionText);
        Assert.Same(selected, viewModel.SelectedRow);
        Assert.Equal(0, refreshes);
    }

    [Fact]
    public async Task C04Review_ResendResponseCannotClaimSuccessForAnEditedEvent()
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new AsyncHandler(_ => release.Task);
        using var api = Configured(handler);
        var viewModel = ResendViewModel(api);
        var pending = viewModel.ResendAsync();
        viewModel.EventId = "other-event";
        release.SetResult(Success(CommandEvent("exam-1")));
        await pending;
        Assert.DoesNotContain("已重发指令", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task C04Review_OlderDecisionRefreshCannotOverwriteNewerDecisionFeedback()
    {
        var firstRefresh = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRefreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshCount = 0;
        using var handler = new AsyncHandler(async message =>
        {
            if (message.Method == HttpMethod.Get)
            {
                if (++refreshCount == 1)
                {
                    firstRefreshStarted.SetResult();
                    return await firstRefresh.Task;
                }
                return Success(MonitoringFixture.Snapshot(new[] { Participation("exam-1") }));
            }
            var request = JsonSerializer.Deserialize<DeviceDecisionRequest>(
                await message.Content!.ReadAsStringAsync(), JsonDefaults.Create())!;
            return Success(new DeviceDecisionResult(request.ParticipationId, request.SelectedDeviceId, "device-1", 2,
                request.ExpectedVersion + 1, request.OperatorId, request.Reason, DateTimeOffset.UtcNow));
        });
        using var api = Configured(handler);
        var viewModel = DecisionViewModel(api);
        var older = viewModel.DecideDeviceAsync();
        await firstRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.SelectedDeviceId = "device-3";
        viewModel.DecisionReason = "new synthetic reason";
        await viewModel.DecideDeviceAsync();
        var newerStatus = viewModel.Status;
        Assert.Contains("设备选择已保存", newerStatus, StringComparison.Ordinal);
        firstRefresh.SetResult(Success(MonitoringFixture.Snapshot(new[] { Participation("exam-1") })));
        await older;
        Assert.Equal(newerStatus, viewModel.Status);
        Assert.Equal("5", viewModel.ExpectedVersionText);
    }

    [Fact]
    public async Task C04Review_DecisionReceiptRequiresAnActualDecisionTimestamp()
    {
        using var handler = new Handler(message => message.Method == HttpMethod.Get
            ? Success(MonitoringFixture.Snapshot(new[] { Participation("exam-1") }))
            : Success(new DeviceDecisionResult("part-1", "device-2", "device-1", 2, 4,
                "operator-1", "synthetic reason", default)));
        using var api = Configured(handler);
        var viewModel = DecisionViewModel(api);
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.DecideDeviceAsync());
        Assert.Equal("3", viewModel.ExpectedVersionText);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C04Review_FormChangesInvalidateLateErrorsAndEvenChangeBackResponses(bool failed)
    {
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new AsyncHandler(_ => release.Task);
        using var api = Configured(handler);
        var viewModel = ResendViewModel(api);
        var pending = viewModel.ResendAsync();
        viewModel.EventId = "other-event";
        viewModel.EventId = "event-1";
        var status = viewModel.Status;
        if (failed) release.SetException(new HttpRequestException("synthetic stale failure"));
        else release.SetResult(Success(CommandEvent("exam-1")));
        await pending;
        Assert.Equal(status, viewModel.Status);
    }

    [Fact]
    public async Task C04Review_DecisionCannotAuthorizeAndRevokeTheSameDevice()
    {
        using var handler = new Handler(_ => Success(new DeviceDecisionResult("part-1", "device-2", "device-2", 2, 4,
            "operator-1", "synthetic reason", DateTimeOffset.UtcNow)));
        using var api = Configured(handler);
        var viewModel = DecisionViewModel(api);
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.DecideDeviceAsync());
        Assert.Equal("3", viewModel.ExpectedVersionText);
    }

}
