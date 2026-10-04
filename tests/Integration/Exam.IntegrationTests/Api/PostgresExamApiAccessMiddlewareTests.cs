using System.Text;
using System.Text.Json;
using Exam.Application.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.IntegrationTests.Postgres;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Http;

namespace Exam.IntegrationTests.Api;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresExamApiAccessMiddlewareTests(PostgresDatabaseFixture fixture)
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private readonly ServerNodeEnvelope server = new($"middleware-{Guid.NewGuid():N}", "node-a", 1);

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
            new FixedOperatorAuthenticator(Token, "operator-a"),
            new AcceptingCredentialValidator(),
            CreateGuard(),
            server);

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        await AssertErrorAsync(context, ErrorCodes.Forbidden);
    }

    [Theory]
    [InlineData("paper-materials?version=1")]
    [InlineData("grading-materials")]
    public async Task Paper_materials_download_requires_an_operator_session(string endpoint)
    {
        // 源材料归档含独立答案：取回入口必须走考务登录会话，不能落到公开路由或设备凭据路由上。
        var invoked = false;
        var middleware = CreateMiddleware(() => invoked = true);
        var context = CreateJsonContext("/api/v1/exams/exam/" + endpoint, "application/json", "{}");
        context.Request.Method = HttpMethods.Get;

        await middleware.InvokeAsync(
            context,
            new FixedOperatorAuthenticator(Token, "operator-a"),
            new AcceptingCredentialValidator(),
            CreateGuard(),
            server);

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        await AssertErrorAsync(context, "UNAUTHORIZED");
    }

    [Theory]
    [InlineData("paper-materials?version=1")]
    [InlineData("grading-materials")]
    public async Task Paper_materials_download_is_not_authorized_by_device_credentials(string endpoint)
    {
        // 即使带着有效的设备凭据也不能取回归档：它不是学生/设备路由。
        var invoked = false;
        var middleware = CreateMiddleware(() => invoked = true);
        var context = CreateJsonContext("/api/v1/exams/exam/" + endpoint, "application/json", "{}");
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers["X-Exam-Device-Id"] = "device-a";
        context.Request.Headers["X-Exam-Device-Credential"] = "credential-a";

        await middleware.InvokeAsync(
            context,
            new FixedOperatorAuthenticator(Token, "operator-a"),
            new AcceptingCredentialValidator(),
            CreateGuard(),
            server);

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        await AssertErrorAsync(context, "UNAUTHORIZED");
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
            new FixedOperatorAuthenticator(Token, "operator-a"),
            new AcceptingCredentialValidator(),
            CreateGuard(),
            server);

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        await AssertErrorAsync(context, ErrorCodes.AuthorizationRevoked);
    }

    [Fact]
    public async Task Device_route_rejects_non_object_body_with_authenticated_server_error()
    {
        var invoked = false;
        var middleware = CreateMiddleware(() => invoked = true);
        var context = CreateJsonContext("/api/v1/resources/unlock", "application/json", "[]");
        context.Request.Headers["X-Exam-Device-Id"] = "device-a";
        context.Request.Headers["X-Exam-Device-Credential"] = "credential-a";

        await middleware.InvokeAsync(context, new FixedOperatorAuthenticator(Token, "operator-a"),
            new AcceptingCredentialValidator(), CreateGuard(), server);

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        await AssertErrorAsync(context, ErrorCodes.AuthorizationRevoked);
    }

    [Fact]
    public async Task Inactive_node_returns_retryable_error_with_server_identity()
    {
        var invoked = false;
        var middleware = CreateMiddleware(() => invoked = true);
        var context = CreateJsonContext("/api/v1/resources/unlock", "application/json", "{}");

        await middleware.InvokeAsync(context, new FixedOperatorAuthenticator(Token, "operator-a"),
            new AcceptingCredentialValidator(), CreateGuard(allowUninitializedState: false), server);

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        await AssertErrorAsync(context, ErrorCodes.NotActiveServer, retryable: true);
    }

    private async Task AssertErrorAsync(DefaultHttpContext context, string code, bool retryable = false)
    {
        context.Response.Body.Position = 0;
        var envelope = await JsonSerializer.DeserializeAsync<ApiEnvelope<object>>(
            context.Response.Body, JsonDefaults.Create());
        Assert.NotNull(envelope);
        Assert.Equal(server.ServerId, envelope.ServerId);
        Assert.Equal(server.NodeId, envelope.NodeId);
        Assert.Equal(server.ServerEpoch, envelope.ServerEpoch);
        Assert.Equal(context.TraceIdentifier, envelope.CorrelationId);
        Assert.Null(envelope.Result);
        Assert.NotNull(envelope.Error);
        Assert.Equal(code, envelope.Error.Code);
        Assert.Equal(retryable, envelope.Error.Retryable);
    }

    private ExamApiAccessMiddleware CreateMiddleware(Action onInvoked) =>
        new(async context =>
        {
            onInvoked();
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            await Task.CompletedTask;
        });

    private ActiveNodeGuard CreateGuard(bool allowUninitializedState = true) =>
        new(
            fixture.DataSource,
            new ServerIdentityOptions
            {
                ServerId = server.ServerId,
                NodeId = server.NodeId,
                ServerEpoch = server.ServerEpoch
            },
            allowUninitializedState);

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
