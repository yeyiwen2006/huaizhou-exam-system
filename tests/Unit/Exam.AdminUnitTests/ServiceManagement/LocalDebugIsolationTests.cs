#if DEBUG
using System.IO;
using Exam.Admin.ServiceManagement;

namespace Exam.AdminUnitTests.ServiceManagement;

[CollectionDefinition("Local debug environment", DisableParallelization = true)]
public sealed class LocalDebugEnvironmentCollection;

[Collection("Local debug environment")]
public sealed class LocalDebugIsolationTests
{
    [Fact]
    public void DebugManagementSettingsStayWithinTheSelectedDataDirectory()
    {
        const string variable = "EXAM_ADMIN_DATA_DIRECTORY";
        var previous = Environment.GetEnvironmentVariable(variable);
        var temporary = Directory.CreateTempSubdirectory("exam-local-admin-");
        try
        {
            var first = Path.Combine(temporary.FullName, "first");
            Environment.SetEnvironmentVariable(variable, first);
            using (var empty = ServiceManagementViewModel.CreateDefault())
                Assert.Equal(string.Empty, empty.Address);

            var target = new ManagementTarget("https://127.0.0.1:9443/", "synthetic-server", "synthetic-node");
            new ManagementEndpointSettings(Path.Combine(first, "Management", "endpoint.json")).Save(target);
            using (var configured = ServiceManagementViewModel.CreateDefault())
            {
                Assert.Equal(target.Address, configured.Address);
                Assert.Equal(target.ServerId, configured.ServerId);
                Assert.Equal(target.NodeId, configured.NodeId);
                Assert.True(configured.CanLogin);
                Assert.False(configured.ShowHighAvailability);
                Assert.Contains("恢复前的数据库保留", configured.RestoreGuidance);
            }

            Environment.SetEnvironmentVariable(variable, Path.Combine(temporary.FullName, "second"));
            using var independent = ServiceManagementViewModel.CreateDefault();
            Assert.Equal(string.Empty, independent.Address);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
            temporary.Delete(recursive: true);
        }
    }
}
#endif
