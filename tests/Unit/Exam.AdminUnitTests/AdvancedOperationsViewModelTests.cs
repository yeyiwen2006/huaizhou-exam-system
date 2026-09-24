using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Common;
using Exam.Contracts.Importing;
using Exam.Contracts.Versioning;

namespace Exam.AdminUnitTests;

public sealed class AdvancedOperationsViewModelTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();

    [Fact]
    public void ImportRevision_ValidSyntheticManifestIsOnlyStaticPreflight()
    {
        using var api = new AdminApiClient();
        var viewModel = new ImportRevisionViewModel(api) { ExamId = "synthetic-minimal" };

        viewModel.ValidateManifestJson(File.ReadAllText(SampleManifest("minimal")));

        Assert.True(viewModel.StaticPreflightPassed);
        Assert.Empty(viewModel.PreflightIssues);
        Assert.Contains("答案关联和逐题满分核对未完成", viewModel.PreflightStatus, StringComparison.Ordinal);
        Assert.Contains("编译", viewModel.PreflightStatus, StringComparison.Ordinal);
        Assert.Contains("正式发布仍未完成", viewModel.PreflightStatus, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("type-invalid", ManifestRuleIds.GroupTypeInvalid, "/groups/0/type")]
    [InlineData("expression-syntax", ManifestRuleIds.ScoreExpressionInvalid, "/groups/0/scoring")]
    public void ImportRevision_UsesD01RulesAndPointersForSyntheticErrors(
        string caseName, string expectedRule, string pointerPrefix)
    {
        using var api = new AdminApiClient();
        var viewModel = new ImportRevisionViewModel(api) { ExamId = "synthetic-error-case" };

        viewModel.ValidateManifestJson(File.ReadAllText(SampleManifest("errors", caseName)));

        Assert.False(viewModel.StaticPreflightPassed);
        Assert.Contains(viewModel.PreflightIssues, issue =>
            issue.Rule == expectedRule && issue.Location.StartsWith(pointerPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void ImportRevision_MalformedAndMissingFieldAreRejectedWithoutStaleIssues()
    {
        using var api = new AdminApiClient();
        var viewModel = new ImportRevisionViewModel(api) { ExamId = "synthetic-minimal" };
        viewModel.ValidateManifestJson("{");
        Assert.Contains(viewModel.PreflightIssues, issue => issue.Rule == ManifestRuleIds.InvalidJson);

        var missing = JsonNode.Parse(File.ReadAllText(SampleManifest("minimal")))!;
        missing.AsObject().Remove("examName");
        viewModel.ValidateManifestJson(missing.ToJsonString());

        Assert.False(viewModel.StaticPreflightPassed);
        Assert.Contains(viewModel.PreflightIssues, issue =>
            issue.Rule == ManifestRuleIds.RequiredMissing && issue.Location == "/examName");
        Assert.DoesNotContain(viewModel.PreflightIssues, issue => issue.Rule == ManifestRuleIds.InvalidJson);
    }

    [Fact]
    public void ImportRevision_ExamSwitchAndRepeatPreflightDoNotKeepOldResult()
    {
        using var api = new AdminApiClient();
        var viewModel = new ImportRevisionViewModel(api) { ExamId = "synthetic-minimal" };
        var valid = File.ReadAllText(SampleManifest("minimal"));
        viewModel.ValidateManifestJson(valid);
        viewModel.ValidateManifestJson(valid);
        Assert.True(viewModel.StaticPreflightPassed);
        Assert.Empty(viewModel.PreflightIssues);

        viewModel.ExamId = "other-exam";
        Assert.False(viewModel.StaticPreflightPassed);
        Assert.Empty(viewModel.PreflightIssues);
        viewModel.ValidateManifestJson(valid);
        Assert.False(viewModel.StaticPreflightPassed);
        Assert.Contains("考试 ID 不一致", viewModel.PreflightStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportRevision_FilePickerRejectsAnswerFileBeforeReading()
    {
        using var api = new AdminApiClient();
        var viewModel = new ImportRevisionViewModel(api,
            new FixedManifestDialog(Path.Combine("server-only", "answer-key.json")))
        { ExamId = "synthetic-minimal" };

        await viewModel.SelectManifestAsync();

        Assert.False(viewModel.StaticPreflightPassed);
        Assert.Empty(viewModel.PreflightIssues);
        Assert.Contains("独立答案不在考务端", viewModel.PreflightStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportRevision_SelectedSyntheticManifestUsesTheSameD01Preflight()
    {
        using var api = new AdminApiClient();
        var viewModel = new ImportRevisionViewModel(api,
            new FixedManifestDialog(SampleManifest("minimal"))) { ExamId = "synthetic-minimal" };

        await viewModel.SelectManifestAsync();
        await viewModel.SelectManifestAsync();

        Assert.True(viewModel.StaticPreflightPassed);
        Assert.Empty(viewModel.PreflightIssues);
        Assert.False(viewModel.IsPreflighting);
        Assert.Contains("未完成", viewModel.PreflightStatus, StringComparison.Ordinal);
    }

    private static string SampleManifest(params string[] segments)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "samples", "synthetic")))
            root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine([root.FullName, "samples", "synthetic", .. segments, "manifest.json"]);
    }

    private sealed class FixedManifestDialog(string path) : IManifestFileDialogService
    {
        public string? OpenManifestFile() => path;
    }

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
    public async Task ImportRevision_ChangingExamDiscardsLateResponse()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new ExamVersionSnapshot("exam-1", new VersionVector(3, 2, 1),
            new ScheduleWindow(now, now.AddHours(1)), ExamLifecycle.Running, now, null,
            null, null, new Dictionary<string, ParticipantVersionStatus>());
        using var handler = new DeferredHandler();
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        var viewModel = new ImportRevisionViewModel(api) { ExamId = "exam-1" };

        var pending = viewModel.LoadVersionAsync();
        Assert.True(viewModel.IsLoading);
        viewModel.ExamId = "exam-2";
        handler.Complete(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ApiEnvelope<ExamVersionSnapshot>(
                "synthetic-server", "synthetic-node", 1, "test", snapshot, null), options: Json)
        });
        await pending;

        Assert.Equal("未查询", viewModel.PaperVersion);
        Assert.False(viewModel.IsLoading);
        Assert.Contains("考试 ID 已变更", viewModel.Status, StringComparison.Ordinal);
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

    private sealed class DeferredHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource<HttpResponseMessage> _response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Complete(HttpResponseMessage response) => _response.SetResult(response);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => _response.Task;
    }
}
