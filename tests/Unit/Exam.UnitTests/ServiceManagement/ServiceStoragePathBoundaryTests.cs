using System.Diagnostics;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.UnitTests.ServiceManagement;

public sealed class ServiceStoragePathBoundaryTests
{
    [Fact]
    public async Task DeploymentDirectoryJunctionIsRejectedWithoutCreatingFilesInItsTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "f01-path-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target"); var link = Path.Combine(root, "link");
        Directory.CreateDirectory(target);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                start.Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"";
                using var process = Process.Start(start)!;
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
            }
            else Directory.CreateSymbolicLink(link, target);
            Assert.Throws<UnauthorizedAccessException>(() => new FileServiceConfigurationStore(Path.Combine(link, "service.json")));
            var config = new ServiceConfiguration("server", "node", "127.0.0.1", 443, link, false, 1);
            var checks = await new LocalStorageStartupReadinessProbe(1).RunAsync(config, CancellationToken.None);
            Assert.All(checks, check => { Assert.False(check.Passed); Assert.True(check.BlocksWrites); });
            Assert.Empty(Directory.GetFileSystemEntries(target));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task PreCancelledStorageProbeDoesNotTouchDeploymentDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "f01-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LocalStorageStartupReadinessProbe(1).RunAsync(
                new("server", "node", "127.0.0.1", 443, root, false, 1), cancel.Token));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
