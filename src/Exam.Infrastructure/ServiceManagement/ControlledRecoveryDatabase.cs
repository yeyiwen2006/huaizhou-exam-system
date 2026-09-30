using Exam.Contracts.ServiceManagement;
using Exam.Contracts.Common;
using System.Text.Json;
using Npgsql;

namespace Exam.Infrastructure.ServiceManagement;

public interface IWindowsServiceDeployment
{
    string GetBinaryPath(string serviceName);
}

/// <summary>Starts only a pre-deployed isolated PostgreSQL service; never registers services or edits copied settings.</summary>
public sealed class ControlledRecoveryDatabase
{
    private readonly string _serviceName;
    private readonly string _workingRoot;
    private readonly string _configurationPath;
    private readonly string _expectedCommand;
    private readonly IWindowsServiceDeployment _deployment;
    private readonly WindowsServiceBusinessHost _host;
    private readonly PostgresArchiveRecovery? _archiveRecovery;

    public ControlledRecoveryDatabase(string serviceName, string originalServiceName, string businessServiceName,
        string managementServiceName, string postgresBinDirectory, string workingRoot, string configurationPath,
        string originalConnection, string recoveryConnection, IWindowsServiceControl? control = null,
        IWindowsServiceDeployment? deployment = null, PostgresArchiveRecovery? archiveRecovery = null)
    {
        foreach (var name in new[] { serviceName, originalServiceName, businessServiceName, managementServiceName })
            _ = new WindowsServiceBusinessHost(name, control);
        if (new[] { serviceName, originalServiceName, businessServiceName, managementServiceName }
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4)
            throw new ArgumentException("管理、业务、原库与恢复库必须为四个独立固定服务。");
        var original = new NpgsqlConnectionStringBuilder(originalConnection);
        var recovery = new NpgsqlConnectionStringBuilder(recoveryConnection);
        static bool Local(string? value) => value is "127.0.0.1" or "::1" || string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase);
        if (!Local(recovery.Host) || Local(original.Host) && original.Port == recovery.Port ||
            string.IsNullOrWhiteSpace(recovery.Database) || original.Database != recovery.Database)
            throw new ArgumentException("恢复数据库必须使用独立回环端口及原数据库名。");
        _serviceName = serviceName;
        _workingRoot = Absolute(workingRoot);
        _configurationPath = Absolute(configurationPath);
        _archiveRecovery = archiveRecovery;
        _expectedCommand = BuildServiceCommand(serviceName, postgresBinDirectory, workingRoot, configurationPath, recovery.Port, archiveRecovery?.Options);
        _deployment = deployment ?? new ScmWindowsServiceControl();
        _host = new(serviceName, control);
    }

    public static string BuildServiceCommand(string serviceName, string postgresBinDirectory, string workingRoot,
        string configurationPath, int port) => BuildServiceCommand(serviceName, postgresBinDirectory, workingRoot, configurationPath, port, null);

    public static string BuildServiceCommand(string serviceName, string postgresBinDirectory, string workingRoot,
        string configurationPath, int port, PostgresArchiveRecoveryOptions? archiveRecovery)
    {
        _ = new WindowsServiceBusinessHost(serviceName);
        if (port is < 1 or > 65535) throw new ArgumentException("恢复端口无效。");
        var binary = Path.Combine(Absolute(postgresBinDirectory), "pg_ctl.exe");
        var database = Path.Combine(Absolute(workingRoot), "slot", "database");
        var config = Absolute(configurationPath);
        var options = $"-c config_file=\"{config.Replace('\\', '/')}\" -c data_directory=\"{database.Replace('\\', '/')}\" " +
            $"-c listen_addresses=127.0.0.1 -c port={port} -c archive_mode=off " +
            "-c shared_preload_libraries=\"\" -c session_preload_libraries=\"\" -c local_preload_libraries=\"\"";
        if (archiveRecovery is not null)
        {
            options += BuildArchiveReplayOptions(archiveRecovery);
        }
        else options += BuildStandaloneReplayOptions();
        // First the pg_ctl CRT parses -o, then the service passes these options to postgres's CRT.
        // Escape quotes for the first layer; PostgreSQL paths use forward slashes so no trailing slash escapes arise.
        var quotedOptions = '\"' + options.Replace("\"", "\\\"", StringComparison.Ordinal) + '\"';
        return $"\"{binary}\" runservice -N \"{serviceName}\" -D \"{database}\" -w -t 60 -o {quotedOptions}";
    }

    public static string BuildArchiveReplayOptions(PostgresArchiveRecoveryOptions archiveRecovery)
    {
        archiveRecovery.Validate();
        // Missing files cannot invoke inherited restore/cleanup commands or fetch from an unrelated source.
        // 'exit 1' is a fixed shell failure on both Windows and Unix; all selected WAL is already in pg_wal.
        return " -c restore_command=\"exit 1\" -c archive_cleanup_command=\"\" -c recovery_end_command=\"\"" +
            " -c primary_conninfo=\"\" -c primary_slot_name=\"\"" +
            " -c recovery_target=\"\" -c recovery_target_name=\"\" -c recovery_target_time=\"\" -c recovery_target_xid=\"\"" +
            $" -c recovery_target_lsn={archiveRecovery.TargetLsn} -c recovery_target_timeline={archiveRecovery.Timeline}" +
            " -c recovery_target_inclusive=on -c recovery_target_action=promote";
    }

    public static string BuildStandaloneReplayOptions() =>
        " -c restore_command=\"exit 1\" -c archive_cleanup_command=\"\" -c recovery_end_command=\"\"" +
        " -c primary_conninfo=\"\" -c primary_slot_name=\"\"" +
        " -c recovery_target=\"\" -c recovery_target_name=\"\" -c recovery_target_time=\"\" -c recovery_target_xid=\"\"" +
        " -c recovery_target_lsn=\"\" -c recovery_target_timeline=current -c recovery_target_action=promote";

    private static string Absolute(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.IndexOfAny(['\"', '\'', '\r', '\n', '\0']) >= 0)
            throw new ArgumentException("恢复部署路径必须为无命令分隔符的本机绝对路径。");
        return Path.GetFullPath(path);
    }

    public async Task<RecoveryWorkingCopy> PrepareAndStartAsync(RestoreRequest request,
        Func<CancellationToken, Task<RecoveryWorkingCopy>> prepare,
        Func<CancellationToken, Task<RecoveryWorkingCopy?>> observe, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(_workingRoot) || !File.Exists(_configurationPath))
            throw new IOException("恢复根目录与独立受保护 PostgreSQL 配置必须预先部署。");
        PathBoundary.RejectExistingReparsePoints(_workingRoot, _configurationPath);
        if (_configurationPath.StartsWith(_workingRoot + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("恢复启动配置不能来自恢复工作副本。");
        CheckDeployment();
        RecoveryWorkingCopy working;
        if (_host.Status == BusinessServiceStatus.Running)
        {
            // Repeated POST observes the existing binding, never copies under a running database.
            working = await observe(token) ?? throw new IOException("运行中的恢复实例没有原请求的完整工作副本证据。");
            Bind(working, request);
            _archiveRecovery?.ReadBinding(working, request);
            if (_archiveRecovery is null && !HasTimelineBinding(working))
                throw new IOException("运行副本缺少独立恢复时间线绑定，不能认作已受控恢复。");
            CheckDeployment();
            if (_host.Status != BusinessServiceStatus.Running) throw new IOException("恢复实例状态改变。");
            return working;
        }
        if (_host.Status != BusinessServiceStatus.Stopped) throw new IOException("恢复服务未处于可核实停止状态。");
        working = await prepare(token);
        Bind(working, request);
        if (_host.Status != BusinessServiceStatus.Stopped) throw new IOException("准备期间恢复服务状态改变。");
        if (_archiveRecovery is not null)
        {
            await _archiveRecovery.PrepareAsync(working, request, token);
            if (_host.Status != BusinessServiceStatus.Stopped) throw new IOException("归档准备期间恢复服务状态改变。");
        }
        var database = Path.Combine(working.WorkingDirectory, "database");
        foreach (var name in new[] { "postmaster.pid", "standby.signal", "recovery.signal" })
        {
            var file = Path.Combine(database, name);
            PathBoundary.RejectExistingReparsePoints(_workingRoot, file);
            if (File.Exists(file) && !(name == "recovery.signal" && (_archiveRecovery is not null || HasTimelineBinding(working)) &&
                new FileInfo(file).Length == 0))
                throw new IOException("恢复副本含运行或复制/归档恢复信号，保留原件并阻止启动。");
        }
        var automatic = Path.Combine(database, "postgresql.auto.conf");
        PathBoundary.RejectExistingReparsePoints(_workingRoot, automatic);
        if (File.Exists(automatic))
        {
            using var input = new FileStream(automatic, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 65536) throw new IOException("继承的 PostgreSQL 自动配置无法核实。");
            using var text = new StreamReader(input);
            while (text.ReadLine() is { } line)
                if (line.Trim() is { Length: > 0 } value && !value.StartsWith('#'))
                    throw new IOException("恢复副本含继承自动配置，不能自动执行；保留文件供部署核对。");
        }
        CheckDeployment();
        if (_archiveRecovery is null) await PrepareStandaloneTimelineAsync(working, token);
        await _host.StartAsync(token);
        if (_host.Status != BusinessServiceStatus.Running) throw new IOException("恢复服务启动未确认。");
        CheckDeployment();
        // Running is only process evidence. The caller must still perform actual SQL target and domain checks.
        return working;
    }

    private bool HasTimelineBinding(RecoveryWorkingCopy working)
    {
        var path = Path.Combine(working.WorkingDirectory, "timeline-recovery.json");
        PathBoundary.RejectExistingReparsePoints(_workingRoot, path);
        if (!File.Exists(path)) return false;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > 16384) throw new IOException("恢复时间线绑定记录无效。");
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        var bound = JsonSerializer.Deserialize<RecoveryWorkingCopy>(bytes, JsonDefaults.Create());
        if (bound is null || bound.RequestId != working.RequestId || bound.ActorId != working.ActorId ||
            bound.BackupId != working.BackupId || bound.SourceDirectory != working.SourceDirectory ||
            bound.WorkingDirectory != working.WorkingDirectory ||
            !bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(bound, JsonDefaults.Create())))
            throw new IOException("恢复时间线绑定与原请求不同。");
        return true;
    }

    private async Task PrepareStandaloneTimelineAsync(RecoveryWorkingCopy working, CancellationToken token)
    {
        var database = Path.Combine(working.WorkingDirectory, "database");
        var signal = Path.Combine(database, "recovery.signal");
        var label = Path.Combine(database, "backup_label");
        PathBoundary.RejectExistingReparsePoints(_workingRoot, label);
        if (!HasTimelineBinding(working))
        {
            if (!File.Exists(label)) throw new IOException("恢复副本缺少实际基础备份标签，不能建立恢复时间线。");
            var marker = Path.Combine(working.WorkingDirectory, "timeline-recovery.json");
            await using var output = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(output, working, JsonDefaults.Create(), token);
            await output.FlushAsync(token); output.Flush(true);
        }
        // PostgreSQL consumes backup_label and recovery.signal after the first real recovery.
        // A stopped restart must retain that recovered timeline, not request a second recovery.
        if (File.Exists(signal) || !File.Exists(label)) return;
        token.ThrowIfCancellationRequested();
        await using var flag = new FileStream(signal, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        flag.Flush(true);
    }

    private void Bind(RecoveryWorkingCopy working, RestoreRequest request)
    {
        if (working.RequestId != request.RequestId || working.ActorId != request.ActorId || working.BackupId != request.BackupId ||
            working.WorkingDirectory != Path.Combine(_workingRoot, "slot"))
            throw new IOException("恢复服务目标与原请求工作槽不匹配。");
        PathBoundary.RejectExistingReparsePoints(_workingRoot, Path.Combine(working.WorkingDirectory, "database"));
        if (_archiveRecovery is not null && File.Exists(Path.Combine(working.WorkingDirectory, "timeline-recovery.json")))
            throw new IOException("原请求已绑定基础备份恢复，不能更改为另一归档恢复方式。");
        foreach (var name in new[] { "wal-recovery.json", "wal-recovery-plan.json" })
        {
            var archiveMarker = Path.Combine(working.WorkingDirectory, name);
            PathBoundary.RejectExistingReparsePoints(_workingRoot, archiveMarker);
            if (_archiveRecovery is null && File.Exists(archiveMarker))
                throw new IOException("原请求已绑定归档恢复，不能通过移除部署配置改变恢复方式。");
        }
    }
    private void CheckDeployment()
    {
        if (_deployment.GetBinaryPath(_serviceName) != _expectedCommand)
            throw new IOException("SCM 中的恢复服务启动命令与独立受控部署不一致。");
    }
}
