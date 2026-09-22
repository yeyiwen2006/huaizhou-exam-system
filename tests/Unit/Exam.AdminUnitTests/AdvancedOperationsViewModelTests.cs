using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Common;
using Exam.Contracts.Versioning;

namespace Exam.AdminUnitTests;

public sealed class AdvancedOperationsViewModelTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();

    [Fact]
    public async Task ImportRevision_LoadsRealVersionSnapshotWithoutEquatingPublicationToClientUpdate()
    {
        var now = DateTimeOffset.UtcNow;
        var paper = new PaperPublicationInfo(2, "artifact-synthetic", new PaperStructure([]), [],
            now, "operator-1", "synthetic revision");
        var participant = new ParticipantVersionStatus("participant-1", ParticipationBusinessState.Answering,
            2, 1, PaperUpdateState.DownloadFailed, "synthetic download error");
        var snapshot = new ExamVersionSnapshot("exam-1", new VersionVector(3, 2, 1),
            new ScheduleWindow(now, now.AddHours(1)), ExamLifecycle.Running, now, null,
            paper, null, new Dictionary<string, ParticipantVersionStatus> { [participant.ParticipantId] = participant });
        using var handler = new SyntheticHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ApiEnvelope<ExamVersionSnapshot>(
                "synthetic-server", "synthetic-node", 1, "test", snapshot, null), options: Json)
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        var viewModel = new ImportRevisionViewModel(api) { ExamId = "exam-1" };

        await viewModel.LoadVersionAsync();

        Assert.Equal("3", viewModel.ScheduleVersion);
        Assert.Equal("2", viewModel.PaperVersion);
        Assert.Contains("客户端更新须逐台确认", viewModel.PublicationState, StringComparison.Ordinal);
        Assert.Equal(PaperUpdateState.DownloadFailed, Assert.Single(viewModel.ParticipantUpdates).PaperUpdateState);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task ImportRevision_ServerFailureRetainsErrorAndEndsLoading()
    {
        using var handler = new SyntheticHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = JsonContent.Create(new ApiEnvelope<object>(
                "synthetic-server", "synthetic-node", 1, "test", null,
                new ApiError("SERVICE_RECOVERING", "Synthetic recovering")), options: Json)
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        var viewModel = new ImportRevisionViewModel(api) { ExamId = "exam-1" };

        await viewModel.LoadVersionAsync();

        Assert.Contains("SERVICE_RECOVERING", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal("未查询", viewModel.PaperVersion);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public async Task ImportRevision_FailedRefreshClearsPreviouslyLoadedSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new ExamVersionSnapshot("exam-1", new VersionVector(3, 2, 1),
            new ScheduleWindow(now, now.AddHours(1)), ExamLifecycle.Running, now, null,
            null, null, new Dictionary<string, ParticipantVersionStatus>());
        var callCount = 0;
        using var handler = new SyntheticHandler(_ => ++callCount == 1
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ApiEnvelope<ExamVersionSnapshot>(
                    "synthetic-server", "synthetic-node", 1, "test", snapshot, null), options: Json)
            }
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = JsonContent.Create(new ApiEnvelope<object>(
                    "synthetic-server", "synthetic-node", 1, "test", null,
                    new ApiError("SERVICE_RECOVERING", "Synthetic recovering")), options: Json)
            });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        var viewModel = new ImportRevisionViewModel(api) { ExamId = "exam-1" };

        await viewModel.LoadVersionAsync();
        Assert.Equal("2", viewModel.PaperVersion);
        await viewModel.LoadVersionAsync();

        Assert.Equal("未查询", viewModel.PaperVersion);
        Assert.Contains("SERVICE_RECOVERING", viewModel.Status, StringComparison.Ordinal);
        Assert.False(viewModel.IsLoading);
    }

    [Fact]
    public void Reports_KeepFiveReadinessStatesDistinct()
    {
        var viewModel = new ReportsWorkspaceViewModel();
        Assert.Equal(5, viewModel.ReportTypes.Count);
        Assert.Contains("接口未提供", viewModel.AvailabilityText, StringComparison.Ordinal);

        var texts = Enum.GetValues<ReportAvailability>()
            .Select(availability => { viewModel.Availability = availability; return viewModel.AvailabilityText; })
            .ToArray();

        Assert.Equal(texts.Length, texts.Distinct(StringComparer.Ordinal).Count());
    }

    private sealed class SyntheticHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(route(request));
    }
}
