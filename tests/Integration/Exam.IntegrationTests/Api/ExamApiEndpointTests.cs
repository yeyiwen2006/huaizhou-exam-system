using System.Net;
using System.Net.Http.Json;
using Exam.Application.Activation;
using Exam.Application.Commands;
using Exam.Application.Participation;
using Exam.Application.Submissions;
using Exam.Application.Versioning;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Versioning;
using Exam.Domain.Commands;
using Exam.Domain.Submissions;
using Exam.Server.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Exam.IntegrationTests.Api;

public sealed class ExamApiEndpointTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Maps_A02_through_A06_application_entry_points()
    {
        await using var application = await CreateApplicationAsync();
        var routes = application.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("/api/v1/activation/requests", routes);
        Assert.Contains("/api/v1/activation/status", routes);
        Assert.Contains("/api/v1/participation/confirm", routes);
        Assert.Contains("/api/v1/commands/acknowledgements", routes);
        Assert.Contains("/api/v1/submissions/final", routes);
        Assert.DoesNotContain("/api/v1/participation/finalize", routes);
        Assert.Contains("/api/v1/versioning/exams/{examId}/paper-publications", routes);
        Assert.Contains("/api/v1/versioning/exams/{examId}/grading-publications", routes);
    }

    [Fact]
    public async Task Participation_cannot_be_finalized_without_the_final_answer_transaction()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();
        var response = await client.PostAsJsonAsync(
            "/api/v1/participation/finalize",
            new FinalizeParticipationRequest("request-bypass", "participation", "device", 1, 1),
            JsonDefaults.Create());

        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"A standalone participation finalization endpoint must not exist: {response.StatusCode}.");
    }

    [Fact]
    public async Task Activation_status_is_a_read_query_that_observes_approval_after_submit_replay()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();
        var json = JsonDefaults.Create();
        var submit = new SubmitActivationRequest("activation-submit", "device-api", "credential-api");

        var submitted = await (await client.PostAsJsonAsync("/api/v1/activation/requests", submit, json))
            .Content.ReadFromJsonAsync<ApiEnvelope<ActivationRequestResult>>(json);
        Assert.NotNull(submitted?.Result);

        await client.PostAsJsonAsync(
            "/api/v1/activation/approve",
            new ApproveActivationRequest("activation-approve", submitted.Result.ActivationRequestId),
            json);

        var replayedSubmit = await (await client.PostAsJsonAsync("/api/v1/activation/requests", submit, json))
            .Content.ReadFromJsonAsync<ApiEnvelope<ActivationRequestResult>>(json);
        var status = await (await client.PostAsJsonAsync(
                "/api/v1/activation/status",
                new GetActivationStatusRequest(
                    submitted.Result.ActivationRequestId,
                    submit.DeviceId,
                    submit.Credential),
                json))
            .Content.ReadFromJsonAsync<ApiEnvelope<ActivationRequestResult>>(json);

        Assert.Equal(ActivationStatus.Pending, replayedSubmit?.Result?.Status);
        Assert.Equal(ActivationStatus.Active, status?.Result?.Status);
        Assert.NotNull(status?.Result?.ClientNumber);
    }

    [Fact]
    public async Task Versioning_endpoint_uses_request_id_envelope_and_maps_conflict_to_409()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();
        var json = JsonDefaults.Create();

        var createResponse = await client.PostAsJsonAsync(
            "/api/v1/versioning/exams",
            new CreateVersionedExamApiRequest(
                "request-create",
                "exam-api",
                new ScheduleWindow(Now.AddHours(1), Now.AddHours(2))),
            json);
        var created = await createResponse.Content.ReadFromJsonAsync<ApiEnvelope<ExamVersionSnapshot>>(json);

        var conflictResponse = await client.PutAsJsonAsync(
            "/api/v1/versioning/exams/exam-api/schedule",
            new UpdateScheduleApiRequest(
                "request-conflict",
                0,
                new ScheduleWindow(Now.AddHours(1), Now.AddHours(3))),
            json);
        var conflict = await conflictResponse.Content.ReadFromJsonAsync<ApiEnvelope<object>>(json);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.NotNull(created?.Result);
        Assert.Equal("request-create", created.CorrelationId);
        Assert.Equal("server-test", created.ServerId);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        Assert.Equal("request-conflict", conflict?.CorrelationId);
        Assert.Equal("VERSION_CONFLICT", conflict?.Error?.Code);
    }

    [Fact]
    public async Task Versioning_write_without_request_id_is_rejected_before_service_mutation()
    {
        await using var application = await CreateApplicationAsync();
        using var client = application.GetTestClient();
        var json = JsonDefaults.Create();

        var response = await client.PostAsJsonAsync(
            "/api/v1/versioning/exams",
            new CreateVersionedExamApiRequest(
                string.Empty,
                "exam-no-request",
                new ScheduleWindow(Now.AddHours(1), Now.AddHours(2))),
            json);
        var error = await response.Content.ReadFromJsonAsync<ApiEnvelope<object>>(json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ErrorCodes.InvalidInput, error?.Error?.Code);
        Assert.False(application.Services
            .GetRequiredService<IExamVersioningService>()
            .GetExam("exam-no-request")
            .Succeeded);
    }

    private static async Task<WebApplication> CreateApplicationAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ExamServer:ServerId"] = "server-test",
            ["ExamServer:NodeId"] = "node-test",
            ["ExamServer:ServerEpoch"] = "1"
        });
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            var defaults = JsonDefaults.Create();
            options.SerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
            options.SerializerOptions.PropertyNameCaseInsensitive = defaults.PropertyNameCaseInsensitive;
            foreach (var converter in defaults.Converters)
            {
                options.SerializerOptions.Converters.Add(converter);
            }
        });
        builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));

        var activationPersistence = new InMemoryActivationPersistence();
        var credentials = new Pbkdf2DeviceCredentialProtector();
        builder.Services.AddSingleton<IActivationPersistence>(activationPersistence);
        builder.Services.AddSingleton<IDeviceCredentialProtector>(credentials);
        builder.Services.AddSingleton<ActivationService>();
        builder.Services.AddSingleton<IDeviceCredentialValidator>(provider =>
            provider.GetRequiredService<ActivationService>());

        builder.Services.AddSingleton<IParticipationPersistence, InMemoryParticipationPersistence>();
        builder.Services.AddSingleton<ICandidateRosterDirectory, InMemoryCandidateRosterDirectory>();
        builder.Services.AddSingleton<IExamEntryDirectory, InMemoryExamEntryDirectory>();
        builder.Services.AddSingleton<ParticipationService>();

        builder.Services.AddSingleton(new ServerNodeEnvelope("server-test", "node-test", 1));
        builder.Services.AddSingleton<ICommandEventStore, InMemoryCommandEventStore>();
        builder.Services.AddSingleton<IParticipationAccessReader, InMemoryParticipationAccessRegistry>();
        builder.Services.AddSingleton<ReliableCommandService>();

        builder.Services.AddSingleton<ISubmissionStore, InMemorySubmissionStore>();
        builder.Services.AddSingleton<IGradingTaskQueue, InMemoryGradingTaskQueue>();
        builder.Services.AddSingleton<ICollectionCompletionStore, InMemoryCollectionCompletionStore>();
        builder.Services.AddSingleton<SubmissionService>();
        builder.Services.AddSingleton<CollectionCompletionService>();
        builder.Services.AddSingleton<IExamVersioningService, InMemoryExamVersioningService>();

        var application = builder.Build();
        application.MapExamApi();
        await application.StartAsync();
        return application;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
