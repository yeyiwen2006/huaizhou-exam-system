using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Exam.Contracts.Activation;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Student.Core;

namespace Exam.StudentIdentityTests;

public sealed class StudentLoginWorkflowTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("exam-direct-login-").FullName;

    [Fact]
    public async Task EachLoginContactsSelectedServer_ReusesProtectedIdentity_AndAcceptsNextDayNumber()
    {
        var day = new DateOnly(2026, 10, 8);
        var requests = new List<LoginStudentRequest>();
        using var api = new HttpStudentApi(new Handler(async message =>
        {
            Assert.Equal("https://exam.example:5443/api/v1/student/login", message.RequestUri!.AbsoluteUri);
            var request = (await message.Content!.ReadFromJsonAsync<LoginStudentRequest>(JsonDefaults.Create()))!;
            requests.Add(request);
            return Reply(request, day);
        }));
        var store = new DeviceStore(_directory);
        var workflow = new StudentLoginWorkflow(store, api);
        var first = await workflow.LoginAsync("EXAM.example", 5443, "合成考生", "01", "0001");
        var second = await new StudentLoginWorkflow(new DeviceStore(_directory), api)
            .LoginAsync("exam.example", 5443, "合成考生", "01", "0001");
        Assert.Equal(first.Device.ClientNumber, second.Device.ClientNumber);
        day = day.AddDays(1);
        var next = await workflow.LoginAsync("exam.example", 5443, "合成考生", "01", "0001");
        Assert.Equal("202610090001", next.Device.ClientNumber);
        Assert.Equal(first.Device.DeviceId, next.Device.DeviceId);
        Assert.Equal(first.Device.Credential, next.Device.Credential);
        Assert.Equal(next.Device, store.Load());
        Assert.Equal(3, requests.Count);
        Assert.Single(requests.Select(request => request.DeviceId).Distinct());
        Assert.Equal(3, requests.Select(request => request.RequestId).Distinct().Count());
    }

    [Fact]
    public async Task LostLoginResponseKeepsIdentity_AndWrongSameDayNumberCannotOverwriteIt()
    {
        var calls = 0;
        string? originalDevice = null;
        using var api = new HttpStudentApi(new Handler(async message =>
        {
            var request = (await message.Content!.ReadFromJsonAsync<LoginStudentRequest>(JsonDefaults.Create()))!;
            originalDevice ??= request.DeviceId;
            Assert.Equal(originalDevice, request.DeviceId);
            if (++calls == 1) throw new HttpRequestException("Synthetic lost response");
            return Reply(request, new DateOnly(2026, 10, 8), calls > 2 ? "0002" : "0001");
        }));
        var store = new DeviceStore(_directory);
        var workflow = new StudentLoginWorkflow(store, api);
        await Assert.ThrowsAsync<HttpRequestException>(() => workflow.LoginAsync("127.0.0.1", 5443, "合成考生", "01", "0001"));
        var saved = store.Load()!;
        Assert.Equal(originalDevice, saved.DeviceId);
        var first = await workflow.LoginAsync("127.0.0.1", 5443, "合成考生", "01", "0001");
        Assert.Equal(saved.Credential, first.Device.Credential);
        await Assert.ThrowsAsync<DeviceStoreException>(() => workflow.LoginAsync("127.0.0.1", 5443, "合成考生", "01", "0001"));
        Assert.Equal(first.Device, store.Load());
    }

    [Theory]
    [InlineData("https://exam.example")]
    [InlineData("exam.example:5443")]
    [InlineData("exam.example/path")]
    public void HostInputRejectsProtocolPortAndPath(string host) =>
        Assert.Throws<ArgumentException>(() => DeviceStore.NormalizeHost(host));

    private static HttpResponseMessage Reply(LoginStudentRequest request, DateOnly day, string sequence = "0001") =>
        new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ApiEnvelope<ConnectedStudentSessionResult>("server", "node", 1, request.RequestId,
                new(new("session-" + request.RequestId, request.DeviceId,
                    new("candidate", request.Name, request.ClassText, request.AdmissionNo), ["exam"],
                    new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)),
                    new(request.DeviceId, day.ToString("yyyyMMdd") + sequence, day, ActivationStatus.Active, null, 1, 1)), null),
                options: JsonDefaults.Create())
        };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
