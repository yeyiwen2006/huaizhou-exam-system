using System.IO;
using System.Text.Json;
using Exam.Admin.ServiceManagement;

namespace Exam.AdminUnitTests.ServiceManagement;

public sealed class ManagementConnectionSettingsTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("exam-management-settings-");
    private string FilePath(string name) => Path.Combine(_directory.FullName, name);
    private readonly ManagementTarget _target = new("https://management.example.test:9443/", "synthetic-server", "synthetic-node");

    [Fact]
    public void DeploymentConfigurationLoadsOnFirstUseAndSavedUserConfigurationTakesPriority()
    {
        var deployment = new ManagementEndpointSettings(FilePath("deployment.json"));
        deployment.Save(_target);
        var settings = new ManagementEndpointSettings(FilePath("user.json"), FilePath("deployment.json"));
        Assert.Equal(_target, settings.Load());
        var userTarget = _target with { Address = "https://127.0.0.1:9555/" };
        settings.Save(userTarget);
        Assert.Equal(userTarget, settings.Load());
        File.WriteAllText(FilePath("user.json"), "invalid", System.Text.Encoding.UTF8);
        Assert.Throws<JsonException>(() => settings.Load());
    }

    [Fact]
    public void ImportPersistsOnlyConnectionFieldsBeforeLoginAndSurvivesRestart()
    {
        var imported = FilePath("import.json");
        File.WriteAllText(imported, """
            {"address":"https://management.example.test:9443/","serverId":"synthetic-server","nodeId":"synthetic-node",
             "password":"synthetic-discard-this-extra-field"}
            """, System.Text.Encoding.UTF8);
        var settings = new ManagementEndpointSettings(FilePath("user.json"));
        using (var model = Model(settings))
        {
            Assert.False(model.CanLogin);
            model.ImportConnectionSettings(imported);
            Assert.True(model.CanLogin);
            Assert.Contains("已导入", model.Message);
        }
        using var restarted = Model(settings);
        Assert.True(restarted.CanLogin);
        Assert.Equal(_target.Address, restarted.Address);
        using var saved = JsonDocument.Parse(File.ReadAllText(FilePath("user.json")));
        Assert.Equal(new[] { "address", "nodeId", "serverId" }, saved.RootElement.EnumerateObject().Select(p => p.Name).Order());
    }

    [Theory]
    [InlineData("{\"address\":\"http://localhost:9443/\",\"serverId\":\"synthetic-server\",\"nodeId\":\"synthetic-node\"}")]
    [InlineData("{\"address\":\"https://remote.invalid/path\",\"serverId\":\"synthetic-server\",\"nodeId\":\"synthetic-node\"}")]
    [InlineData("{\"address\":null}")]
    [InlineData("invalid-json")]
    public void InvalidImportPreservesTheSavedAndDisplayedConnection(string invalid)
    {
        var settings = new ManagementEndpointSettings(FilePath("user.json"));
        settings.Save(_target);
        File.WriteAllText(FilePath("import.json"), invalid, System.Text.Encoding.UTF8);
        using var model = Model(settings);
        model.ImportConnectionSettings(FilePath("import.json"));
        Assert.Contains("导入失败", model.Message);
        Assert.Equal(_target.Address, model.Address);
        Assert.Equal(_target, settings.Load());
    }

    [Fact]
    public async Task MissingConfigurationExplainsTheProblemAndConfiguredRemoteServerAllowsLogin()
    {
        var settings = new ManagementEndpointSettings(FilePath("user.json"));
        using (var missing = Model(settings))
        {
            Assert.False(missing.CanLogin);
            Assert.Contains("联系系统部署人员", missing.ConnectionStatus);
            await missing.LoginAsync("synthetic");
            Assert.Equal(missing.ConnectionStatus, missing.Message);
        }
        settings.Save(_target);
        using var configured = Model(settings);
        Assert.True(configured.CanLogin);
        Assert.True(configured.CanConfigureConnection);
        Assert.Contains("连接已配置", configured.ConnectionStatus);
        Assert.Contains(_target.Address, configured.TargetText);
    }

    [Fact]
    public void DeploymentStaffCanSaveManualSettingsBeforeTheFirstLogin()
    {
        var settings = new ManagementEndpointSettings(FilePath("user.json"));
        using var model = Model(settings);
        model.Address = _target.Address; model.ServerId = _target.ServerId; model.NodeId = _target.NodeId;
        Assert.True(model.SaveConnectionCommand.CanExecute(null));
        model.SaveConnectionCommand.Execute(null);
        Assert.Equal(_target, settings.Load());
    }

    private static ServiceManagementViewModel Model(ManagementEndpointSettings settings) =>
        new(_ => throw new InvalidOperationException("This configuration test must not contact a service."),
            new EmptyStore(), settings);
    private sealed class EmptyStore : IPendingManagementStore
    {
        public PendingManagementRequest? Load(ManagementTarget target) => null;
        public void Save(PendingManagementRequest request) => throw new NotSupportedException();
        public void Clear(PendingManagementRequest request) => throw new NotSupportedException();
    }
    public void Dispose() => _directory.Delete(recursive: true);
}
