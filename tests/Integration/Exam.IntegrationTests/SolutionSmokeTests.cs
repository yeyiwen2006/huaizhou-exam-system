using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Exam.IntegrationTests;

public sealed class SolutionSmokeTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void ServerEntryAssembly_IsAvailableToIntegrationHarness()
    {
        Assert.Equal("Exam.Server", typeof(Program).Assembly.GetName().Name);
    }

    [Fact]
    public async Task TimeEndpoint_ReturnsServerIdentityAndUtc8Time()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var before = DateTimeOffset.UtcNow;
        using var response = await client.GetAsync("/api/system/time");
        var after = DateTimeOffset.UtcNow;
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("development-server", document.RootElement.GetProperty("serverId").GetString());
        Assert.Equal("development-node-a", document.RootElement.GetProperty("nodeId").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("serverEpoch").GetInt64());
        var serverTime = document.RootElement.GetProperty("result").GetProperty("serverTime").GetDateTimeOffset();
        Assert.Equal(TimeSpan.FromHours(8), serverTime.Offset);
        Assert.InRange(serverTime, before, after);
    }
}
