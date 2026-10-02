using Exam.Contracts.HighAvailability;
using Exam.Infrastructure.HighAvailability;

namespace Exam.UnitTests.HighAvailability;

public sealed class FailoverDeploymentSelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "synthetic-ha-selection-" + Guid.NewGuid().ToString("N"));
    public FailoverDeploymentSelectionTests() => Directory.CreateDirectory(_root);
    private string PathName => Path.Combine(_root, "selection.json");
    private FailoverDeploymentSelection Selection => new("server", "node-b", "node-b", "node-a", 2, 4,
        ProtectionMode.EmergencySingleNode, "synthetic-takeover", Path.Combine(_root, "confirmation", "epochs", "2", "node-b"));
    [Fact]
    public async Task SelectedEpochAndRoleSurviveNewInstances_AndCannotRegress()
    {
        var store = new FileFailoverDeploymentSelection(PathName); Assert.Null(store.Read()); await store.SaveAsync(Selection, default);
        Assert.Equal(Selection, new FileFailoverDeploymentSelection(PathName).Read()); Assert.True(Directory.Exists(Selection.ConfirmationDirectory));
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(Selection with { Epoch = 1 }, default));
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(Selection with { StateVersion = 3 }, default));
        Assert.Equal(Selection, store.Read());
    }
    [Fact]
    public async Task WrongConfiguredIdentityAndCorruptOrDuplicateRecordsFailClosed()
    {
        var store = new FileFailoverDeploymentSelection(PathName); await store.SaveAsync(Selection, default);
        Assert.Throws<IOException>(() => FileFailoverDeploymentSelection.ReadConfigured(_ => PathName, "wrong", "node-b"));
        await File.WriteAllTextAsync(PathName, "{\"serverId\":\"server\",\"serverId\":\"different\"}");
        Assert.ThrowsAny<Exception>(() => store.Read());
    }
    [Fact]
    public async Task SameVersionCannotReplaceAnotherPlanOrSelectAnUnknownActiveNode()
    {
        var store = new FileFailoverDeploymentSelection(PathName); await store.SaveAsync(Selection, default);
        await store.SaveAsync(Selection, default);
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(Selection with { RequestId = "synthetic-other" }, default));
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(Selection with { ActiveNodeId = "unknown" }, default));
        Assert.Equal(Selection, store.Read());
    }
    [Fact]
    public async Task SelectedConfirmationDirectoryMustMatchProtectedNodeAndEpoch()
    {
        var store = new FileFailoverDeploymentSelection(PathName); await store.SaveAsync(Selection, default);
        string? Config(string key) => key switch
        {
            "HighAvailability:SelectionPath" => PathName,
            "HighAvailability:Nodes:node-b:ConfirmationDirectory" => Path.Combine(_root, "confirmation"),
            _ => null
        };
        Assert.Equal(Selection, FileFailoverDeploymentSelection.ReadConfigured(Config, "server", "node-b"));
        await store.SaveAsync(Selection with { StateVersion = 5, ConfirmationDirectory = Path.Combine(_root, "different") }, default);
        Assert.Throws<IOException>(() => FileFailoverDeploymentSelection.ReadConfigured(Config, "server", "node-b"));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
