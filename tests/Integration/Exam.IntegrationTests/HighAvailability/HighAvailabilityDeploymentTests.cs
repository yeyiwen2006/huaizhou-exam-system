using Exam.Contracts.HighAvailability;
using Exam.Infrastructure.HighAvailability;
using Exam.Infrastructure.ServiceManagement;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Exam.IntegrationTests.HighAvailability;

// All deployment identities and paths in these tests are synthetic.
public sealed class HighAvailabilityDeploymentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "synthetic-ha-startup-" + Guid.NewGuid().ToString("N"));
    private string SelectionPath => Path.Combine(_root, "selection.json");
    private string ConfirmationRoot => Path.Combine(_root, "confirmation");
    public HighAvailabilityDeploymentTests() => Directory.CreateDirectory(_root);

    private FailoverDeploymentSelection Selection => new("server", "node-b", "node-b", "node-a", 2, 4,
        ProtectionMode.EmergencySingleNode, "synthetic-takeover", Path.Combine(ConfirmationRoot, "epochs", "2", "node-b"));

    private WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["ExamServer:ServerId"] = "server", ["ExamServer:NodeId"] = "node-b", ["ExamServer:ServerEpoch"] = "1",
            ["HighAvailability:SelectionPath"] = SelectionPath,
            ["HighAvailability:Nodes:node-b:ConfirmationDirectory"] = ConfirmationRoot,
            ["HighAvailability:Nodes:node-b:ConnectionString"] = "Host=synthetic-primary.example.test;Database=synthetic",
            ["HighAvailability:Nodes:node-b:ResourcesDirectory"] = Path.Combine(_root, "resources"),
            ["HighAvailability:Nodes:node-b:CertificateThumbprint"] = "synthetic-thumbprint",
            ["HighAvailability:Nodes:node-a:ConnectionString"] = "Host=synthetic-standby.example.test;Database=synthetic",
            ["HighAvailability:Nodes:node-a:BusinessHttpsUrl"] = "https://synthetic-standby.example.test/" });
        return builder;
    }

    [Fact]
    public async Task ProtectedSelectionChangesStartupEpochAndConfirmationBinding()
    {
        await new FileFailoverDeploymentSelection(SelectionPath).SaveAsync(Selection, default);
        var builder = Builder(); HighAvailabilityDeployment.Apply(builder);
        Assert.Equal("2", builder.Configuration["ExamServer:ServerEpoch"]);
        Assert.Equal("node-b", builder.Configuration["Confirmation:PrimaryNodeId"]);
        Assert.Equal("node-a", builder.Configuration["Confirmation:StandbyNodeId"]);
        Assert.Equal(Selection.ConfirmationDirectory, builder.Configuration["Confirmation:Directory"]);
        Assert.Equal(builder.Configuration["HighAvailability:Nodes:node-b:ConnectionString"], builder.Configuration["ConnectionStrings:Exam"]);
        Assert.Equal(Path.Combine(_root, "resources"), builder.Configuration["Backup:VersionedResourcesDirectory"]);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("directory")]
    [InlineData("missing-root")]
    public async Task InvalidPersistedBindingsFailBeforeStartup(string invalid)
    {
        var selection = invalid == "directory" ? Selection with { ConfirmationDirectory = Path.Combine(_root, "wrong") } : Selection;
        await new FileFailoverDeploymentSelection(SelectionPath).SaveAsync(selection, default);
        var builder = Builder();
        if (invalid == "identity") builder.Configuration["ExamServer:NodeId"] = "node-a";
        if (invalid == "missing-root") builder.Configuration["HighAvailability:Nodes:node-b:ConfirmationDirectory"] = null;
        Assert.Throws<IOException>(() => HighAvailabilityDeployment.Apply(builder));
        Assert.Equal("1", builder.Configuration["ExamServer:ServerEpoch"]);
    }

    [Theory]
    [InlineData("localhost", "127.0.0.1")]
    [InlineData("same.example.test", "same.example.test")]
    public void TwoConfiguredNodesCannotClaimTheSamePhysicalEndpoint(string first, string second)
    {
        var nodes = new Dictionary<string, FailoverNodeDeployment> {
            ["node-a"] = new("node-a", $"Host={first};Port=5444;Database=synthetic", _root, "synthetic", _root, "https://node-a.example.test/"),
            ["node-b"] = new("node-b", $"Host={second};Port=5444;Database=synthetic", _root, "synthetic", _root, "https://node-b.example.test/") };
        var options = new HighAvailabilityManagementOptions("server", "node-b", nodes, SelectionPath, _root, _root, _root,
            "synthetic-postgres", "synthetic-business", "synthetic-peer-token");
        using var http = new HttpClient();
        Assert.Throws<ArgumentException>(() => new PostgresHighAvailabilityManagementPlatform(options, http));
    }

    public void Dispose()
    {
        PathBoundary.ResolveWithinRoot(Path.GetTempPath(), _root, "Synthetic cleanup escaped its temporary root.");
        Directory.Delete(_root, true);
    }
}
