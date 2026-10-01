using Exam.IntegrationTests.Postgres;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Exam.Application.Activation;
using Exam.Application.Submissions;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Submissions;
using Exam.Domain.Submissions;
using Exam.Infrastructure.Postgres.Submissions;
using Exam.Server.Api;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Exam.IntegrationTests.Submissions;

public sealed partial class PostgresSubmissionStoreTests
{
    [Fact]
    public async Task Collection_HttpApiRequiresOperatorAndReplaysAuthoritativeEvents()
    {
        var operatorToken = await SyntheticOperators.LoginAsync(fixture.DataSource, "operator-test");
        var first = await SeedParticipationAsync();
        var missing = await SeedParticipationAsync(examId: first.ExamId);
        await SaveFinalAsync(first);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddSingleton<ISubmissionStore, PostgresSubmissionStore>();
        builder.Services.AddSingleton<IGradingTaskQueue, PostgresGradingTaskQueue>();
        builder.Services.AddSingleton<PostgresCollectionCompletionStore>();
        builder.Services.AddSingleton<ICollectionCompletionStore>(provider => provider.GetRequiredService<PostgresCollectionCompletionStore>());
        builder.Services.AddSingleton<IAuthoritativeCollectionCompletionStore>(provider => provider.GetRequiredService<PostgresCollectionCompletionStore>());
        builder.Services.AddSingleton<SubmissionService>();
        builder.Services.AddSingleton<CollectionCompletionService>();
        // B06 失焦端点属于同一分组：分组在首个请求时整体构建，服务必须在 DI 中可用。
        builder.Services.AddSingleton<IFocusEventStore, InMemoryFocusEventStore>();
        builder.Services.AddSingleton<FocusEventService>();
        builder.Services.AddPostgresOperatorAccounts();
        builder.Services.AddSingleton<IDeviceCredentialValidator, CollectionTestDeviceValidator>();
        builder.Services.AddSingleton(new ServerNodeEnvelope(Id("api-server"), "node-a", 1));
        builder.Services.AddSingleton(new ActiveNodeGuard(fixture.DataSource, new ServerIdentityOptions
        {
            ServerId = Id("api-server"), NodeId = "node-a", ServerEpoch = 1
        }, true));
        await using var app = builder.Build();
        app.UseMiddleware<ExamApiAccessMiddleware>();
        app.MapGroup("/api/v1").MapSubmissionApi();
        await app.StartAsync();
        using var client = app.GetTestClient();
        var url = $"/api/v1/submissions/collection/events?examId={first.ExamId}&afterRosterVersion=0";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Exam-Device-Id", first.DeviceId);
        client.DefaultRequestHeaders.Add("X-Exam-Device-Credential", "valid-device-only");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(url)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        var forged = await client.PostAsJsonAsync("/api/v1/submissions/collection/evaluate",
            new EvaluateCollectionApiRequest(Id("forged-subset"), first.ExamId, 999, [first.ParticipationId]));
        Assert.Equal(HttpStatusCode.OK, forged.StatusCode);
        var empty = await client.GetFromJsonAsync<ApiEnvelope<CollectionCompletedNotification[]>>(url);
        Assert.Empty(empty!.Result!);
        await SaveFinalAsync(missing);
        await app.Services.GetRequiredService<IAuthoritativeCollectionCompletionStore>().ProcessPendingAsync();
        var response = await client.GetFromJsonAsync<ApiEnvelope<CollectionCompletedNotification[]>>(url);
        var item = Assert.Single(response!.Result!);
        Assert.Equal(2, item.ParticipantCount);
        Assert.Equal(2, item.RosterVersion);
        Assert.Equal(item, Assert.Single((await client.GetFromJsonAsync<ApiEnvelope<CollectionCompletedNotification[]>>(url))!.Result!));
        var next = await client.GetFromJsonAsync<ApiEnvelope<CollectionCompletedNotification[]>>(
            $"/api/v1/submissions/collection/events?examId={first.ExamId}&afterRosterVersion={item.RosterVersion}");
        Assert.Empty(next!.Result!);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(
            $"/api/v1/submissions/collection/events?examId={first.ExamId}&afterRosterVersion=-1")).StatusCode);
        await using var fence = fixture.DataSource.CreateCommand(
            "SELECT delivery_fence_at IS NOT NULL FROM exam.collection_completion_events WHERE event_id = @id;");
        fence.Parameters.AddWithValue("id", item.EventId);
        Assert.Equal(true, await fence.ExecuteScalarAsync());
    }

    private sealed class CollectionTestDeviceValidator : IDeviceCredentialValidator
    {
        public ValueTask<bool> ValidateActiveDeviceAsync(string deviceId, string credential, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
        public ValueTask<bool> IsActiveDeviceAsync(string deviceId, CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }
}
