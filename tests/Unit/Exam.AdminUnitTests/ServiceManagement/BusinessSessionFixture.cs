using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Exam.Admin.Services;
using Exam.Contracts.Common;
using Exam.Contracts.Operators;

namespace Exam.AdminUnitTests.ServiceManagement;

internal sealed class BusinessSessionFixture : HttpMessageHandler
{
    public const string Credential = "synthetic-business-token";
    public static async Task<AdminSessionSnapshot> CaptureAsync()
    {
        using var api = new AdminApiClient(new BusinessSessionFixture());
        api.Configure("https://exam.example.test/", Credential, "synthetic-operator");
        await api.CheckConnectionAsync();
        return Assert.IsType<AdminSessionSnapshot>(api.CaptureSession());
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var account = new OperatorAccount("synthetic-operator", "合成考务账号", OperatorStatus.Active, true, DateTimeOffset.UtcNow, null);
        return Task.FromResult(request.RequestUri!.AbsolutePath switch
        {
            "/health/ready" => new HttpResponseMessage(HttpStatusCode.OK),
            "/api/v1/operators/login" => Reply(new OperatorSession(Credential, account)),
            "/api/v1/operators/me" => Reply(account),
            "/api/v1/operators/logout" => Reply(new { loggedOut = true }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
    }

    private static HttpResponseMessage Reply<T>(T result) => new(HttpStatusCode.OK)
    { Content = JsonContent.Create(new ApiEnvelope<T>("synthetic-server", "synthetic-node", 1, "synthetic-session", result, null), options: JsonDefaults.Create()) };
}
