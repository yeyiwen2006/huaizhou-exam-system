using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Student;
using Exam.Student.Core;

namespace Exam.StudentIdentityTests;

public sealed class HttpStudentApiTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-22T08:00:00+08:00");

    [Fact]
    public async Task ExamList_UsesHttpsCredentialHeadersAndAuthenticatedSession()
    {
        using var api = new HttpStudentApi(new Handler(request =>
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("/api/v1/student/exams", request.RequestUri.AbsolutePath);
            Assert.Equal("?sessionId=session%2F1", request.RequestUri.Query);
            Assert.Equal("device", request.Headers.GetValues("X-Exam-Device-Id").Single());
            Assert.Equal("credential", request.Headers.GetValues("X-Exam-Device-Credential").Single());
            Assert.DoesNotContain("credential", request.RequestUri.ToString());
            return Response<IReadOnlyList<StudentExamOverview>>([
                new("exam", "合成考试", Start, Start.AddHours(1), ["科目甲"], ExamLifecycle.Scheduled, "类别甲")]);
        }));
        api.ConfigureConnection(Device());
        var exams = await api.GetExamOverviewsAsync("session/1");
        Assert.Equal("合成考试", exams.Single().Name);
        Assert.Null(exams.Single().CategoryName);
    }

    [Theory]
    [InlineData("other-server", 3)]
    [InlineData("server", 1)]
    [InlineData("server", 0)]
    public async Task SuccessWithWrongServerOrOldEpoch_IsRejected(string serverId, long epoch)
    {
        using var api = new HttpStudentApi(new Handler(_ => TimeResponse(serverId, epoch)));
        api.ConfigureConnection(Device() with { ServerEpoch = 2 });
        await Assert.ThrowsAsync<HttpRequestException>(() => api.GetServerTimeAsync());
    }

    [Fact]
    public async Task ReconfiguringPendingDevice_DoesNotEraseLearnedServerIdentity()
    {
        var count = 0;
        using var api = new HttpStudentApi(new Handler(_ => TimeResponse(++count == 1 ? "server" : "other", 2)));
        var pending = Device() with { ServerId = null, ServerEpoch = 0, Status = ActivationStatus.Pending };
        api.ConfigureConnection(pending);
        await api.GetServerTimeAsync();
        api.ConfigureConnection(pending);
        await Assert.ThrowsAsync<HttpRequestException>(() => api.GetServerTimeAsync());
    }

    [Fact]
    public async Task SwitchingConnection_DiscardsAnOldInflightResponse()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var api = new HttpStudentApi(new AsyncHandler(_ => response.Task));
        api.ConfigureConnection(Device());
        var pending = api.GetServerTimeAsync();
        api.ConfigureConnection(Device() with { Host = "127.0.0.2", ServerId = "server-2" });
        response.SetResult(TimeResponse());
        await Assert.ThrowsAsync<HttpRequestException>(() => pending);
    }

    [Fact]
    public async Task RedirectAndMalformedSuccess_NeverBecomeSuccessfulOperations()
    {
        using var redirect = new HttpStudentApi(new Handler(_ => new(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://untrusted.invalid") }
        }));
        redirect.ConfigureConnection(Device());
        await Assert.ThrowsAsync<HttpRequestException>(() => redirect.GetServerTimeAsync());

        using var malformed = new HttpStudentApi(new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent("not json", Encoding.UTF8, "application/json")
        }));
        malformed.ConfigureConnection(Device());
        await Assert.ThrowsAsync<HttpRequestException>(() => malformed.GetServerTimeAsync());
    }

    [Fact]
    public async Task BusinessError_IsReturnedWithoutInventingSuccess()
    {
        using var api = new HttpStudentApi(new Handler(_ => new(HttpStatusCode.Forbidden)
        {
            Content = JsonContent.Create(new ApiEnvelope<ServerTimeSnapshot>("server", "node", 1, "request",
                null, new(ErrorCodes.NotActivated, "设备未激活。")), options: JsonDefaults.Create())
        }));
        api.ConfigureConnection(Device());
        var result = await api.GetServerTimeAsync();
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.NotActivated, result.Error!.Code);
    }

    [Fact]
    public async Task ServerClock_UsesMonotonicElapsedTimeAndHalfRoundTrip()
    {
        var time = new ManualTime();
        var clock = new ServerClock(time);
        Assert.Throws<InvalidOperationException>(() => clock.UtcNow);
        using var api = new HttpStudentApi(new Handler(_ =>
        {
            time.Advance(TimeSpan.FromSeconds(2));
            return TimeResponse();
        }));
        api.ConfigureConnection(Device());
        await clock.SynchronizeAsync(api);
        Assert.Equal(Start.AddSeconds(1), clock.UtcNow);
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(Start.AddSeconds(4), clock.UtcNow);
        clock.Reset();
        Assert.False(clock.IsSynchronized);
    }

    [Fact]
    public async Task Clock_RejectsSlowOrInconsistentSnapshots()
    {
        var time = new ManualTime();
        var clock = new ServerClock(time);
        using var slow = new HttpStudentApi(new Handler(_ =>
        {
            time.Advance(TimeSpan.FromSeconds(11));
            return TimeResponse();
        }));
        slow.ConfigureConnection(Device());
        await Assert.ThrowsAsync<InvalidOperationException>(() => clock.SynchronizeAsync(slow));
        Assert.False(clock.IsSynchronized);
        using var inconsistent = new HttpStudentApi(new Handler(_ =>
            Response(new ServerTimeSnapshot(Start, new("other", "node", 1)))));
        inconsistent.ConfigureConnection(Device());
        await Assert.ThrowsAsync<InvalidOperationException>(() => clock.SynchronizeAsync(inconsistent));
    }

    private static DeviceRecord Device() => new("device", "credential", "20260922001", "127.0.0.1", 5443,
        Start, "server", ActivationStatus.Active, ServerEpoch: 1);

    private static HttpResponseMessage TimeResponse(string server = "server", long epoch = 1) =>
        Response(new ServerTimeSnapshot(Start, new(server, "node", epoch)), server, epoch);

    private static HttpResponseMessage Response<T>(T result, string server = "server", long epoch = 1) =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ApiEnvelope<T>(server, "node", epoch, "request", result, null),
                options: JsonDefaults.Create())
        };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.MinValue;
        public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }
}
