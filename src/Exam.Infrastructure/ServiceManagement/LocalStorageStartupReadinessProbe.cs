using Exam.Application.ServiceManagement;
using Exam.Contracts.ServiceManagement;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>
/// Checks the configured data volume without creating a missing deployment directory.
/// A caller composes this with database, certificate, resource and recovery probes.
/// </summary>
public sealed class LocalStorageStartupReadinessProbe(long minimumAvailableBytes) : IStartupReadinessProbe
{
    private const string PathCheck = "data-directory";
    private const string SpaceCheck = "data-free-space";
    private const string WriteCheck = "data-write";

    public async Task<IReadOnlyList<StartupCheckResult>> RunAsync(
        ServiceConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (minimumAvailableBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumAvailableBytes));
        }

        var directory = configuration.DataDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
        {
            return BlockAll("数据目录不是绝对路径。");
        }

        directory = Path.GetFullPath(directory);
        try
        {
            if (!Directory.Exists(directory))
            {
                return BlockAll("数据目录不存在，不会自动创建。");
            }

            PathBoundary.RejectExistingReparsePoints(directory, directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return BlockAll("数据目录不可访问或经过符号链接。");
        }

        var results = new List<StartupCheckResult>
        {
            new(PathCheck, true, "数据目录存在且路径受控。", true)
        };

        try
        {
            var root = Path.GetPathRoot(directory)
                ?? throw new IOException("无法确定数据目录所在卷。");
            var available = new DriveInfo(root).AvailableFreeSpace;
            results.Add(available >= minimumAvailableBytes
                ? new(SpaceCheck, true, "数据卷可用空间满足配置下限。", true)
                : new(SpaceCheck, false, "数据卷可用空间低于配置下限。", true));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            results.Add(new(SpaceCheck, false, "无法核对数据卷可用空间。", true));
        }

        var probePath = Path.Combine(directory, $".exam-startup-{Guid.NewGuid():N}.probe");
        var writePassed = false;
        try
        {
            await using var output = new FileStream(
                probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, options: FileOptions.WriteThrough);
            await output.WriteAsync(new byte[] { 0x45 }, cancellationToken);
            output.Flush(flushToDisk: true);
            writePassed = true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            writePassed = false;
        }
        finally
        {
            try
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                writePassed = false;
            }
        }

        results.Add(writePassed
            ? new(WriteCheck, true, "数据目录可写且刷新成功。", true)
            : new(WriteCheck, false, "数据目录写入、刷新或清理失败。", true));

        return results;
    }

    private static IReadOnlyList<StartupCheckResult> BlockAll(string reason) =>
    [
        new(PathCheck, false, reason, true),
        new(SpaceCheck, false, "数据目录未就绪，未执行空间检查。", true),
        new(WriteCheck, false, "数据目录未就绪，未执行写入检查。", true)
    ];
}
