using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class RecoveryResourceSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "resource-selection-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _settings = new();
    public RecoveryResourceSelectionTests()
    {
        Directory.CreateDirectory(_root);
        _settings["Recovery:SelectionPath"] = Path.Combine(_root, "selection.json");
        _settings["Backup:VersionedResourcesDirectory"] = Path.Combine(_root, "original-resources");
        _settings["ConnectionStrings:Exam"] = "Host=127.0.0.1;Port=60001;Database=synthetic";
        _settings["ConnectionStrings:ExamRecovery"] = "Host=127.0.0.1;Port=60002;Database=synthetic";
    }
    private string? Get(string key) => _settings.GetValueOrDefault(key);

    [Fact]
    public async Task SelectedRecoveryUsesItsActualResourcesAndNeverFallsBackAfterLoss()
    {
        Assert.Equal(_settings["Backup:VersionedResourcesDirectory"], FileRecoveryDeploymentSelection.ResolveResources(Get, "server", "node", 1));
        var working = Path.Combine(_root, "working"); var resources = Path.Combine(working, "resources"); Directory.CreateDirectory(resources);
        await new FileRecoveryDeploymentSelection(_settings["Recovery:SelectionPath"]!).SelectAsync(
            new("server", "node", "request", "operator", "backup", working, 1, "ExamRecovery"), default);
        Assert.Equal(resources, FileRecoveryDeploymentSelection.ResolveResources(Get, "server", "node", 1));
        Assert.Equal(_settings["ConnectionStrings:ExamRecovery"], FileRecoveryDeploymentSelection.ResolveTimelineConnection(Get, "server", "node", 1));
        _settings["ConnectionStrings:ExamRecoveryTimeline"] = "Host=127.0.0.1;Port=60002;Database=synthetic;Username=synthetic-replication";
        Assert.Equal(_settings["ConnectionStrings:ExamRecoveryTimeline"], FileRecoveryDeploymentSelection.ResolveTimelineConnection(Get, "server", "node", 1));
        Directory.Delete(resources);
        Assert.Throws<IOException>(() => FileRecoveryDeploymentSelection.ResolveResources(Get, "server", "node", 1));
        Assert.Throws<InvalidDataException>(() => FileRecoveryDeploymentSelection.ResolveResources(Get, "another-server", "node", 1));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
