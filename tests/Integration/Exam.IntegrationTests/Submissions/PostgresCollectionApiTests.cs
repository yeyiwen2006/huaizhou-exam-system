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
using Exam.Infrastructure.Postgres.Exams;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.Contracts.Versioning;
using Exam.IntegrationTests.Cli;
using System.Text.Json;
using Exam.Server.Api;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Exam.IntegrationTests.Submissions;

public sealed partial class PostgresSubmissionStoreTests
{
    [Fact]
    public async Task Collection_HttpApiRequiresOperatorAndReplaysAuthoritativeEvents()
    {
        var operatorToken = await SyntheticOperators.LoginAsync(fixture.DataSource, "operator-test");
        var catalog = new PostgresExamCatalog(fixture.DataSource, new PostgresExamVersioningService(fixture.DataSource), TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        var created = await catalog.CreateAsync(new(Id("collection-create"), Id("合成回收考试"), new(now.AddHours(1), now.AddHours(2)),
            new([new("subject", "合成科目", "3", [new("group", "合成题组", "single", 1, 1, "3", false,
                new("3", "0", "0"), null)])])), "operator-test");
        var first = await SeedParticipationAsync(examId: created.Summary.ExamId);
        var missing = await SeedParticipationAsync(examId: first.ExamId);
        await SaveFinalAsync(first);
        var builder = WebApplication.CreateBuilder();
        var identity = new ServerNodeEnvelope(Id("api-server"), "node-a", 1);
        builder.Configuration["ExamServer:ServerId"] = identity.ServerId;
        builder.Configuration["ExamServer:NodeId"] = identity.NodeId;
        builder.Configuration["ExamServer:ServerEpoch"] = "1";
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(fixture.DataSource);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IExamVersioningService, PostgresExamVersioningService>();
        builder.Services.AddSingleton<PostgresExamCatalog>();
        builder.Services.AddSingleton<ISubmissionStore, PostgresSubmissionStore>();
        builder.Services.AddSingleton<IGradingTaskQueue, PostgresGradingTaskQueue>();
        builder.Services.AddSingleton<PostgresCollectionCompletionStore>();
        builder.Services.AddSingleton<ICollectionCompletionStore>(provider => provider.GetRequiredService<PostgresCollectionCompletionStore>());
        builder.Services.AddSingleton<IAuthoritativeCollectionCompletionStore>(provider => provider.GetRequiredService<PostgresCollectionCompletionStore>());
        builder.Services.AddSingleton<SubmissionService>();
        builder.Services.AddSingleton<CollectionCompletionService>();
        // 失焦记录 失焦端点属于同一分组：分组在首个请求时整体构建，服务必须在 DI 中可用。
        builder.Services.AddSingleton<IFocusEventStore, InMemoryFocusEventStore>();
        builder.Services.AddSingleton<FocusEventService>();
        builder.Services.AddPostgresOperatorAccounts();
        builder.Services.AddSingleton<IDeviceCredentialValidator, CollectionTestDeviceValidator>();
        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton(new ActiveNodeGuard(fixture.DataSource, new ServerIdentityOptions
        {
            ServerId = identity.ServerId, NodeId = identity.NodeId, ServerEpoch = identity.ServerEpoch
        }, true));
        await using var app = builder.Build();
        app.UseMiddleware<ExamApiAccessMiddleware>();
        app.MapGroup("/api/v1").MapSubmissionApi();
        app.MapGroup("/api/v1").MapExamCatalogApi();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
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
        var exam = (await app.Services.GetRequiredService<PostgresExamCatalog>().ListAsync()).Single(x => x.ExamId == first.ExamId);
        var cli = await CliProcess.RunWithTokenAsync(operatorToken, "monitor", "watch", exam.Name, "--server", app.Urls.Single());
        Assert.Equal(0, cli.Exit);
        Assert.Equal(item, JsonSerializer.Deserialize<CollectionCompletedNotification>(cli.Out, JsonDefaults.Create()));
        var tail = await CliProcess.RunWithTokenAsync(operatorToken, "monitor", "watch", exam.Name,
            item.RosterVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), "--server", app.Urls.Single());
        Assert.Equal(0, tail.Exit);
        Assert.Empty(tail.Out);
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
