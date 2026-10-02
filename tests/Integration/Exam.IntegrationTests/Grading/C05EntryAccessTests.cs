using System.Text;
using System.Text.Json;
using Exam.Application.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.IntegrationTests.Postgres;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Http;

namespace Exam.IntegrationTests.Grading;

/// <summary>
/// C05 两个业务入口（导入发布与只改规则的修订）的**权限接线**核对：
/// 不在公开路由白名单、不是设备路由，因此一律要求考务登录会话；
/// 节点不是活动主节点时整个 /api/v1 也必须先拒绝。请求体里自称的操作者不能顶替已认证身份。
///
/// 只走中间件，不进入业务处理；处理器被调用即视为鉴权失败。
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class C05EntryAccessTests(PostgresDatabaseFixture fixture)
{
    private const string Token = "0123456789abcdef0123456789abcdef";
    private readonly ServerNodeEnvelope _server = new($"c05-access-{Guid.NewGuid():N}", "node-a", 1);

    [Theory]
    [InlineData("/api/v1/imports/publish")]
    [InlineData("/api/v1/imports/synthetic-exam/rule-revisions")]
    public async Task Business_entries_without_an_operator_session_are_rejected(string route)
    {
        var invoked = false;
        var context = CreateJsonContext(route, "application/json", "{\"reason\":\"合成用例\"}");

        await Middleware(() => invoked = true).InvokeAsync(context, new FixedOperatorAuthenticator(Token, "operator-a"),
            new AcceptingCredentialValidator(), Guard(), _server);

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        await AssertErrorAsync(context, "UNAUTHORIZED");
    }

    [Theory]
    [InlineData("/api/v1/imports/publish")]
    [InlineData("/api/v1/imports/synthetic-exam/rule-revisions")]
    public async Task Business_entries_reject_a_spoofed_operator_in_the_body(string route)
    {
        // 操作者只从已认证会话取；请求体自称的身份即使带着有效令牌也不接受。
        var invoked = false;
        var context = CreateJsonContext(route, "application/json", "{\"operator\":\"spoofed\"}");
        context.Request.Headers.Authorization = $"Bearer {Token}";

        await Middleware(() => invoked = true).InvokeAsync(context, new FixedOperatorAuthenticator(Token, "operator-a"),
            new AcceptingCredentialValidator(), Guard(), _server);

        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        await AssertErrorAsync(context, ErrorCodes.Forbidden);
    }

    [Theory]
    [InlineData("/api/v1/imports/publish")]
    [InlineData("/api/v1/imports/synthetic-exam/rule-revisions")]
    public async Task Business_entries_are_refused_while_the_node_is_not_the_active_main_node(string route)
    {
        var invoked = false;
        var context = CreateJsonContext(route, "application/json", "{}");
        context.Request.Headers.Authorization = $"Bearer {Token}";

        await Middleware(() => invoked = true).InvokeAsync(context, new FixedOperatorAuthenticator(Token, "operator-a"),
            new AcceptingCredentialValidator(), Guard(allowUninitializedState: false), _server);

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
        Assert.Equal(_server.ServerId, envelope.ServerId);
        Assert.Equal(_server.NodeId, envelope.NodeId);
        Assert.Equal(code, envelope.Error?.Code);
        Assert.Equal(retryable, envelope.Error?.Retryable);
    }

    private static ExamApiAccessMiddleware Middleware(Action onInvoked) =>
        new(async context =>
        {
            onInvoked();
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            await Task.CompletedTask;
        });

    private ActiveNodeGuard Guard(bool allowUninitializedState = true) => new(
        fixture.DataSource,
        new ServerIdentityOptions
        {
            ServerId = _server.ServerId,
            NodeId = _server.NodeId,
            ServerEpoch = _server.ServerEpoch
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
        public ValueTask<bool> ValidateActiveDeviceAsync(string deviceId, string credential,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

        public ValueTask<bool> IsActiveDeviceAsync(string deviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }
}
