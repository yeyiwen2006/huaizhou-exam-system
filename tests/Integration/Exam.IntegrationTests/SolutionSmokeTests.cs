using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Exam.IntegrationTests;

public sealed class SolutionSmokeTests
{
    [Fact]
    public void ServerEntryAssembly_IsAvailableToIntegrationHarness() =>
        Assert.Equal("Exam.Server", typeof(Program).Assembly.GetName().Name);

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public void BusinessHostNeverStartsWithoutDatabaseConfiguration(string environment)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Exam"] = string.Empty });
            });
        });
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("PostgreSQL", error.Message);
    }
}
