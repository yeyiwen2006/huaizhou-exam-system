using System.Text;
using Exam.Application.Activation;
using Exam.IntegrationTests.Postgres;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Http;

namespace Exam.IntegrationTests.Api;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresExamApiAccessMiddlewareTests(PostgresDatabaseFixture fixture)
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task Operator_route_rejects_spoofed_audit_identity()
    {
        var invoked = false;
        var middleware = CreateMiddleware(() => invoked = true);
        var context = CreateJsonContext(
            "/api/v1/versioning/exams/exam/paper-publications",
            "application/merge-patch+json",
            "{\"operator\":\"spoofed\"}");
        context.Request.Headers.Authorization = $"Bearer {Token}";

        await middleware.InvokeAsync(
            context,
            new ExamApiSecurityOptions(Token, "operator-a"),
            new AcceptingCredentialValidator(),
            CreateGuard());

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
    }

    [Fact]
    public async Task Device_route_checks_plus_json_body_without_content_length()
    {
        var invoked = false;
        var middleware = CreateMiddleware(() => invoked = true);
        var context = CreateJsonContext(
            "/api/v1/versioning/exams/exam/participants/participant/paper-transfer",
            "application/problem+json",
            "{\"deviceId\":\"other-device\"}");
        context.Request.Headers["X-Exam-Device-Id"] = "device-a";
        context.Request.Headers["X-Exam-Device-Credential"] = "credential-a";
        Assert.Null(context.Request.ContentLength);

        await middleware.InvokeAsync(
            context,
            new ExamApiSecurityOptions(Token, "operator-a"),
            new AcceptingCredentialValidator(),
            CreateGuard());

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    private ExamApiAccessMiddleware CreateMiddleware(Action onInvoked) =>
        new(async context =>
        {
            onInvoked();
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            await Task.CompletedTask;
        });

    private ActiveNodeGuard CreateGuard() =>
        new(
            fixture.DataSource,
            new ServerIdentityOptions
            {
                ServerId = $"middleware-{Guid.NewGuid():N}",
                NodeId = "node-a",
                ServerEpoch = 1
            },
            allowUninitializedState: true);

    private static DefaultHttpContext CreateJsonContext(string path, string contentType, string json)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class AcceptingCredentialValidator : IDeviceCredentialValidator
    {
        public ValueTask<bool> ValidateActiveDeviceAsync(
            string deviceId,
            string credential,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public ValueTask<bool> IsActiveDeviceAsync(
            string deviceId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }
}
