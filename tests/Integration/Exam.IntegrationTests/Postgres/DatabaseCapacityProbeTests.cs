using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Exam.Application.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Server.Capacity;
using Exam.Server.Diagnostics;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Exam.IntegrationTests.Postgres;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class DatabaseCapacityProbeTests(PostgresDatabaseFixture database)
{
    [Fact]
    public async Task DiagnosticsIsOperatorOnly_AndReturnsSafeRealDatabaseAggregates()
    {
        var operatorToken = await SyntheticOperators.LoginAsync(database.DataSource, "capacity-operator");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(database.DataSource);
        builder.Services.AddExamCapacity(builder.Configuration, new(16, 100, 3, 1, 0, 10, false));
        builder.Services.AddSingleton(new ServerNodeEnvelope("capacity-server", "node-a", 1));
        builder.Services.AddPostgresOperatorAccounts();
        builder.Services.AddSingleton<IDeviceCredentialValidator, SyntheticDeviceValidator>();
        builder.Services.AddSingleton(new ActiveNodeGuard(database.DataSource,
            new ServerIdentityOptions { ServerId = "capacity-server", NodeId = "node-a", ServerEpoch = 1 }, true));
        await using var app = builder.Build();
        app.UseMiddleware<ExamApiAccessMiddleware>();
        app.MapExamDiagnostics();
        await app.StartAsync();
        using var client = app.GetTestClient();
        const string path = "/api/v1/diagnostics/runtime";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Exam-Device-Id", "device");
        client.DefaultRequestHeaders.Add("X-Exam-Device-Credential", "synthetic-device-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<RuntimeDiagnosticsSnapshot>>();
        Assert.Equal("capacity-server", envelope!.ServerId);
        Assert.True(envelope.Result!.DatabaseActivity.Available);
        Assert.True(envelope.Result.DatabaseActivity.ClientConnections >= 1);
        Assert.Equal(3, envelope.Result.Requests.Count);
        Assert.True(envelope.Result.WorkingSetBytes > 0);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(operatorToken, text);
        Assert.DoesNotContain("synthetic-device-secret", text);
        Assert.DoesNotContain(database.ConnectionString, text);
        await app.StopAsync();
    }

    [Fact]
    public async Task ActualCapacityMatchesDeclaredBudget_StartupRejectsDifferentDatabaseSettings()
    {
        int maximum;
        int reserved;
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand("""
                SELECT current_setting('max_connections')::integer,
                       current_setting('superuser_reserved_connections')::integer
                       + COALESCE(NULLIF(current_setting('reserved_connections', true), ''), '0')::integer;
                """, connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            maximum = reader.GetInt32(0);
            reserved = reader.GetInt32(1);
        }
        var settings = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        var options = new DatabaseCapacityOptions(settings.MaxPoolSize, maximum, reserved, 1, 0, 3, true);
        options.Validate();
        var probe = new DatabaseCapacityProbe(database.DataSource, options);
        Assert.Null(await probe.CheckAsync(CancellationToken.None));
        await new DatabaseCapacityStartupCheck(probe, options).StartAsync(CancellationToken.None);

        var stale = options with { PostgresMaxConnections = maximum + 1 };
        var staleProbe = new DatabaseCapacityProbe(database.DataSource, stale);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DatabaseCapacityStartupCheck(staleProbe, stale).StartAsync(CancellationToken.None));
        Assert.Contains("部署预算不一致", error.Message);
        Assert.DoesNotContain(settings.Username!, error.Message);
    }

    private sealed class SyntheticDeviceValidator : IDeviceCredentialValidator
    {
        public ValueTask<bool> ValidateActiveDeviceAsync(string deviceId, string credential, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
        public ValueTask<bool> IsActiveDeviceAsync(string deviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);
    }
}
