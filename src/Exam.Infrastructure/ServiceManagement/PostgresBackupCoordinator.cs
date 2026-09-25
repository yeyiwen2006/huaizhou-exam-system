using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Npgsql;

namespace Exam.Infrastructure.ServiceManagement;

public sealed record PostgresBackupOptions(
    string PostgresBinDirectory,
    string ConnectionString,
    string AllowedBackupRoot,
    string RestoreStagingRoot,
    string VersionedResourcesDirectory,
    string ProtectedConfigurationDirectory,
    TimeSpan ToolTimeout);

public sealed class PostgresBackupCoordinator(
    PostgresBackupOptions options,
    IExternalProcessRunner processRunner,
    TimeProvider? timeProvider = null,
    NpgsqlDataSource? dataSource = null) : IBackupCoordinator
{
    private const int ManifestFormatVersion = 2;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<BackupResult> CreateAsync(BackupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireRequest(request.RequestId, request.ActorId);
        ValidateOptions();
        var destination = ResolveWithinRoot(options.AllowedBackupRoot, request.DestinationDirectory);
        await using var operationLock = await DatabaseOperationLock.AcquireAsync(
            dataSource,
            [$"backup-request:{request.RequestId}", $"backup-destination:{destination}"],
            cancellationToken);

        if (dataSource is not null &&
            await FindByRequestIdAsync(request, destination, cancellationToken) is { } replay)
        {
            return replay;
        }

        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            var recovered = await TryRecoverCompletedBackupAsync(request, destination, cancellationToken);
            if (recovered is not null)
            {
                if (dataSource is not null)
                {
                    await SaveBackupRecordAsync(recovered, request, cancellationToken);
                }

                return recovered;
            }

            throw new IOException("备份目标目录必须不存在或为空，且不能含另一项或不完整的备份。");
        }

        var expectedResources = await CaptureDirectoryAsync(
            options.VersionedResourcesDirectory,
            "版本化资源源目录",
            cancellationToken);
        var expectedProtected = await CaptureDirectoryAsync(
            options.ProtectedConfigurationDirectory,
            "受保护配置源目录",
            cancellationToken);

        Directory.CreateDirectory(destination);
        PathBoundary.RejectExistingReparsePoints(options.AllowedBackupRoot, destination);
        var databaseDirectory = ResolveChild(destination, "database");
        var resourcesDirectory = ResolveChild(destination, "resources");
        var protectedDirectory = ResolveChild(destination, "protected-configuration");
        Directory.CreateDirectory(databaseDirectory);

        var connection = new NpgsqlConnectionStringBuilder(options.ConnectionString);
        var backup = await processRunner.RunAsync(
            Path.Combine(options.PostgresBinDirectory, "pg_basebackup.exe"),
            [
                "--pgdata", databaseDirectory,
                "--format=plain",
                "--wal-method=stream",
                "--checkpoint=fast",
                "--no-password"
            ],
            BuildPostgresEnvironment(connection),
            options.ToolTimeout,
            cancellationToken);
        EnsureSuccess("PostgreSQL 基础备份", backup);

        await CopySnapshotAsync(
            options.VersionedResourcesDirectory,
            resourcesDirectory,
            expectedResources,
            cancellationToken);
        await CopySnapshotAsync(
            options.ProtectedConfigurationDirectory,
            protectedDirectory,
            expectedProtected,
            cancellationToken);

        await RequireSnapshotUnchangedAsync(
            options.VersionedResourcesDirectory,
            expectedResources,
            "版本化资源",
            cancellationToken);
        await RequireSnapshotUnchangedAsync(
            options.ProtectedConfigurationDirectory,
            expectedProtected,
            "受保护配置",
            cancellationToken);
        await RequireSnapshotUnchangedAsync(resourcesDirectory, expectedResources, "备份资源", cancellationToken);
        await RequireSnapshotUnchangedAsync(protectedDirectory, expectedProtected, "备份受保护配置", cancellationToken);

        var backupId = Guid.NewGuid().ToString("N");
        var now = _timeProvider.GetUtcNow().ToUniversalTime();
        var completedAt = new DateTimeOffset(now.Ticks - (now.Ticks % 10), TimeSpan.Zero);
        var manifest = new BackupManifest(
            ManifestFormatVersion,
            backupId,
            request.RequestId,
            completedAt,
            request.ActorId,
            "database",
            "resources",
            "protected-configuration",
            expectedResources,
            expectedProtected);
        await File.WriteAllTextAsync(
            Path.Combine(destination, "exam-backup.json"),
            JsonSerializer.Serialize(manifest, JsonDefaults.Create()),
            cancellationToken);

        var result = ToResult(destination, manifest);
        if (dataSource is not null)
        {
            await SaveBackupRecordAsync(result, request, cancellationToken);
        }

        return result;
    }

    public async Task RestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireRequest(request.RequestId, request.ActorId);
        ValidateOptions();
        var source = ResolveWithinRoot(options.AllowedBackupRoot, request.SourceDirectory);
        var stagingRoot = Path.GetFullPath(options.RestoreStagingRoot);
        Directory.CreateDirectory(stagingRoot);
        var staging = ResolveWithinRoot(stagingRoot, Path.Combine(stagingRoot, request.BackupId));
        await using var operationLock = await DatabaseOperationLock.AcquireAsync(
            dataSource,
            [$"restore-request:{request.RequestId}", $"restore-staging:{staging}"],
            cancellationToken);

        if (dataSource is not null &&
            await FindRestoreRecordAsync(request, source, staging, cancellationToken))
        {
            await ValidateCompletedRestoreAsync(staging, request, source, cancellationToken);
            return;
        }

        if (Directory.Exists(staging))
        {
            await ValidateCompletedRestoreAsync(staging, request, source, cancellationToken);
            if (dataSource is not null)
            {
                await SaveRestoreRecordAsync(request, source, staging, cancellationToken);
            }

            return;
        }

        var manifest = await ReadManifestAsync(source, cancellationToken);
        if (!string.Equals(manifest.BackupId, request.BackupId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("请求的备份标识与备份清单不一致。");
        }

        var sourceDatabase = ResolveChild(source, manifest.DatabaseDirectory);
        var sourceResources = ResolveChild(source, manifest.ResourcesDirectory);
        var sourceProtected = ResolveChild(source, manifest.ProtectedConfigurationDirectory);
        await ValidateBackupContentsAsync(
            sourceDatabase,
            sourceResources,
            sourceProtected,
            manifest,
            verifyDatabase: true,
            cancellationToken);

        Directory.CreateDirectory(staging);
        var stagingDatabase = ResolveChild(staging, "database");
        var stagingResources = ResolveChild(staging, "resources");
        var stagingProtected = ResolveChild(staging, "protected-configuration");
        await CopyDirectoryFixedAsync(sourceDatabase, stagingDatabase, cancellationToken);
        await CopySnapshotAsync(sourceResources, stagingResources, manifest.ResourceFiles, cancellationToken);
        await CopySnapshotAsync(sourceProtected, stagingProtected, manifest.ProtectedConfigurationFiles, cancellationToken);

        await ValidateBackupContentsAsync(
            stagingDatabase,
            stagingResources,
            stagingProtected,
            manifest,
            verifyDatabase: true,
            cancellationToken);
        File.Copy(
            Path.Combine(source, "exam-backup.json"),
            Path.Combine(staging, "exam-backup.json"),
            overwrite: false);
        await File.WriteAllTextAsync(
            Path.Combine(staging, "restore-verified.json"),
            JsonSerializer.Serialize(
                new RestoreMarker(
                    request.RequestId,
                    request.ActorId,
                    request.BackupId,
                    source,
                    _timeProvider.GetUtcNow().ToUniversalTime()),
                JsonDefaults.Create()),
            cancellationToken);
        if (dataSource is not null)
        {
            await SaveRestoreRecordAsync(request, source, staging, cancellationToken);
        }
    }

    private void ValidateOptions()
    {
        if (!Path.IsPathFullyQualified(options.PostgresBinDirectory) ||
            !Path.IsPathFullyQualified(options.AllowedBackupRoot) ||
            !Path.IsPathFullyQualified(options.RestoreStagingRoot) ||
            !Path.IsPathFullyQualified(options.VersionedResourcesDirectory) ||
            !Path.IsPathFullyQualified(options.ProtectedConfigurationDirectory) ||
            string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new InvalidOperationException("备份工具的目录和数据库连接必须使用完整配置。");
        }

        RequireNonEmptyDirectory(options.VersionedResourcesDirectory, "版本化资源源目录");
        RequireNonEmptyDirectory(options.ProtectedConfigurationDirectory, "受保护配置源目录");
        var roots = new[]
        {
            options.AllowedBackupRoot,
            options.RestoreStagingRoot,
            options.VersionedResourcesDirectory,
            options.ProtectedConfigurationDirectory
        }.Select(Path.GetFullPath).ToArray();
        for (var left = 0; left < roots.Length; left++)
        {
            PathBoundary.RejectExistingReparsePoints(roots[left], roots[left]);
            for (var right = left + 1; right < roots.Length; right++)
            {
                if (ContainsOrEquals(roots[left], roots[right]) || ContainsOrEquals(roots[right], roots[left]))
                {
                    throw new InvalidOperationException("备份根目录、恢复暂存目录、资源目录和受保护配置目录不得相同或互相包含。");
                }
            }
        }
    }

    private async Task<BackupResult?> TryRecoverCompletedBackupAsync(
        BackupRequest request,
        string destination,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(destination, "exam-backup.json")))
        {
            return null;
        }

        var manifest = await ReadManifestAsync(destination, cancellationToken);
        if (!string.Equals(manifest.RequestId, request.RequestId, StringComparison.Ordinal) ||
            !string.Equals(manifest.ActorId, request.ActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("现有备份清单与 requestId 或操作者不匹配。");
        }

        await ValidateBackupContentsAsync(
            ResolveChild(destination, manifest.DatabaseDirectory),
            ResolveChild(destination, manifest.ResourcesDirectory),
            ResolveChild(destination, manifest.ProtectedConfigurationDirectory),
            manifest,
            verifyDatabase: true,
            cancellationToken);
        return ToResult(destination, manifest);
    }

    private async Task<BackupManifest> ReadManifestAsync(string root, CancellationToken cancellationToken)
    {
        var path = ResolveChild(root, "exam-backup.json");
        if (!File.Exists(path))
        {
            throw new InvalidDataException("备份缺少 exam-backup.json 清单。");
        }

        var manifest = JsonSerializer.Deserialize<BackupManifest>(
            await File.ReadAllTextAsync(path, cancellationToken),
            JsonDefaults.Create()) ?? throw new InvalidDataException("备份清单无法解析。");
        if (manifest.FormatVersion != ManifestFormatVersion ||
            string.IsNullOrWhiteSpace(manifest.BackupId) ||
            string.IsNullOrWhiteSpace(manifest.RequestId) ||
            string.IsNullOrWhiteSpace(manifest.ActorId) ||
            manifest.ResourceFiles.Count == 0 ||
            manifest.ProtectedConfigurationFiles.Count == 0)
        {
            throw new InvalidDataException("备份清单版本或必需字段无效。");
        }

        return manifest;
    }

    private async Task ValidateBackupContentsAsync(
        string databaseDirectory,
        string resourcesDirectory,
        string protectedDirectory,
        BackupManifest manifest,
        bool verifyDatabase,
        CancellationToken cancellationToken)
    {
        RequireNonEmptyDirectory(databaseDirectory, "数据库基础备份");
        await RequireSnapshotUnchangedAsync(resourcesDirectory, manifest.ResourceFiles, "版本化资源", cancellationToken);
        await RequireSnapshotUnchangedAsync(
            protectedDirectory,
            manifest.ProtectedConfigurationFiles,
            "受保护配置",
            cancellationToken);
        if (!verifyDatabase)
        {
            return;
        }

        var verification = await processRunner.RunAsync(
            Path.Combine(options.PostgresBinDirectory, "pg_verifybackup.exe"),
            [databaseDirectory],
            new Dictionary<string, string>(),
            options.ToolTimeout,
            cancellationToken);
        EnsureSuccess("PostgreSQL 备份校验", verification);
    }

    private async Task<BackupResult?> FindByRequestIdAsync(
        BackupRequest request,
        string resolvedDestination,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT backup_id, destination_directory, completed_at, includes_database,
                   includes_protected_configuration, includes_versioned_resources, actor_id
              FROM exam.backup_records
             WHERE request_id = @request_id;
            """;
        await using var command = dataSource!.CreateCommand(sql);
        command.Parameters.AddWithValue("request_id", request.RequestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(reader.GetString(1), resolvedDestination, comparison) ||
            !string.Equals(reader.GetString(6), request.ActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("requestId 已用于不同的备份目录或操作者。");
        }

        return new BackupResult(
            reader.GetString(0),
            reader.GetString(1),
            new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero),
            reader.GetBoolean(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5));
    }

    private async Task SaveBackupRecordAsync(
        BackupResult result,
        BackupRequest request,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO exam.backup_records
                (backup_id, request_id, destination_directory, completed_at,
                 includes_database, includes_protected_configuration,
                 includes_versioned_resources, actor_id)
            VALUES
                (@backup_id, @request_id, @destination_directory, @completed_at,
                 @includes_database, @includes_protected_configuration,
                 @includes_versioned_resources, @actor_id)
            ON CONFLICT (request_id) DO NOTHING;
            """;
        await using var command = dataSource!.CreateCommand(sql);
        command.Parameters.AddWithValue("backup_id", result.BackupId);
        command.Parameters.AddWithValue("request_id", request.RequestId);
        command.Parameters.AddWithValue("destination_directory", result.DestinationDirectory);
        command.Parameters.AddWithValue("completed_at", result.CompletedAt);
        command.Parameters.AddWithValue("includes_database", result.IncludesDatabase);
        command.Parameters.AddWithValue(
            "includes_protected_configuration",
            result.IncludesProtectedConfiguration);
        command.Parameters.AddWithValue("includes_versioned_resources", result.IncludesVersionedResources);
        command.Parameters.AddWithValue("actor_id", request.ActorId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            var winner = await FindByRequestIdAsync(request, result.DestinationDirectory, cancellationToken);
            if (winner is null || winner != result)
            {
                throw new DBConcurrencyException("requestId 的备份结果已被另一并发操作占用。");
            }
        }
    }

    private async Task ValidateCompletedRestoreAsync(
        string staging,
        RestoreRequest request,
        string source,
        CancellationToken cancellationToken)
    {
        var manifest = await ReadManifestAsync(staging, cancellationToken);
        var markerPath = ResolveChild(staging, "restore-verified.json");
        if (!File.Exists(markerPath))
        {
            throw new IOException("恢复暂存目录已经存在但尚未完成校验，不会覆盖或当作成功恢复。");
        }

        var marker = JsonSerializer.Deserialize<RestoreMarker>(
            await File.ReadAllTextAsync(markerPath, cancellationToken),
            JsonDefaults.Create()) ?? throw new InvalidDataException("恢复校验标记无法解析。");
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(manifest.BackupId, request.BackupId, StringComparison.Ordinal) ||
            !string.Equals(marker.RequestId, request.RequestId, StringComparison.Ordinal) ||
            !string.Equals(marker.ActorId, request.ActorId, StringComparison.Ordinal) ||
            !string.Equals(marker.BackupId, request.BackupId, StringComparison.Ordinal) ||
            !string.Equals(marker.SourceDirectory, source, pathComparison))
        {
            throw new InvalidOperationException("现有恢复结果与 requestId、备份、来源目录或操作者不匹配。");
        }

        await ValidateBackupContentsAsync(
            ResolveChild(staging, manifest.DatabaseDirectory),
            ResolveChild(staging, manifest.ResourcesDirectory),
            ResolveChild(staging, manifest.ProtectedConfigurationDirectory),
            manifest,
            verifyDatabase: true,
            cancellationToken);
    }

    private async Task<bool> FindRestoreRecordAsync(
        RestoreRequest request,
        string source,
        string staging,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT backup_id, source_directory, staging_directory, actor_id
              FROM exam.restore_records
             WHERE request_id = @request_id;
            """;
        await using var command = dataSource!.CreateCommand(sql);
        command.Parameters.AddWithValue("request_id", request.RequestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return false;
        }

        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(reader.GetString(0), request.BackupId, StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(1), source, pathComparison) ||
            !string.Equals(reader.GetString(2), staging, pathComparison) ||
            !string.Equals(reader.GetString(3), request.ActorId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("requestId 已用于不同的恢复备份、目录或操作者。");
        }

        return true;
    }

    private async Task SaveRestoreRecordAsync(
        RestoreRequest request,
        string source,
        string staging,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO exam.restore_records
                (request_id, backup_id, source_directory, staging_directory, actor_id, completed_at)
            VALUES
                (@request_id, @backup_id, @source_directory, @staging_directory, @actor_id, @completed_at)
            ON CONFLICT (request_id) DO NOTHING;
            """;
        await using var command = dataSource!.CreateCommand(sql);
        command.Parameters.AddWithValue("request_id", request.RequestId);
        command.Parameters.AddWithValue("backup_id", request.BackupId);
        command.Parameters.AddWithValue("source_directory", source);
        command.Parameters.AddWithValue("staging_directory", staging);
        command.Parameters.AddWithValue("actor_id", request.ActorId);
        command.Parameters.AddWithValue("completed_at", _timeProvider.GetUtcNow().ToUniversalTime());
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0 &&
            !await FindRestoreRecordAsync(request, source, staging, cancellationToken))
        {
            throw new DBConcurrencyException("requestId 的恢复结果已被另一并发操作占用。");
        }
    }

    private static IReadOnlyDictionary<string, string> BuildPostgresEnvironment(
        NpgsqlConnectionStringBuilder connection)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PGHOST"] = connection.Host ?? throw new InvalidOperationException("备份数据库主机不能为空。"),
            ["PGPORT"] = connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PGUSER"] = connection.Username ?? throw new InvalidOperationException("备份数据库用户不能为空。"),
            ["PGDATABASE"] = string.IsNullOrWhiteSpace(connection.Database) ? "postgres" : connection.Database,
            ["PGSSLMODE"] = connection.SslMode.ToString().ToLowerInvariant()
        };
        if (!string.IsNullOrEmpty(connection.Password))
        {
            values["PGPASSWORD"] = connection.Password;
        }

        return values;
    }

    private static async Task<IReadOnlyList<BackupFileEntry>> CaptureDirectoryAsync(
        string root,
        string description,
        CancellationToken cancellationToken)
    {
        RequireNonEmptyDirectory(root, description);
        var fullRoot = Path.GetFullPath(root);
        PathBoundary.RejectExistingReparsePoints(fullRoot, fullRoot);
        var files = EnumerateFilesWithoutLinks(fullRoot);
        var result = new List<BackupFileEntry>(files.Count);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = NormalizeRelative(Path.GetRelativePath(fullRoot, file));
            await using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            result.Add(new BackupFileEntry(
                relative,
                stream.Length,
                Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))));
        }

        if (result.Count == 0)
        {
            throw new DirectoryNotFoundException($"{description}不存在或为空：{root}");
        }

        return result.AsReadOnly();
    }

    private static async Task RequireSnapshotUnchangedAsync(
        string root,
        IReadOnlyList<BackupFileEntry> expected,
        string description,
        CancellationToken cancellationToken)
    {
        var actual = await CaptureDirectoryAsync(root, description, cancellationToken);
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidDataException($"{description}在备份或恢复期间发生变化，未得到一致快照。");
        }
    }

    private static async Task CopySnapshotAsync(
        string source,
        string destination,
        IReadOnlyList<BackupFileEntry> snapshot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceFile = ResolveChild(source, entry.RelativePath);
            var target = ResolveChild(destination, entry.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = new FileStream(
                sourceFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(
                target,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
    }

    private static async Task CopyDirectoryFixedAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        RequireNonEmptyDirectory(source, "复制源目录");
        var files = EnumerateFilesWithoutLinks(Path.GetFullPath(source));
        Directory.CreateDirectory(destination);
        foreach (var sourceFile in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, sourceFile);
            var target = ResolveChild(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
    }

    private static List<string> EnumerateFilesWithoutLinks(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            PathBoundary.RejectExistingReparsePoints(root, directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnauthorizedAccessException($"备份内容不允许包含符号链接或目录联接：{entry}");
                }

                if (Directory.Exists(entry))
                {
                    pending.Push(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
        }

        files.Sort((left, right) => StringComparer.Ordinal.Compare(
            NormalizeRelative(Path.GetRelativePath(root, left)),
            NormalizeRelative(Path.GetRelativePath(root, right))));
        return files;
    }

    private static void EnsureSuccess(string operation, ExternalProcessResult result)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        // 工具输出可能包含连接信息或受保护路径，不进入异常和上层日志。
        throw new IOException($"{operation}失败（退出码 {result.ExitCode}）。请检查 PostgreSQL 工具和部署环境。");
    }

    private static BackupResult ToResult(string destination, BackupManifest manifest) =>
        new(
            manifest.BackupId,
            destination,
            manifest.CompletedAt,
            IncludesDatabase: true,
            IncludesProtectedConfiguration: true,
            IncludesVersionedResources: true);

    private static string ResolveWithinRoot(string root, string requested) =>
        PathBoundary.ResolveWithinRoot(
            root,
            requested,
            "请求目录超出允许的备份或恢复根目录。");

    private static string ResolveChild(string parent, string child)
    {
        if (Path.IsPathFullyQualified(child))
        {
            throw new InvalidDataException("备份清单中的目录必须使用相对路径。");
        }

        return ResolveWithinRoot(parent, Path.Combine(parent, child));
    }

    private static bool ContainsOrEquals(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative == "." ||
               (!Path.IsPathFullyQualified(relative) && relative != ".." &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private static string NormalizeRelative(string relative) =>
        relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static void RequireNonEmptyDirectory(string path, string description)
    {
        if (!Directory.Exists(path) || !Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new DirectoryNotFoundException($"{description}不存在或为空：{path}");
        }
    }

    private static void RequireRequest(string requestId, string actorId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(actorId))
        {
            throw new ArgumentException("requestId 和 actorId 不能为空。");
        }
    }

    private sealed record BackupFileEntry(string RelativePath, long Length, string Sha256);

    private sealed record BackupManifest(
        int FormatVersion,
        string BackupId,
        string RequestId,
        DateTimeOffset CompletedAt,
        string ActorId,
        string DatabaseDirectory,
        string ResourcesDirectory,
        string ProtectedConfigurationDirectory,
        IReadOnlyList<BackupFileEntry> ResourceFiles,
        IReadOnlyList<BackupFileEntry> ProtectedConfigurationFiles);

    private sealed record RestoreMarker(
        string RequestId,
        string ActorId,
        string BackupId,
        string SourceDirectory,
        DateTimeOffset VerifiedAt);

    private sealed class DatabaseOperationLock(NpgsqlConnection connection) : IAsyncDisposable
    {
        public static async Task<DatabaseOperationLock?> AcquireAsync(
            NpgsqlDataSource? dataSource,
            IReadOnlyList<string> keys,
            CancellationToken cancellationToken)
        {
            if (dataSource is null)
            {
                return null;
            }

            var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            try
            {
                foreach (var key in keys.Order(StringComparer.Ordinal))
                {
                    await using var command = new NpgsqlCommand(
                        "SELECT pg_advisory_lock(hashtextextended(@key, 0));",
                        connection);
                    command.Parameters.AddWithValue("key", key);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                return new DatabaseOperationLock(connection);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock_all();", connection);
                await command.ExecuteNonQueryAsync(CancellationToken.None);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
