using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Exam.Contracts.Activation;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Student.Core;

namespace Exam.StudentIdentityTests;

public sealed class StudentPresenceReporterTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-05T09:00:00+08:00");

    [Fact]
    public async Task ProductionTransport_ReportsPagesAndQuestionWithoutAnswersOrPrivateMarks()
    {
        var requests = new List<UpdateDevicePresenceRequest>();
        var opens = new List<OpenDevicePresenceRequest>();
        using var api = new HttpStudentApi(new Handler(async request =>
        {
            Assert.Equal("device", request.Headers.GetValues("X-Exam-Device-Id").Single());
            Assert.Equal("synthetic-credential", request.Headers.GetValues("X-Exam-Device-Credential").Single());
            var json = await request.Content!.ReadAsStringAsync();
            Assert.DoesNotContain("marks", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("answers", json, StringComparison.OrdinalIgnoreCase);
            if (request.RequestUri!.AbsolutePath.EndsWith("/open"))
            {
                var open = (await request.Content.ReadFromJsonAsync<OpenDevicePresenceRequest>(JsonDefaults.Create()))!;
                opens.Add(open);
                return Response(new DevicePresenceLease(open.ConnectionId, 3, At));
            }
            Assert.Equal("/api/v1/student/presence", request.RequestUri.AbsolutePath);
            var state = (await request.Content.ReadFromJsonAsync<UpdateDevicePresenceRequest>(JsonDefaults.Create()))!;
            requests.Add(state);
            return Response(new DevicePresenceReceipt(state.Generation, state.Sequence, At));
        }));
        api.ConfigureConnection(new("device", "synthetic-credential", "20261005001", "localhost", 5443,
            At, "server", ActivationStatus.Active, ServerEpoch: 1));
        var reporter = new StudentPresenceReporter(api);
        await reporter.ReportAsync(new(null, null, "登录"));
        await reporter.ReportAsync(new("session", "exam", "等待考试", DownloadState: "下载中"));
        await reporter.ReportAsync(new("session", "exam", "正在答题", "opaque-id", 2, "已下载"));
        await reporter.ReportAsync(new(null, null, "已退出"), disconnect: true);
        Assert.Single(opens);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, requests.Select(r => r.Sequence));
        Assert.All(requests, r => Assert.Equal(3, r.Generation));
        Assert.Equal("opaque-id", requests[2].CurrentQuestionId);
        Assert.True(requests[^1].Disconnect);
        Assert.Null(requests[^1].SessionId);
        Assert.Null(reporter.Error);
    }

    [Fact]
    public async Task HeartbeatAndLostOpenReply_ReuseLeaseRequestAndNeverConfirmWrongReceipt()
    {
        var clock = new Clock();
        var api = new FakeApi { FailFirstOpen = true };
        var reporter = new StudentPresenceReporter(api, clock);
        var state = new StudentPresenceState("session", "exam", "等待考试", InstalledPaperVersion: 1, DownloadState: "已下载");
        await reporter.ReportAsync(state);
        Assert.NotNull(reporter.Error);
        await reporter.ReportAsync(state);
        Assert.Equal(api.Opens[0], api.Opens[1]);
        Assert.Single(api.Updates);
        await reporter.ReportAsync(state);
        clock.Advance(59);
        await reporter.ReportAsync(state);
        Assert.Single(api.Updates);
        clock.Advance(1);
        api.BadReceipt = true;
        await reporter.ReportAsync(state);
        Assert.NotNull(reporter.Error);
        api.BadReceipt = false;
        await reporter.ReportAsync(state);
        Assert.Null(reporter.Error);
        Assert.Equal(new long[] { 1, 2, 3 }, api.Updates.Select(r => r.Sequence));
    }

    [Fact]
    public async Task ReplacedProcess_CannotOpenANewLeaseAndTakeOverAgain()
    {
        var api = new FakeApi { Replaced = true };
        var reporter = new StudentPresenceReporter(api);
        await reporter.ReportAsync(new(null, null, "登录"));
        await reporter.ReportAsync(new("session", "exam", "正在答题"));
        Assert.Single(api.Opens);
        Assert.Single(api.Updates);
        Assert.Contains("被替换", reporter.Error);
    }

    private static ApiEnvelope<T> Envelope<T>(T result) => new("server", "node", 1, "test", result, null);
    private static HttpResponseMessage Response<T>(T result) => new(HttpStatusCode.OK)
        { Content = JsonContent.Create(Envelope(result), options: JsonDefaults.Create()) };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
    }
    private sealed class FakeApi : IStudentPresenceApi
    {
        public bool FailFirstOpen, BadReceipt, Replaced;
        public List<OpenDevicePresenceRequest> Opens { get; } = [];
        public List<UpdateDevicePresenceRequest> Updates { get; } = [];
        public Task<ApiEnvelope<DevicePresenceLease>> OpenPresenceAsync(OpenDevicePresenceRequest request, CancellationToken ct)
        {
            Opens.Add(request);
            if (FailFirstOpen && Opens.Count == 1) throw new HttpRequestException("synthetic lost reply");
            return Task.FromResult(Envelope(new DevicePresenceLease(request.ConnectionId, 3, At)));
        }
        public Task<ApiEnvelope<DevicePresenceReceipt>> ReportPresenceAsync(UpdateDevicePresenceRequest request, CancellationToken ct)
        {
            Updates.Add(request);
            return Task.FromResult(Replaced ? new ApiEnvelope<DevicePresenceReceipt>("server", "node", 1, "test", null,
                new(ErrorCodes.VersionConflict, "synthetic replaced")) :
                Envelope(new DevicePresenceReceipt(request.Generation, BadReceipt ? request.Sequence + 1 : request.Sequence, At)));
        }
    }
}
