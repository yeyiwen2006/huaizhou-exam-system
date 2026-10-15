using System.Net.Http;
using System.Text.Json;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;

namespace Exam.AdminUnitTests;

public sealed partial class ReviewRegressionTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DeviceDecision_AllowsEmptyNote(string note)
    {
        DeviceDecisionRequest? sent = null;
        using var handler = new AsyncHandler(async message =>
        {
            if (message.Method == HttpMethod.Get) return Success(MonitoringFixture.Default());
            sent = JsonSerializer.Deserialize<DeviceDecisionRequest>(await message.Content!.ReadAsStringAsync(), JsonDefaults.Create())!;
            return Success(new DeviceDecisionResult(sent.ParticipationId, sent.SelectedDeviceId, "device-1", 2, 4,
                sent.OperatorId, sent.Reason, DateTimeOffset.UtcNow));
        });
        using var api = Configured(handler);
        using var viewModel = DecisionViewModel(api);
        viewModel.DecisionReason = note;
        await viewModel.DecideDeviceAsync();
        Assert.NotNull(sent);
        Assert.Equal(string.Empty, sent.Reason);
        Assert.Contains("已保留设备", viewModel.Status);
        Assert.DoesNotContain("备注：", viewModel.Status);
    }

    [Fact]
    public async Task CollectionPolling_PreservesStatusUntilAResultChanges()
    {
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new AsyncHandler(_ => ++calls == 1
            ? Task.FromResult(Success(Array.Empty<CollectionCompletedNotification>())) : pending.Task);
        using var api = Configured(handler);
        using var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };
        await viewModel.RefreshCollectionEventsAsync();
        var previous = viewModel.CollectionStatus;
        var changes = new List<string>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.CollectionStatus)) changes.Add(viewModel.CollectionStatus);
        };
        var polling = viewModel.RefreshCollectionEventsAsync();
        Assert.Equal(previous, viewModel.CollectionStatus);
        pending.SetResult(Success(Array.Empty<CollectionCompletedNotification>()));
        await polling;
        Assert.Empty(changes);
    }

    [Fact]
    public async Task C04Review_DuplicateCandidateRowsAreNotAuthoritative()
    {
        using var handler = new Handler(_ => Success(MonitoringFixture.Snapshot("exam-1",
            MonitoringFixture.Participant(),
            MonitoringFixture.Participant(participationId: "other-part"))));
        using var api = Configured(handler);
        var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };
        await Assert.ThrowsAsync<AdminApiException>(() => viewModel.LoadAsync());
        Assert.Empty(viewModel.Rows);
    }

    [Fact]
    public async Task C04Review_WithdrawnCandidateCannotAppearInActualParticipationResponse()
    {
        using var handler = new Handler(_ => Success(MonitoringFixture.Snapshot("exam-1",
            MonitoringFixture.Participant(businessState: ParticipationBusinessState.NotParticipating))));
        using var api = Configured(handler);
        var viewModel = new MonitoringViewModel(api) { ExamId = "exam-1" };
        await Assert.ThrowsAsync<AdminApiException>(() => viewModel.LoadAsync());
        Assert.Empty(viewModel.Rows);
    }

    [Fact]
    public async Task C04Review_LostDecisionResponseReusesTheOriginalRequestIdentity()
    {
        var requests = new List<DeviceDecisionRequest>();
        using var handler = new AsyncHandler(async message =>
        {
            if (message.Method == HttpMethod.Get) return Success(MonitoringFixture.Default());
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
                return Task.FromResult(Success(MonitoringFixture.Default()));
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
                return Success(MonitoringFixture.Default());
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
        Assert.Contains("new synthetic reason", newerStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("device-3", newerStatus, StringComparison.Ordinal);
        firstRefresh.SetResult(Success(MonitoringFixture.Default()));
        await older;
        Assert.Equal(newerStatus, viewModel.Status);
        Assert.Equal("5", viewModel.ExpectedVersionText);
    }

    [Fact]
    public async Task C04Review_DecisionReceiptRequiresAnActualDecisionTimestamp()
    {
        using var handler = new Handler(message => message.Method == HttpMethod.Get
            ? Success(MonitoringFixture.Default())
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
