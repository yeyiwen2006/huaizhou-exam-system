using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Exam.Contracts.Activation;
using Exam.Contracts.Participation;
using Exam.Student.Core;

namespace Exam.StudentIdentityTests;

public sealed class PackageTransportTests
{
    [Fact]
    public async Task DownloadUsesPreparationAndDeviceAuthorizationWithoutLeakingCredential()
    {
        using var api = Create(request =>
        {
            Assert.Equal("?preparationId=prep%2F1&sessionId=session%2F1", request.RequestUri!.Query);
            Assert.Equal("credential", request.Headers.GetValues("X-Exam-Device-Credential").Single());
            Assert.DoesNotContain("credential", request.RequestUri.ToString());
            return PackageResponse();
        });
        var bytes = new List<byte>();
        await foreach (var chunk in api.DownloadPackageChunksAsync(Descriptor(), "prep/1", "session/1"))
        {
            bytes.AddRange(chunk);
        }
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }

    [Theory]
    [InlineData("https://127.0.0.2:5443/api/v1/resources/paper")]
    [InlineData("http://127.0.0.1:5443/api/v1/resources/paper")]
    [InlineData("https://127.0.0.1:5443/api/v1/resources/paper?deviceId=other")]
    [InlineData("https://127.0.0.1:5443/api/v1/activation/requests")]
    [InlineData("//outside.invalid/api/v1/resources/paper")]
    public async Task DownloadRejectsUntrustedOrAmbiguousResourceAddressBeforeSending(string address)
    {
        using var api = Create(_ => throw new Xunit.Sdk.XunitException("不应发送请求"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in api.DownloadPackageChunksAsync(Descriptor() with { EncryptedPackageUri = address }, "prep", "session")) { }
        });
    }

    [Theory]
    [InlineData("X-Exam-Server-Id", "other")]
    [InlineData("X-Exam-Server-Epoch", "0")]
    [InlineData("X-Exam-Paper-Version", "2")]
    public async Task DownloadRejectsWrongEnvelopeOrVersion(string header, string value)
    {
        using var api = Create(_ =>
        {
            var response = PackageResponse();
            response.Headers.Remove(header);
            response.Headers.Add(header, value);
            return response;
        });
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in api.DownloadPackageChunksAsync(Descriptor(), "prep", "session")) { }
        });
    }

    private static ExamResourceDescriptor Descriptor() => new("/api/v1/resources/paper", 3, 1, 1);

    private static HttpStudentApi Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var api = new HttpStudentApi(new Handler(respond));
        api.ConfigureConnection(new("device", "credential", "20260922001", "127.0.0.1", 5443,
            default, "server", ActivationStatus.Active, ServerEpoch: 1));
        return api;
    }

    private static HttpResponseMessage PackageResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.huaizhou.exam-package");
        response.Headers.Add("X-Exam-Server-Id", "server");
        response.Headers.Add("X-Exam-Node-Id", "node");
        response.Headers.Add("X-Exam-Server-Epoch", "1");
        response.Headers.Add("X-Exam-Paper-Version", "1");
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
