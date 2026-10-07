using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Admin.Services;
using Exam.Admin.ViewModels;
using Exam.Contracts.Common;
using Exam.Contracts.Versioning;

namespace Exam.AdminUnitTests;

public sealed class ScheduleReplyBoundaryTests
{
    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(4L)]
    [InlineData(5L)]
    [InlineData(7L)]
    [InlineData(long.MaxValue)]
    public async Task UpdateReply_MustBeExactlyTheRequestedVersionPlusOne(long replyVersion)
    {
        using var handler = new ScheduleHandler { SaveReply = Snapshot(replyVersion) };
        using var api = CreateApi(handler);
        var viewModel = Draft(api);
        await viewModel.LoadScheduleAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.SaveScheduleAsync());

        Assert.Contains("无法确认保存结果", error.Message, StringComparison.Ordinal);
        Assert.Equal(5, handler.ExpectedVersions.Single());
        Assert.Equal(5, viewModel.LoadedScheduleVersion);
        Assert.DoesNotContain("考试时间已保存", viewModel.Status, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(2L)]
    [InlineData(long.MaxValue)]
    public async Task ScheduleWithoutLoadedVersionNeverSendsAWrite(long replyVersion)
    {
        using var handler = new ScheduleHandler { SaveReply = Snapshot(replyVersion) };
        using var api = CreateApi(handler);
        var viewModel = Draft(api);

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.SaveScheduleAsync());

        Assert.Empty(handler.ExpectedVersions);
        Assert.Contains("尚未读取", viewModel.ScheduleVersionText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingNestedReplyFields_AreRejectedAsInvalidReceipts(bool missingVersions)
    {
        using var handler = new ScheduleHandler
        {
            SaveReply = missingVersions
                ? Snapshot(6) with { Versions = null! }
                : Snapshot(6) with { Schedule = null! }
        };
        using var api = CreateApi(handler);
        var viewModel = Draft(api);
        await viewModel.LoadScheduleAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.SaveScheduleAsync());

        Assert.Contains("无法确认保存结果", error.Message, StringComparison.Ordinal);
        Assert.Equal(5, viewModel.LoadedScheduleVersion);
    }

    [Fact]
    public async Task EditedDraft_IsPreserved_AndNextSaveUsesTheAcknowledgedVersion()
    {
        using var handler = new ScheduleHandler { DelaySave = true };
        using var api = CreateApi(handler);
        var viewModel = Draft(api);
        await viewModel.LoadScheduleAsync();
        var saving = viewModel.SaveScheduleAsync();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.ScheduledEndText = "2026-10-01 13:00 +08:00";
        handler.Reply.TrySetResult(Snapshot(6));
        await saving.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("2026-10-01 13:00 +08:00", viewModel.ScheduledEndText);
        Assert.Contains("尚未保存", viewModel.Status, StringComparison.Ordinal);
        handler.DelaySave = false;
        handler.SaveReply = Snapshot(7) with { Schedule = Window() with { EndsAt = Window().EndsAt.AddHours(1) } };
        await viewModel.SaveScheduleAsync();

        Assert.Equal(new long?[] { 5, 6 }, handler.ExpectedVersions);
        Assert.Equal(7, viewModel.LoadedScheduleVersion);
        Assert.StartsWith("考试时间已保存", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EquivalentUtcInstants_AreAcceptedWithoutRequiringTheSameOffset()
    {
        using var handler = new ScheduleHandler
        {
            SaveReply = Snapshot(6) with
            {
                Schedule = new ScheduleWindow(Window().StartsAt.ToUniversalTime(), Window().EndsAt.ToUniversalTime())
            }
        };
        using var api = CreateApi(handler);
        var viewModel = Draft(api);
        await viewModel.LoadScheduleAsync();

        await viewModel.SaveScheduleAsync();

        Assert.StartsWith("考试时间已保存", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplyForPreviousExam_DoesNotSetTheNewExamVersion()
    {
        using var handler = new ScheduleHandler { DelaySave = true };
        using var api = CreateApi(handler);
        var viewModel = Draft(api);
        await viewModel.LoadScheduleAsync();
        var saving = viewModel.SaveScheduleAsync();
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.ExamId = "other-synthetic-exam";
        handler.Reply.TrySetResult(Snapshot(1));
        await saving.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("尚未读取", viewModel.ScheduleVersionText, StringComparison.Ordinal);
        Assert.DoesNotContain("考试时间已保存", viewModel.Status, StringComparison.Ordinal);
    }

    private static AdminApiClient CreateApi(HttpMessageHandler handler)
    {
        var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");
        return api;
    }

    private static ExamConfigurationViewModel Draft(AdminApiClient api)
    {
        var draft = new ExamConfigurationViewModel(api)
        {
            ExamId = "synthetic-exam",
            ScheduledStartText = "2026-10-01 10:00 +08:00",
            ScheduledEndText = "2026-10-01 12:00 +08:00"
        };
        draft.Subjects[0].Groups[0].QuestionCount = 10;
        return draft;
    }

    private static ScheduleWindow Window() => new(
        new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.FromHours(8)),
        new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.FromHours(8)));

    private static ExamVersionSnapshot Snapshot(long version) => new(
        "synthetic-exam", new VersionVector(version, 0, 0), Window(), ExamLifecycle.Scheduled,
        null, null, null, null, new Dictionary<string, ParticipantVersionStatus>());

    private sealed class ScheduleHandler : HttpMessageHandler
    {
        public ExamVersionSnapshot SaveReply { get; set; } = Snapshot(6);
        public bool DelaySave { get; set; }
        public List<long?> ExpectedVersions { get; } = [];
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ExamVersionSnapshot> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var snapshot = Snapshot(5);
            if (request.Method != HttpMethod.Get)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                ExpectedVersions.Add(body.RootElement.TryGetProperty("expectedVersion", out var version) ? version.GetInt64() : null);
                Started.TrySetResult();
                snapshot = DelaySave ? await Reply.Task.WaitAsync(cancellationToken) : SaveReply;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ApiEnvelope<ExamVersionSnapshot>(
                    "synthetic-server", "synthetic-node", 1, "test", snapshot, null), options: JsonDefaults.Create())
            };
        }
    }
}
