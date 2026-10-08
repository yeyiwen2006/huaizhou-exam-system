using Exam.IntegrationTests.Postgres;
using Exam.Contracts.Common;
using Microsoft.AspNetCore.Http;

namespace Exam.IntegrationTests.Api;

public sealed partial class PostgresExamApiAccessMiddlewareTests
{
    [Fact]
    public async Task StudentLoginDoesNotRequireAnApprovedDeviceOrOperatorSession()
    {
        var invoked = false;
        var context = CreateJsonContext("/api/v1/student/login", "application/json", "{}");
        await CreateMiddleware(() => invoked = true).InvokeAsync(context,
            new FixedOperatorAuthenticator(Token, "operator-a"),
            new AcceptingCredentialValidator(), CreateGuard(), server);
        Assert.True(invoked);
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/submissions/final")]
    [InlineData("/api/v1/SUBMISSIONS/FINAL")]
    [InlineData("/api/v1/submissions/final/")]
    [InlineData("/api/v1/participation/confirm/")]
    [InlineData("/api/v1/VERSIONING/exams/exam/participants/paper/PAPER-TRANSFER")]
    public async Task DeviceRoutesNeverAcceptOperatorSessionInsteadOfDevice(string path)
    {
        var invoked = false;
        var context = CreateJsonContext(path, "application/json", "{}");
        context.Request.Headers.Authorization = $"Bearer {Token}";
        await CreateMiddleware(() => invoked = true).InvokeAsync(context,
            new FixedOperatorAuthenticator(Token, "operator-a"),
            new AcceptingCredentialValidator(), CreateGuard(), server);
        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        await AssertErrorAsync(context, ErrorCodes.AuthorizationRevoked);
    }

    [Theory]
    [InlineData("/api/v1/submissions/focus-events")]
    [InlineData("/api/v1/submissions/focus-events/")]
    [InlineData("/API/V1/SUBMISSIONS/FOCUS-EVENTS")]
    [InlineData("/api/v1/submissions/FOCUS-EVENTS/")]
    public async Task FocusSummaryNeverAcceptsStudentCredentials(string path)
    {
        var invoked = false;
        var context = CreateJsonContext(path, "application/json", "{}");
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers["X-Exam-Device-Id"] = "device-a";
        context.Request.Headers["X-Exam-Device-Credential"] = "credential-a";
        await CreateMiddleware(() => invoked = true).InvokeAsync(context,
            new FixedOperatorAuthenticator(Token, "operator-a"),
            new AcceptingCredentialValidator(), CreateGuard(), server);
        Assert.False(invoked);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/STUDENT/SERVER-TOPOLOGY")]
    [InlineData("/api/v1/student/server-topology/")]
    public async Task DeviceRouteVariantsKeepWorkingWithTheirOwnCredential(string path)
    {
        var invoked = false;
        var context = CreateJsonContext(path, "application/json", "{}");
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers["X-Exam-Device-Id"] = "device-a";
        context.Request.Headers["X-Exam-Device-Credential"] = "credential-a";
        await CreateMiddleware(() => invoked = true).InvokeAsync(context,
            new FixedOperatorAuthenticator(Token, "operator-a"),
            new AcceptingCredentialValidator(), CreateGuard(), server);
        Assert.True(invoked);
        Assert.Equal(StatusCodes.Status204NoContent, context.Response.StatusCode);
    }
}
