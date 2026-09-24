using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class FileServiceConfigurationBoundaryTests
{
    [Theory]
    [InlineData("missing-ha")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("quoted-port")]
    [InlineData("oversized")]
    public async Task InvalidConfigurationIsNotReinterpretedOrOverwritten(string defect)
    {
        var root = Path.Combine(Path.GetTempPath(), "f01-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "service.json");
            var original = new ServiceConfiguration("server", "node", "127.0.0.1", 443, root, false, 1);
            var json = JsonSerializer.Serialize(original, JsonDefaults.Create());
            json = defect switch
            {
                "missing-ha" => json.Replace("\"highAvailabilityEnabled\":false,", ""),
                "duplicate" => json.Replace("\"httpsPort\":443", "\"httpsPort\":8443,\"httpsPort\":443"),
                "unknown" => json.Replace("\"httpsPort\":443", "\"unexpected\":true,\"httpsPort\":443"),
                "quoted-port" => json.Replace("\"httpsPort\":443", "\"httpsPort\":\"443\""),
                _ => json + new string(' ', 65536)
            };
            await File.WriteAllTextAsync(path, json);
            var store = new FileServiceConfigurationStore(path);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(original, 1));
            Assert.Equal(json, await File.ReadAllTextAsync(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConcurrentSnapshotReadersDoNotBreakAtomicConfigurationPublication()
    {
        var root = Path.Combine(Path.GetTempPath(), "f01-readers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new FileServiceConfigurationStore(Path.Combine(root, "service.json"));
            var config = await store.SaveAsync(new("server", "node", "127.0.0.1", 443, root, false, 0), 0);
            var readers = Enumerable.Range(0, 4).Select(async _ =>
            {
                for (var i = 0; i < 20; i++)
                {
                    var snapshot = await store.LoadAsync();
                    Assert.Equal(snapshot.Version == 1 ? 443 : 7443, snapshot.HttpsPort);
                }
            }).ToArray();
            var updated = await store.SaveAsync(config with { HttpsPort = 7443 }, 1);
            await Task.WhenAll(readers);
            Assert.Equal(updated, await store.LoadAsync());
        }
        finally { Directory.Delete(root, true); }
    }
}
