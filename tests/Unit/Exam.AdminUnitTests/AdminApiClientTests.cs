using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Admin.Services;
using Exam.Contracts.Activation;
using Exam.Contracts.Common;
using Exam.Contracts.Operators;
using Exam.Contracts.Versioning;

namespace Exam.AdminUnitTests;

public sealed class AdminApiClientTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();

    [Fact]
    public void Configure_RejectsPlainHttpIncludingLoopback()
    {
        using var api = new AdminApiClient();

        Assert.Throws<ArgumentException>(() => api.Configure(
            "http://192.0.2.1:5001/", "synthetic-token", "operator-1"));
        Assert.Throws<ArgumentException>(() => api.Configure(
            "http://127.0.0.1:5001/", "synthetic-token", "operator-1"));
        api.Configure("https://exam.example.com/", "synthetic-token", "operator-1");
        Assert.True(api.IsConfigured);
    }

    [Fact]
    public async Task ConnectionCheck_RejectsInvalidOperatorTokenEvenWhenHealthIsReady()
    {
        using var handler = new RouteHandler(request =>
            request.RequestUri!.AbsolutePath == "/health/ready"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "invalid-token", "operator-1");

        var error = await Assert.ThrowsAsync<AdminApiException>(() => api.CheckConnectionAsync());

        Assert.Equal("OPERATOR_AUTH_FAILED", error.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task ConnectionCheck_ReadsCurrentOperatorAccount()
    {
        using var handler = new RouteHandler(request =>
            request.RequestUri!.AbsolutePath == "/health/ready"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : request.RequestUri.AbsolutePath == "/api/v1/operators/me"
                    ? Success(Account("operator-1", OperatorStatus.Active, isAdministrator: false))
                    : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001", "synthetic-token", "operator-1");

        await api.CheckConnectionAsync();

        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("operator-1", api.Account!.Username);
        Assert.False(api.IsAdministrator);
    }

    [Fact]
    public async Task Login_PostsCredentialsWithoutAuthorization_AndUsesReturnedSession()
    {
        var requests = new List<(string Path, string? Authorization, string Body)>();
        using var handler = new RouteHandler(request =>
        {
            requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(),
                request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty));
            return request.RequestUri.AbsolutePath switch
            {
                "/api/v1/operators/login" => Success(new OperatorSession("synthetic-session",
                    Account("Admin.User", OperatorStatus.Active, isAdministrator: true))),
                "/api/v1/operators" => Success(new[] { Account("Admin.User", OperatorStatus.Active, isAdministrator: true) }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        using var api = new AdminApiClient(handler);

        var account = await api.LoginAsync("https://localhost:5001", "admin.user", "synthetic-password");
        var listed = await api.ListOperatorsAsync();

        Assert.Equal("Admin.User", account.Username);
        Assert.True(api.IsConfigured);
        Assert.True(api.IsAdministrator);
        Assert.Equal("Admin.User", api.OperatorId);
        Assert.Single(listed);
        Assert.Null(requests[0].Authorization);
        Assert.Contains("synthetic-password", requests[0].Body);
        Assert.Equal("Bearer synthetic-session", requests[1].Authorization);
    }

    [Fact]
    public async Task Login_RefusedAccountKeepsClientLoggedOut()
    {
        using var handler = new RouteHandler(_ => Error(HttpStatusCode.Forbidden, OperatorErrorCodes.PendingApproval));
        using var api = new AdminApiClient(handler);

        var error = await Assert.ThrowsAsync<AdminApiException>(() =>
            api.LoginAsync("https://localhost:5001", "pending-user", "synthetic-password"));

        Assert.Equal(OperatorErrorCodes.PendingApproval, error.Code);
        Assert.False(api.IsConfigured);
        Assert.Null(api.Account);
    }

    [Fact]
    public async Task Logout_ClearsLocalSessionEvenWhenServerIsUnavailable()
    {
        using var handler = new RouteHandler(request => request.RequestUri!.AbsolutePath == "/api/v1/operators/login"
            ? Success(new OperatorSession("synthetic-session", Account("operator-1", OperatorStatus.Active, isAdministrator: false)))
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var api = new AdminApiClient(handler);
        await api.LoginAsync("https://localhost:5001", "operator-1", "synthetic-password");

        await api.LogoutAsync();

        Assert.False(api.IsConfigured);
        Assert.Null(api.Account);
        Assert.Equal(string.Empty, api.OperatorId);
    }

    private static OperatorAccount Account(string username, OperatorStatus status, bool isAdministrator) =>
        new(username, "合成考务人员", status, isAdministrator, DateTimeOffset.UnixEpoch, null);

    [Fact]
    public async Task ConnectionCheck_RejectsGenericProxyNotFound()
    {
        using var handler = new RouteHandler(request =>
            request.RequestUri!.AbsolutePath == "/health/ready"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");

        var error = await Assert.ThrowsAsync<AdminApiException>(() => api.CheckConnectionAsync());

        Assert.Equal("OPERATOR_AUTH_FAILED", error.Code);
    }

    [Fact]
    public async Task SaveSchedule_PropagatesServerVersionConflict()
    {
        using var handler = new RouteHandler(request => request.Method == HttpMethod.Get
            ? Error(HttpStatusCode.NotFound, "EXAM_NOT_FOUND")
            : Error(HttpStatusCode.Conflict, "VERSION_CONFLICT"));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");

        var error = await Assert.ThrowsAsync<AdminApiException>(() => api.SaveScheduleAsync(
            "synthetic-exam", new ScheduleWindow(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)), expectedVersion: 7, reason: "合成并发测试"));

        Assert.Equal("VERSION_CONFLICT", error.Code);
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SaveSchedule_DoesNotTreatGenericNotFoundAsMissingExam()
    {
        using var handler = new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");

        var error = await Assert.ThrowsAsync<AdminApiException>(() => api.SaveScheduleAsync(
            "synthetic-exam", new ScheduleWindow(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)), expectedVersion: 1, reason: "合成并发测试"));

        Assert.Equal("HTTP_404", error.Code);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task ApproveActivation_UsesOnlyServerGeneratedClientNumber()
    {
        string? requestJson = null;
        using var handler = new RouteHandler(request =>
        {
            requestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var result = new ActivationResult("device-1", "20260923001", new DateOnly(2026, 9, 23),
                ActivationStatus.Active, "room-1", 1, 1);
            return Success(result);
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");

        var approved = await api.ApproveActivationAsync("activation-1", "room-1");

        Assert.Equal("20260923001", approved.ClientNumber);
        using var document = JsonDocument.Parse(requestJson!);
        Assert.False(document.RootElement.TryGetProperty("clientNumber", out _));
        Assert.Equal("activation-1", document.RootElement.GetProperty("activationRequestId").GetString());
    }

    [Fact]
    public async Task ApproveActivation_PropagatesServerFailure()
    {
        using var handler = new RouteHandler(_ => Error(HttpStatusCode.NotFound, "ACTIVATION_REQUEST_NOT_FOUND"));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");

        var error = await Assert.ThrowsAsync<AdminApiException>(() => api.ApproveActivationAsync("missing", null));

        Assert.Equal("ACTIVATION_REQUEST_NOT_FOUND", error.Code);
    }

    [Theory]
    [InlineData("VERSION_CONFLICT")]
    [InlineData("AUTHORIZATION_REVOKED")]
    public async Task DeviceDecision_PropagatesAuthoritativeFailure(string code)
    {
        using var handler = new RouteHandler(_ => Error(HttpStatusCode.Conflict, code));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");

        var error = await Assert.ThrowsAsync<AdminApiException>(() =>
            api.DecideDeviceAsync("participation-1", "device-2", 3, "synthetic reason"));

        Assert.Equal(code, error.Code);
    }

    [Fact]
    public async Task ResendCommand_UsesOfficialEndpointAndPropagatesFailure()
    {
        string? requestedPath = null;
        using var handler = new RouteHandler(request =>
        {
            requestedPath = request.RequestUri!.AbsolutePath;
            return Error(HttpStatusCode.NotFound, "EVENT_NOT_FOUND");
        });
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");

        var error = await Assert.ThrowsAsync<AdminApiException>(() =>
            api.ResendCommandAsync("event-1", "participation-1", 1, "device-1", 1));

        Assert.Equal("/api/v1/commands/event-1/resend", requestedPath);
        Assert.Equal("EVENT_NOT_FOUND", error.Code);
    }

    private static HttpResponseMessage Error(HttpStatusCode status, string code) => new(status)
    {
        Content = JsonContent.Create(new ApiEnvelope<object>(
            "synthetic-server", "synthetic-node", 1, "test", null,
            new ApiError(code, $"Synthetic {code}")), options: Json)
    };

    private static HttpResponseMessage Success<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new ApiEnvelope<T>(
            "synthetic-server", "synthetic-node", 1, "test", value, null), options: Json)
    };

    private sealed class RouteHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(route(request));
        }
    }
}
