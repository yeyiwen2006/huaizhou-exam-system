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

namespace Exam.AdminUnitTests;

public sealed class MonitoringWorkflowTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();
    private static MonitoringSnapshot Snapshot() => MonitoringFixture.Snapshot(new[]
    {
        new ParticipationResult("part-1", "exam-1", "candidate-1", ParticipationBusinessState.Answering,
            "device-1", 1, 2, 3, null)
    });

    [Fact]
    public async Task QuestionDetailsUsePublishedNumbersAndDoNotExposeInternalIdentifiers()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with
        {
            Questions = [new("private-question-key", 12, "subject", "group", "single", ["private-option-key", "other"])],
            Participants = [snapshot.Participants[0] with
            { CurrentQuestionId = "private-question-key", Answers = new Dictionary<string, Exam.Contracts.Submissions.AnswerValue>
                { ["private-question-key"] = Exam.Contracts.Submissions.AnswerValue.Selected("private-option-key") } }]
        };
        using var handler = new Handler(_ => Task.FromResult(Success(snapshot)));
        using var api = Api(handler);
        using var model = new MonitoringViewModel(api) { ExamId = "exam-1" };
        await model.LoadAsync(); model.SelectedRow = Assert.Single(model.Rows);
        Assert.Equal("第 12 题", model.SelectedRow.CurrentQuestion);
        var answer = Assert.Single(model.AnswerDetails);
        Assert.Equal("第 12 题", answer.Question);
        Assert.Equal("第 1 个选项", answer.Answer);
    }

    [Fact]
    public async Task BusinessResend_UsesSelectedAuthorizationAndRealDeliveryResponse()
    {
        var snapshot = Snapshot();
        snapshot = snapshot with { Participants = new[] { snapshot.Participants[0] with
        { Commands = new[] { new MonitoringCommand("start-1", CommandEventType.Start, 4, "待确认", DateTimeOffset.UtcNow, null, true) } } } };
        using var handler = new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get) return Success(snapshot);
            Assert.EndsWith("/start-1/resend", request.RequestUri!.AbsolutePath);
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("part-1", body.RootElement.GetProperty("participationId").GetString());
            Assert.Equal(2, body.RootElement.GetProperty("authorizationVersion").GetInt64());
            return Success(new CommandDelivery(new("synthetic-server", "node-1", 1), "start-1", "exam-1", "part-1", 1,
                CommandEventType.Start, 4, "device-1", 2, null, "protected-reference", new Dictionary<string, string>(), DateTimeOffset.UtcNow, 7));
        });
        using var api = Api(handler);
        using var model = new MonitoringViewModel(api) { ExamId = "exam-1" };
        await model.LoadAsync(); model.SelectedRow = model.Rows[0];
        await model.ResendSelectedAsync(CommandEventType.Start);
        Assert.Contains("累计发送 7 次", model.Status);
    }

    [Fact]
    public async Task Supplement_LostResponseRetainsTheSameEventAndTarget()
    {
        var snapshot = Snapshot() with { Lifecycle = ExamLifecycle.Ended };
        var requests = new List<CreateCommandEventRequest>();
        using var handler = new Handler(async request =>
        {
            if (request.Method == HttpMethod.Get) return Success(snapshot);
            var body = JsonSerializer.Deserialize<CreateCommandEventRequest>(await request.Content!.ReadAsStringAsync(), Json)!;
            requests.Add(body);
            if (requests.Count == 1) throw new HttpRequestException("lost response");
            return Success(new CommandEventView(body.EventId, body.ExamId, body.ParticipationId, body.EntryVersion,
                body.EventType, body.StateVersion, body.TargetDeviceId, body.TargetAuthorizationVersion, null, DateTimeOffset.UtcNow, false, 0));
        });
        using var api = Api(handler);
        using var model = new MonitoringViewModel(api) { ExamId = "exam-1" };
        await model.LoadAsync(); model.SelectedRow = model.Rows[0];
        await Assert.ThrowsAsync<HttpRequestException>(model.RequestUploadAsync);
        await model.RequestUploadAsync();
        Assert.Equal(requests[0], requests[1]);
        Assert.Contains("原固定答卷", model.Status);
        Assert.DoesNotContain("回收完成。", model.Status);
    }

    [Fact]
    public async Task Supplement_CannotInterruptAnsweringOrRepeatCollectedWork()
    {
        var writes = 0;
        using var handler = new Handler(request =>
        {
            if (request.Method != HttpMethod.Get) writes++;
            return Task.FromResult(Success(Snapshot()));
        });
        using var api = Api(handler);
        using var model = new MonitoringViewModel(api) { ExamId = "exam-1" };
        await model.LoadAsync(); model.SelectedRow = model.Rows[0];
        await model.RequestUploadAsync();
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task ActiveMonitoring_RefreshesAndStopsWhenThePageIsLeft()
    {
        var reads = 0;
        using var handler = new Handler(request =>
        {
            var monitoring = request.RequestUri!.AbsolutePath.EndsWith("/monitoring", StringComparison.Ordinal);
            if (monitoring) Interlocked.Increment(ref reads);
            return Task.FromResult(monitoring ? Success(Snapshot()) : Success(Array.Empty<Exam.Contracts.Submissions.CollectionCompletedNotification>()));
        });
        using var api = Api(handler);
        using var model = new MonitoringViewModel(api) { ExamId = "exam-1", ConnectionReady = true, IsPageActive = true };
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref reads) < 2 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.True(reads >= 2);
        model.IsPageActive = false;
        var stoppedAt = reads;
        await Task.Delay(1200);
        Assert.Equal(stoppedAt, reads);
    }

    [Fact]
    public async Task DeviceComparison_RetainsChosenDeviceAndShowsSavedAnswersAcrossRefresh()
    {
        var snapshot = Snapshot();
        var first = new ConflictDevice("device-1", "001", "第一试场", DeviceConnectionState.Online,
            DateTimeOffset.UtcNow, true, 5, "答题页") { LastSyncedAt = DateTimeOffset.UtcNow.AddMinutes(-1), SavedAnswerCount = 3 };
        var second = first with { DeviceId = "device-2", ClientNumber = "002", IsAuthorized = false, SavedAnswerCount = 0, LastSyncedAt = null };
        snapshot = snapshot with { Participants = new[] { snapshot.Participants[0] with { ConflictDevices = new[] { first, second } } } };
        using var handler = new Handler(_ => Task.FromResult(Success(snapshot)));
        using var api = Api(handler);
        using var model = new MonitoringViewModel(api) { ExamId = "exam-1" };
        await model.LoadAsync(); model.SelectedRow = model.Rows[0]; model.SelectedConflictDevice = second;
        model.DecisionReason = "换机";
        await model.LoadAsync();
        Assert.Equal("device-2", model.SelectedDeviceId);
        Assert.Equal("002", model.SelectedConflictDevice!.ClientNumber);
        Assert.Contains("已保存 3 题", model.ConflictDevices.First().Progress);
        Assert.Equal("尚无已保存进度", model.ConflictDevices.Last().LastSync);
    }

    private static AdminApiClient Api(HttpMessageHandler handler)
    {
        var api = new AdminApiClient(handler); api.Configure("https://localhost:5001/", "synthetic-token", "operator-1"); return api;
    }
    private static HttpResponseMessage Success<T>(T value) => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(new ApiEnvelope<T>("synthetic-server", "node-1", 1, "test", value, null), options: Json) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => route(request);
    }
}
