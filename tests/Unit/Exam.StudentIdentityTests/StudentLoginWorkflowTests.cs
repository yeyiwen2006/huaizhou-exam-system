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

    [Theory]
    [InlineData(" EXAM.example ", "https://exam.example/")]
    [InlineData("192.0.2.10", "https://192.0.2.10/")]
    [InlineData("::1", "https://[::1]/")]
    [InlineData("https://EXAM.example/", "https://exam.example/")]
    [InlineData("https://exam.example:443/", "https://exam.example/")]
    [InlineData("https://exam.example:5443/", "https://exam.example:5443/")]
    [InlineData("https://localhost:5443", "https://localhost:5443/")]
    public async Task EachLoginContactsSelectedServer_ReusesProtectedIdentity_AndAcceptsNextDayNumber(
        string address, string expectedAddress)
    {
        var day = new DateOnly(2026, 10, 8);
        var requests = new List<LoginStudentRequest>();
        using var api = new HttpStudentApi(new Handler(async message =>
        {
            Assert.Equal(new Uri(new Uri(expectedAddress), "api/v1/student/login"), message.RequestUri);
            var request = (await message.Content!.ReadFromJsonAsync<LoginStudentRequest>(JsonDefaults.Create()))!;
            requests.Add(request);
            return Reply(request, day);
        }));
        var store = new DeviceStore(_directory);
        var workflow = new StudentLoginWorkflow(store, api);
        var first = await workflow.LoginAsync(address, "合成考生", "01", "0001");
        Assert.Equal(new Uri(expectedAddress).Port, first.Device.Port);
        var second = await new StudentLoginWorkflow(new DeviceStore(_directory), api)
            .LoginAsync(expectedAddress, "合成考生", "01", "0001");
        Assert.Equal(first.Device.ClientNumber, second.Device.ClientNumber);
        day = day.AddDays(1);
        var next = await workflow.LoginAsync(address, "合成考生", "01", "0001");
        Assert.Equal("202610090001", next.Device.ClientNumber);
        Assert.Equal(first.Device.DeviceId, next.Device.DeviceId);
        Assert.Equal(first.Device.Credential, next.Device.Credential);
        Assert.Equal(next.Device, store.Load());
        Assert.Equal(3, requests.Count);
        Assert.Single(requests.Select(request => request.DeviceId).Distinct());
        Assert.Equal(3, requests.Select(request => request.RequestId).Distinct().Count());
    }

    [Fact]
    public async Task LegacyNumberUpgradesOnLoginWithoutReplacingProtectedDeviceIdentity()
    {
        var store = new DeviceStore(_directory);
        var legacy = new DeviceRecord("legacy-device", "synthetic-legacy-credential", "20261008007",
            "exam.example", 5443, new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero),
            "server", ActivationStatus.Active, CredentialVersion: 1, ServerEpoch: 1);
        store.Save(legacy);
        using var api = new HttpStudentApi(new Handler(async message =>
        {
            var request = (await message.Content!.ReadFromJsonAsync<LoginStudentRequest>(JsonDefaults.Create()))!;
            Assert.Equal(legacy.DeviceId, request.DeviceId);
            Assert.Equal(legacy.Credential, request.Credential);
            return Reply(request, new DateOnly(2026, 10, 8), "0008");
        }));

        var result = await new StudentLoginWorkflow(store, api).LoginAsync("https://exam.example:5443", "合成考生", "01", "0001");

        Assert.Equal("202610080008", result.Device.ClientNumber);
        Assert.Equal(legacy.DeviceId, result.Device.DeviceId);
        Assert.Equal(legacy.Credential, result.Device.Credential);
        Assert.Equal(result.Device, new DeviceStore(_directory).Load());
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
        await Assert.ThrowsAsync<HttpRequestException>(() => workflow.LoginAsync("https://127.0.0.1:5443", "合成考生", "01", "0001"));
        var saved = store.Load()!;
        Assert.Equal(originalDevice, saved.DeviceId);
        var first = await workflow.LoginAsync("https://127.0.0.1:5443", "合成考生", "01", "0001");
        Assert.Equal(saved.Credential, first.Device.Credential);
        await Assert.ThrowsAsync<DeviceStoreException>(() => workflow.LoginAsync("https://127.0.0.1:5443", "合成考生", "01", "0001"));
        Assert.Equal(first.Device, store.Load());
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://exam.example")]
    [InlineData("exam.example:5443")]
    [InlineData("exam.example/path")]
    [InlineData("https://exam.example:0")]
    [InlineData("https://exam.example:65536")]
    [InlineData("https://exam.example/path")]
    [InlineData("https://user@exam.example")]
    [InlineData("https://exam.example/?query=value")]
    [InlineData("https://exam.example/#fragment")]
    public async Task InvalidAddressDoesNotConnectOrCreateIdentity(string address)
    {
        var requests = 0;
        using var api = new HttpStudentApi(new Handler(_ =>
        {
            requests++;
            throw new InvalidOperationException("Invalid addresses must not send a request.");
        }));
        var workflow = new StudentLoginWorkflow(new DeviceStore(_directory), api);

        await Assert.ThrowsAsync<ArgumentException>(() => workflow.LoginAsync(address, "合成考生", "01", "0001"));

        Assert.Equal(0, requests);
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

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
