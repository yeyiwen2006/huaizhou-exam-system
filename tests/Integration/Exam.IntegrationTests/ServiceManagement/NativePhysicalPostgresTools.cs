using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Exam.Infrastructure.ServiceManagement;
using Npgsql;

namespace Exam.IntegrationTests.ServiceManagement;

internal interface IPhysicalPostgresTools : IExternalProcessRunner, IAsyncDisposable
{
    bool IsNative { get; }
    string? NativeBinDirectory { get; }
    int BaseBackupCalls { get; }
    int VerifyCalls { get; }
    int RestoredPort { get; }
    Task StartRestoredAsync(string verifiedDatabaseDirectory, PostgresArchiveRecoveryOptions? archiveReplay = null);
    Task ExportSourceWalAsync(string sourceDirectory, string archive, IReadOnlyList<string> files);
    Task PauseSourceAsync();
    Task ResumeSourceAsync();
}

/// <summary>Opt-in native execution of the same physical recovery scenarios; never controls a Windows service.</summary>
internal sealed class NativePhysicalPostgresTools : IPhysicalPostgresTools
{
    public static bool Enabled => Environment.GetEnvironmentVariable("EXAM_TEST_NATIVE_PHYSICAL") == "1";
    private readonly string _sourceDirectory;
    private readonly string _binDirectory;
    private readonly string _connectionString;
    private readonly string _root;
    private readonly NpgsqlDataSource[] _sourcePools;
    private readonly IExternalProcessRunner _runner = new ExternalProcessRunner();
    private string? _restoredDirectory;
    private bool _sourceStopped;
    private string? _sourceSystemIdentifier;
    public bool IsNative => true;
    public string NativeBinDirectory => _binDirectory;
    public int BaseBackupCalls { get; private set; }
    public int VerifyCalls { get; private set; }
    public int RestoredPort { get; } = AllocatePort();

    public NativePhysicalPostgresTools(string connectionString, string root, params NpgsqlDataSource[] sourcePools)
    {
        RequireEnabled();
        _sourceDirectory = PhysicalPostgresTestPaths.RequireSourceDirectory(Required("EXAM_TEST_NATIVE_SOURCE_DIRECTORY"));
        var bin = Required("EXAM_TEST_NATIVE_POSTGRES_BIN");
        if (!Path.IsPathFullyQualified(bin)) throw new InvalidOperationException("PostgreSQL 工具目录必须为绝对路径。");
        _binDirectory = Path.GetFullPath(bin);
        if (new[] { "pg_ctl.exe", "pg_basebackup.exe", "pg_verifybackup.exe" }
            .Any(name => !File.Exists(Path.Combine(_binDirectory, name))))
            throw new InvalidOperationException("本机物理恢复测试需要明确的 PostgreSQL 工具目录。");
        _connectionString = connectionString;
        RequireSourceConnection(connectionString);
        _root = PhysicalPostgresTestPaths.RequireWorkingRoot(root);
        _sourcePools = sourcePools;
    }

    public static async Task ValidateConfiguredSourceAsync()
    {
        RequireEnabled();
        var directory = PhysicalPostgresTestPaths.RequireSourceDirectory(Required("EXAM_TEST_NATIVE_SOURCE_DIRECTORY"));
        await ValidateSourceAsync(Required("EXAM_TEST_POSTGRES"), directory);
    }

    private static void RequireEnabled()
    {
        if (!Enabled || !OperatingSystem.IsWindows())
            throw new InvalidOperationException("Windows 本机物理恢复测试必须显式设置 EXAM_TEST_NATIVE_PHYSICAL=1。");
    }

    private static string Required(string name) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
        ? Environment.GetEnvironmentVariable(name)! : throw new InvalidOperationException("缺少本机物理恢复测试配置：" + name);

    private static NpgsqlConnectionStringBuilder RequireSourceConnection(string connectionString)
    {
        var settings = new NpgsqlConnectionStringBuilder(connectionString);
        if (settings.Host != "127.0.0.1" || settings.Port is <= 1024 or 5432 || settings.Username != "postgres" ||
            settings.Database != "postgres" && settings.Database?.StartsWith("exam_test_", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("本机物理恢复源必须为专用回环非默认端口和合成测试数据库。");
        return settings;
    }

    private static async Task<string> ValidateSourceAsync(string connectionString, string directory)
    {
        PhysicalPostgresTestPaths.RequireSourceDirectory(directory);
        var settings = RequireSourceConnection(connectionString);
        settings.Pooling = false;
        settings.Timeout = 3;
        settings.CommandTimeout = 5;
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT current_setting('data_directory'), inet_server_port(), current_setting('listen_addresses'),
                   pg_is_in_recovery(), current_setting('server_version_num')::integer, system_identifier::text
              FROM pg_control_system()
            """, connection);
        await using var row = await command.ExecuteReaderAsync();
        if (!await row.ReadAsync() || !string.Equals(Path.GetFullPath(row.GetString(0)), directory, StringComparison.OrdinalIgnoreCase) ||
            row.GetInt32(1) != settings.Port || row.GetString(2) != "127.0.0.1" || row.GetBoolean(3) ||
            row.GetInt32(4) / 10000 != 18)
            throw new InvalidOperationException("实际 PostgreSQL 源实例与显式临时目录、回环端口或版本不一致，禁止操作。");
        return row.GetString(5);
    }

    private async Task VerifySourceAsync()
    {
        var actual = await ValidateSourceAsync(_connectionString, _sourceDirectory);
        if (_sourceSystemIdentifier is not null && _sourceSystemIdentifier != actual)
            throw new InvalidOperationException("专用测试源实例的集群标识发生变化。");
        _sourceSystemIdentifier = actual;
    }

    public async Task<ExternalProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken = default,
        string? workingDirectory = null, ExternalProcessLimits? limits = null)
    {
        var name = Path.GetFileName(executable);
        if (!string.Equals(Path.GetFullPath(executable), Path.Combine(_binDirectory, name), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只能运行显式 PostgreSQL 工具目录中的程序。");
        if (name == "pg_basebackup.exe")
        {
            await VerifySourceAsync();
            var destinationIndex = arguments.ToList().IndexOf("--pgdata");
            if (destinationIndex < 0 || destinationIndex + 1 >= arguments.Count)
                throw new InvalidOperationException("物理备份必须指定独立测试目标。");
            OwnPath(arguments[destinationIndex + 1]);
            var settings = RequireSourceConnection(_connectionString);
            if (environment.GetValueOrDefault("PGHOST") != settings.Host ||
                environment.GetValueOrDefault("PGPORT") != settings.Port.ToString(CultureInfo.InvariantCulture) ||
                environment.GetValueOrDefault("PGUSER") != settings.Username)
                throw new InvalidOperationException("物理备份工具不能连接其他实例。");
            BaseBackupCalls++;
        }
        else if (name == "pg_verifybackup.exe")
        {
            if (arguments.Count != 1) throw new InvalidOperationException("备份校验仅允许一个测试副本路径。");
            OwnPath(arguments[0]);
            VerifyCalls++;
        }
        else throw new InvalidOperationException("物理恢复适配仅运行固定 PostgreSQL 备份和校验工具。");
        return await _runner.RunAsync(Path.Combine(_binDirectory, name), arguments, environment, timeout,
            cancellationToken, workingDirectory, limits);
    }

    public async Task ExportSourceWalAsync(string sourceDirectory, string archive, IReadOnlyList<string> files)
    {
        await VerifySourceAsync();
        if (!string.Equals(Path.GetFullPath(sourceDirectory), _sourceDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("WAL 来源必须是已核实的专用源实例。");
        OwnPath(archive);
        foreach (var file in files)
        {
            if (file.Length != 24 || file.Any(c => !"0123456789ABCDEF".Contains(c)))
                throw new InvalidDataException("合成 WAL 文件名无效。");
            var source = Path.Combine(_sourceDirectory, "pg_wal", file);
            PhysicalPostgresTestPaths.RejectReparsePoints(source);
            var destination = OwnPath(Path.Combine(archive, file));
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await input.CopyToAsync(output);
            output.Flush(flushToDisk: true);
        }
    }

    public async Task StartRestoredAsync(string verifiedDatabaseDirectory, PostgresArchiveRecoveryOptions? archiveReplay = null)
    {
        var directory = OwnPath(verifiedDatabaseDirectory);
        if (_restoredDirectory is not null || !string.Equals(directory, Path.Combine(_root, "working", "slot", "database"),
            StringComparison.OrdinalIgnoreCase) || File.Exists(Path.Combine(directory, "postmaster.pid")))
            throw new InvalidOperationException("恢复实例只能在本测试准备的独立数据库槽中启动一次。");
        _restoredDirectory = directory; // Retain cleanup responsibility if pg_ctl starts but cannot report success.
        var replay = archiveReplay is null ? "" : ControlledRecoveryDatabase.BuildArchiveReplayOptions(archiveReplay);
        var options = $"-p {RestoredPort} -h 127.0.0.1 -c archive_mode=off -c shared_preload_libraries=\"\" " +
            "-c session_preload_libraries=\"\" -c local_preload_libraries=\"\"" + replay;
        await ControlAsync(["-D", directory, "-l", OwnPath(Path.Combine(_root, "restored-postgres.log")),
            "-o", options, "-w", "-t", "60", "start"]);
    }

    public async Task PauseSourceAsync()
    {
        if (_sourceStopped) return;
        await VerifySourceAsync();
        _sourceStopped = true;
        await ControlAsync(["-D", _sourceDirectory, "-m", "fast", "-w", "-t", "60", "stop"]);
    }

    public async Task ResumeSourceAsync()
    {
        if (!_sourceStopped) return;
        PhysicalPostgresTestPaths.RequireSourceDirectory(_sourceDirectory);
        if (File.Exists(Path.Combine(_sourceDirectory, "postmaster.pid")))
        {
            await VerifySourceAsync();
            ClearRestartedSourcePools();
            _sourceStopped = false;
            return;
        }
        var settings = RequireSourceConnection(_connectionString);
        await ControlAsync(["-D", _sourceDirectory, "-l", Path.Combine(Path.GetDirectoryName(_sourceDirectory)!, "native-source-resumed.log"),
            "-o", $"-p {settings.Port} -h 127.0.0.1", "-w", "-t", "60", "start"]);
        await VerifySourceAsync();
        ClearRestartedSourcePools();
        _sourceStopped = false;
    }

    private void ClearRestartedSourcePools()
    {
        // This test restarts the entire source cluster, unlike Docker pause/unpause.
        // Reconnect both fixtures before their next assertion; pooled sockets belong to the stopped server.
        foreach (var source in _sourcePools) source.Clear();
    }

    private async Task ControlAsync(IReadOnlyList<string> arguments)
    {
        // pg_ctl exits after readiness, while postgres remains alive. Redirecting the controller's
        // streams leaves inherited Windows pipe handles open in postgres and waits for EOF forever.
        // Give it private pipes so a resumed source cannot keep the test host's output open.
        // Quiet pg_ctl emits only bounded control errors; PostgreSQL itself writes to the explicit -l log.
        // Wait only for the controller, never for EOF on handles inherited by the long-lived server.
        var start = new ProcessStartInfo(Path.Combine(_binDirectory, "pg_ctl.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _binDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-s");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("无法启动专用 PostgreSQL 控制进程。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // Stop only this controller; disposal still owns the verified database directory.
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) when (process.HasExited) { }
            throw new TimeoutException("专用 PostgreSQL 控制进程没有在期限内退出。" + ReadControlLog(arguments));
        }
        if (process.ExitCode != 0)
            throw new IOException($"专用本机 PostgreSQL 启停失败（退出码 {process.ExitCode}）。" + ReadControlLog(arguments));
    }

    private string ReadControlLog(IReadOnlyList<string> arguments)
    {
        var index = arguments.ToList().IndexOf("-l");
        if (index < 0 || index + 1 >= arguments.Count) return "";
        var path = Path.GetFullPath(arguments[index + 1]);
        var sourceLog = Path.Combine(Path.GetDirectoryName(_sourceDirectory)!, "native-source-resumed.log");
        if (!string.Equals(path, sourceLog, StringComparison.OrdinalIgnoreCase)) OwnPath(path);
        PhysicalPostgresTestPaths.RejectReparsePoints(path);
        if (!File.Exists(path)) return "";
        // Only synthetic-instance logs, bounded to the latest startup diagnostic lines.
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (input.Length > 8192) input.Seek(-8192, SeekOrigin.End);
        using var reader = new StreamReader(input);
        return Environment.NewLine + reader.ReadToEnd();
    }

    public async ValueTask DisposeAsync()
    {
        try { await ResumeSourceAsync(); }
        finally
        {
            if (_restoredDirectory is not null && File.Exists(Path.Combine(OwnPath(_restoredDirectory), "postmaster.pid")))
                await ControlAsync(["-D", _restoredDirectory, "-m", "fast", "-w", "-t", "60", "stop"]);
        }
    }

    private string OwnPath(string path) => PhysicalPostgresTestPaths.RequireChild(_root, path);
    private static int AllocatePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }
}

internal static class PhysicalPostgresTestPaths
{
    public static string RequireSourceDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("专用源目录必须使用绝对路径。");
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        if (parent is null || Path.GetFileName(full) != "postgres-source" ||
            !SamePath(Path.GetDirectoryName(parent), Path.GetTempPath()) ||
            !Regex.IsMatch(Path.GetFileName(parent), @"^(codex-f01-native-[a-zA-Z0-9-]+|codex-pr[0-9]+(?:-[0-9]+)*-review-[0-9]{8})$"))
            throw new InvalidOperationException("源目录只能位于系统临时目录下专用 codex-f01-native 或 Codex PR 演练目录。");
        RejectReparsePoints(full);
        return full;
    }

    public static string RequireWorkingRoot(string path, bool rejectReparsePoints = true)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("副本目录必须使用绝对路径。");
        var full = Path.GetFullPath(path);
        if (!SamePath(Path.GetDirectoryName(full), Path.GetTempPath()) ||
            !Regex.IsMatch(Path.GetFileName(full), @"^f01-physical-[a-f0-9]{32}$"))
            throw new InvalidOperationException("只能操作本测试的独立临时副本。");
        if (rejectReparsePoints) RejectReparsePoints(full);
        else if (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("清理目标不能是目录联接或符号链接。");
        return full;
    }

    public static string RequireChild(string root, string path)
    {
        RequireWorkingRoot(root);
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, full);
        if (relative is "." or ".." || Path.IsPathFullyQualified(relative) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("副本操作超出本测试目录。");
        RejectReparsePoints(full);
        return full;
    }

    public static void RejectReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("物理恢复测试路径不能经过符号链接或目录联接。");
    }

    private static bool SamePath(string? left, string right) => left is not null && string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
