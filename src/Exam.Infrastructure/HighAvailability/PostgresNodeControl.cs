using Exam.Application.HighAvailability;
using System.Security.Cryptography;
using System.Text;
using Exam.Contracts.HighAvailability;
using Exam.Infrastructure.ServiceManagement;
using Npgsql;

namespace Exam.Infrastructure.HighAvailability;

public sealed record PostgresNodeConnection(string NodeId, string ConnectionString)
{
    public string? ReplicationConnectionString { get; init; }
}

public sealed class PostgresNodeHealthProbe(
    IReadOnlyDictionary<string, PostgresNodeConnection> nodes,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<NodeState> ObserveAsync(
        string nodeId,
        DeploymentNodeRole role,
        bool keyMaterialReady,
        bool certificateReady,
        CancellationToken cancellationToken = default)
    {
        if (!nodes.TryGetValue(nodeId, out var node))
        {
            throw new KeyNotFoundException($"未配置节点：{nodeId}");
        }

        try
        {
            var settings = new NpgsqlConnectionStringBuilder(node.ConnectionString)
            { CommandTimeout = 5, Pooling = false };
            settings.Timeout = Math.Clamp(settings.Timeout, 3, 15);
            await using var connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            const string sql = """
                SELECT pg_is_in_recovery(),
                       CASE
                           WHEN pg_is_in_recovery() AND NOT pg_is_wal_replay_paused() AND
                                pg_last_wal_receive_lsn() IS NOT NULL AND
                                pg_last_wal_replay_lsn() IS NOT NULL THEN
                               pg_wal_lsn_diff(pg_last_wal_receive_lsn(), pg_last_wal_replay_lsn())
                           WHEN pg_is_in_recovery() THEN 9223372036854775807
                           ELSE 0
                       END;
                """;
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            var inRecovery = reader.GetBoolean(0);
            var replayLag = decimal.ToInt64(reader.GetDecimal(1));
            return new NodeState(
                nodeId,
                role,
                Reachable: true,
                inRecovery,
                replayLag,
                keyMaterialReady,
                certificateReady,
                _timeProvider.GetUtcNow());
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException)
        {
            return new NodeState(
                nodeId,
                role,
                Reachable: false,
                DatabaseInRecovery: role != DeploymentNodeRole.Primary,
                ReplayLagBytes: long.MaxValue,
                keyMaterialReady,
                certificateReady,
                _timeProvider.GetUtcNow());
        }
    }
}

public interface IPostgresNodeRebuilder
{
    Task PrepareRebuildAsync(
        string nodeId,
        string sourceNodeId,
        CancellationToken cancellationToken);
}

public sealed class PostgresFailoverPlatform(
    IReadOnlyDictionary<string, PostgresNodeConnection> nodes,
    IPostgresNodeRebuilder rebuilder) : IFailoverPlatform
{
    public async Task PromoteStandbyAsync(string nodeId, CancellationToken cancellationToken)
    {
        if (!nodes.TryGetValue(nodeId, out var node))
        {
            throw new KeyNotFoundException($"未配置节点：{nodeId}");
        }

        await using var connection = new NpgsqlConnection(node.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var recoveryCommand = new NpgsqlCommand(
                         """
                         SELECT pg_is_in_recovery() AND
                                pg_last_wal_receive_lsn() IS NOT NULL AND
                                pg_last_wal_replay_lsn() IS NOT NULL AND
                                pg_last_wal_receive_lsn() = pg_last_wal_replay_lsn() AND
                                EXISTS
                                (
                                    SELECT 1
                                      FROM pg_stat_wal_receiver
                                     WHERE status = 'streaming'
                                       AND last_msg_receipt_time >= clock_timestamp() - interval '10 seconds'
                                );
                         """,
                         connection))
        {
            if ((bool)(await recoveryCommand.ExecuteScalarAsync(cancellationToken) ?? false) is false)
            {
                throw new InvalidOperationException("目标 PostgreSQL 节点不是已连接且完成重放的可提升备用库。");
            }
        }

        await using (var promoteCommand = new NpgsqlCommand("SELECT pg_promote(true, 60);", connection))
        {
            if ((bool)(await promoteCommand.ExecuteScalarAsync(cancellationToken) ?? false) is false)
            {
                throw new InvalidOperationException("PostgreSQL 备用库未能在限定时间内完成提升。");
            }
        }
    }

    public Task BeginRebuildAsync(
        string nodeId,
        string sourceNodeId,
        CancellationToken cancellationToken) =>
        rebuilder.PrepareRebuildAsync(nodeId, sourceNodeId, cancellationToken);
}

public sealed record StagedNodeRebuildOptions(
    string PostgresBinDirectory,
    string StagingRoot,
    IReadOnlyDictionary<string, PostgresNodeConnection> Nodes,
    TimeSpan Timeout);

public sealed class StagedPostgresNodeRebuilder(
    StagedNodeRebuildOptions options,
    IExternalProcessRunner processRunner) : IPostgresNodeRebuilder
{
    public async Task PrepareRebuildAsync(
        string nodeId,
        string sourceNodeId,
        CancellationToken cancellationToken)
    {
        if (!options.Nodes.TryGetValue(sourceNodeId, out var source))
        {
            throw new KeyNotFoundException($"未配置重建源节点：{sourceNodeId}");
        }

        var root = Path.GetFullPath(options.StagingRoot);
        Directory.CreateDirectory(root);
        var target = PathBoundary.ResolveWithinRoot(
            root,
            Path.Combine(root, nodeId),
            "备机重建暂存目录超出允许范围。");

        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            throw new IOException("备机重建暂存目录必须不存在或为空，不会覆盖旧数据。");
        }

        Directory.CreateDirectory(target);
        // The slot survives pg_basebackup until the rebuilt standby is actually started.
        // A temporary stream slot leaves a WAL recycling gap during verification/resource copy.
        var replicationSlot = "exam_rebuild_" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(nodeId + "\0" + target))).ToLowerInvariant()[..40];
        var managementConnection = new NpgsqlConnectionStringBuilder(source.ConnectionString);
        var connection = string.IsNullOrWhiteSpace(source.ReplicationConnectionString)
            ? managementConnection : new NpgsqlConnectionStringBuilder(source.ReplicationConnectionString);
        if (!string.Equals(connection.Host, managementConnection.Host, StringComparison.OrdinalIgnoreCase) || connection.Port != managementConnection.Port)
            throw new IOException("复制连接须与已核实的源数据库使用相同主机和端口。");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PGHOST"] = connection.Host ?? throw new InvalidOperationException("重建源数据库主机不能为空。"),
            ["PGPORT"] = connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PGUSER"] = connection.Username ?? throw new InvalidOperationException("重建源数据库用户不能为空。"),
            ["PGPASSWORD"] = connection.Password ?? string.Empty,
            ["PGAPPNAME"] = nodeId,
            ["PGSSLMODE"] = connection.SslMode switch
            {
                SslMode.VerifyCA => "verify-ca",
                SslMode.VerifyFull => "verify-full",
                _ => connection.SslMode.ToString().ToLowerInvariant()
            },
            ["PGSSLROOTCERT"] = connection.RootCertificate ?? string.Empty
        };
        var result = await processRunner.RunAsync(
            Path.Combine(options.PostgresBinDirectory, OperatingSystem.IsWindows() ? "pg_basebackup.exe" : "pg_basebackup"),
            ["--pgdata", target, "--write-recovery-conf", "--wal-method=stream", "--checkpoint=fast",
                "--compress=server-gzip:level=1", "--slot=" + replicationSlot, "--create-slot", "--no-password", "--no-clean"],
            environment,
            options.Timeout,
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new IOException($"备机重建基础备份失败：{result.StandardError.Trim()}");
        }
    }
}
