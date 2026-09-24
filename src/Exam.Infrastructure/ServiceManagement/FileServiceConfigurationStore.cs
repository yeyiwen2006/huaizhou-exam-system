using System.Data;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Application.ServiceManagement;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>
/// Stores the non-secret local service configuration outside the business database. The caller
/// supplies a directory protected by the deployment account; credentials and private keys belong
/// in the separately protected configuration source, never in this file.
/// </summary>
public sealed class FileServiceConfigurationStore : IServiceConfigurationStore
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);
    private readonly string _path;
    private readonly string _directory;

    public FileServiceConfigurationStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("服务配置路径必须是绝对路径。", nameof(path));
        }

        _path = Path.GetFullPath(path);
        _directory = Path.GetDirectoryName(_path)
            ?? throw new ArgumentException("无法确定服务配置目录。", nameof(path));
        if (!Directory.Exists(_directory))
        {
            throw new DirectoryNotFoundException("服务配置目录不存在，不会自动创建或覆盖部署配置。");
        }

        PathBoundary.RejectExistingReparsePoints(_directory, _path);
    }

    public async Task<ServiceConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        CheckPath();
        return await ReadAsync(cancellationToken)
            ?? throw new FileNotFoundException("服务配置不存在，业务服务不得以默认配置启动。", _path);
    }

    /// <summary>
    /// Expected version zero creates the first configuration. A completed return means the new
    /// version replaced the prior file; concurrent writers coordinate through a sibling lock file.
    /// </summary>
    public async Task<ServiceConfiguration> SaveAsync(
        ServiceConfiguration requested,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (expectedVersion < 0 || expectedVersion == long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        Validate(requested);
        CheckPath();
        await using var guard = await AcquireLockAsync(cancellationToken);
        var current = await ReadAsync(cancellationToken);
        if ((current?.Version ?? 0) != expectedVersion)
        {
            throw new DBConcurrencyException("服务配置版本已变化，请刷新后重试。");
        }

        if (current is not null &&
            (!string.Equals(current.ServerId, requested.ServerId, StringComparison.Ordinal) ||
             !string.Equals(current.NodeId, requested.NodeId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("服务配置不能更换逻辑服务器或节点身份。");
        }

        var next = requested with { Version = checked(expectedVersion + 1) };
        var temporary = Path.Combine(_directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, options: FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, next, JsonDefaults.Create(), cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            CheckPath();
            File.Move(temporary, _path, overwrite: true);
            return next;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task<ServiceConfiguration?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var configuration = await JsonSerializer.DeserializeAsync<ServiceConfiguration>(
                input, JsonDefaults.Create(), cancellationToken);
            if (configuration is null || configuration.Version < 1)
            {
                throw new InvalidDataException("服务配置内容不完整或版本无效。");
            }

            Validate(configuration);
            return configuration;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("服务配置无法解析，已保留原文件供检查。", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("服务配置字段无效，已保留原文件供检查。", exception);
        }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var lockPath = _path + ".lock";
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckPath();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, cancellationToken);
            }
        }
    }

    private void CheckPath()
    {
        if (!Directory.Exists(_directory))
        {
            throw new DirectoryNotFoundException("服务配置目录不可用。");
        }

        PathBoundary.RejectExistingReparsePoints(_directory, _path);
        PathBoundary.RejectExistingReparsePoints(_directory, _path + ".lock");
    }

    private static void Validate(ServiceConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.ServerId) ||
            string.IsNullOrWhiteSpace(configuration.NodeId) ||
            string.IsNullOrWhiteSpace(configuration.BindAddress) ||
            string.IsNullOrWhiteSpace(configuration.DataDirectory) ||
            !Path.IsPathFullyQualified(configuration.DataDirectory) ||
            configuration.HttpsPort is < 1 or > 65535)
        {
            throw new ArgumentException("服务配置的身份、地址、端口或数据目录无效。", nameof(configuration));
        }
    }
}
