using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Admin.Services;
using Exam.Contracts.Activation;
using Exam.Contracts.Common;
using Exam.Contracts.Versioning;

namespace Exam.AdminUnitTests;

public sealed class AdminApiClientTests
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();

    [Fact]
    public void Configure_RejectsPlainHttpOutsideLoopback()
    {
        using var api = new AdminApiClient();

        Assert.Throws<ArgumentException>(() => api.Configure(
            "http://192.0.2.1:5001/", "synthetic-token", "operator-1"));
        api.Configure("http://127.0.0.1:5001/", "synthetic-token", "operator-1");
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
    public async Task ConnectionCheck_AcceptsAuthenticatedMissingExam()
    {
        using var handler = new RouteHandler(request =>
            request.RequestUri!.AbsolutePath == "/health/ready"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : Error(HttpStatusCode.NotFound, "EXAM_NOT_FOUND"));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001", "synthetic-token", "operator-1");

        await api.CheckConnectionAsync();

        Assert.Equal(2, handler.RequestCount);
    }

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
            "synthetic-exam", new ScheduleWindow(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1))));

        Assert.Equal("VERSION_CONFLICT", error.Code);
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task SaveSchedule_DoesNotTreatGenericNotFoundAsMissingExam()
    {
        using var handler = new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var api = new AdminApiClient(handler);
        api.Configure("https://localhost:5001/", "synthetic-token", "operator-1");

        var error = await Assert.ThrowsAsync<AdminApiException>(() => api.SaveScheduleAsync(
            "synthetic-exam", new ScheduleWindow(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1))));

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
