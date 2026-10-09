using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Exam.Application.ServiceManagement;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;
using Npgsql;

internal sealed class LocalServiceContext(string configuration, string postgresBin) : IServiceConfigurationStore
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string ConfigurationPath { get; } = Path.GetFullPath(configuration);
    public string Root { get; } = Path.GetDirectoryName(Path.GetFullPath(configuration))!;
    public string PostgresBin { get; } = Path.GetFullPath(postgresBin);
    public LocalEntry.LocalSettings Settings => LocalEntry.ReadSettings(ConfigurationPath).Settings;
    public string ManagementDirectory => Path.Combine(Root, "management");
    public string SelectionPath => Path.Combine(ManagementDirectory, "active-database.json");
    private FileServiceConfigurationStore Store => new(Path.Combine(ManagementDirectory, "service.json"));
    public string Connection(string? database = null) =>
        new NpgsqlConnectionStringBuilder(LocalEntry.ConnectionString(Settings, database)) { Pooling = false }.ConnectionString;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(ManagementDirectory);
        Directory.CreateDirectory(Path.Combine(ManagementDirectory, "operations"));
        Directory.CreateDirectory(Path.Combine(Root, "backups"));
        Directory.CreateDirectory(Path.Combine(Root, "restores"));
        var data = Path.Combine(Root, "business-data");
        Directory.CreateDirectory(data);
        if (!File.Exists(Path.Combine(ManagementDirectory, "service.json")))
            await Store.SaveAsync(new(Settings.ServerId, "local-node-a", "127.0.0.1", Settings.HttpsPort, data, false, 0), 0);
    }

    public Task<ServiceConfiguration> LoadAsync(CancellationToken cancellationToken = default) => Store.LoadAsync(cancellationToken);

    public Task<ServiceConfiguration> SaveAsync(ServiceConfiguration requested, long expectedVersion, CancellationToken cancellationToken = default)
    {
        if (requested.BindAddress != "127.0.0.1" || requested.HighAvailabilityEnabled ||
            requested.HttpsPort is < 1024 or > 65535 ||
            new[] { Settings.DatabasePort, Settings.AdminPort, Settings.ManagementPort }.Contains(requested.HttpsPort))
            throw new ArgumentException("本机测试使用 127.0.0.1；业务端口须独立，主备测试须使用正式部署环境。");
        var data = Inside(Root, requested.DataDirectory);
        Directory.CreateDirectory(data);
        return Store.SaveAsync(requested with { DataDirectory = data }, expectedVersion, cancellationToken);
    }

    internal static string Inside(string root, string path)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请填写绝对目录。");
        path = Path.GetFullPath(path);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择当前测试数据目录内的子目录。");
        for (var part = path; part.Length >= root.Length; part = Path.GetDirectoryName(part)!)
            if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("测试管理目录不能包含链接或联接点。");
        return path;
    }

    internal static async Task WriteAsync<T>(string path, T value, CancellationToken token = default)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, Json), token);
        File.Move(temporary, path, true);
    }
}

internal sealed class LocalStartupException(int exitCode) : IOException("测试业务后台启动失败，请查看 management.log。")
{
    public int ExitCode { get; } = exitCode;
}

internal sealed class LocalBusinessControl(LocalServiceContext context) : IManagedBusinessHost, IStartupReadinessProbe, IAsyncDisposable
{
    private readonly List<Process> _children = [];
    public BusinessServiceStatus Status => _children.Count == 0 ? BusinessServiceStatus.Stopped :
        _children.Count == 2 && _children.All(p => !p.HasExited) ? BusinessServiceStatus.Running : BusinessServiceStatus.Faulted;

    public async Task StartAsync(CancellationToken token)
    {
        if (Status != BusinessServiceStatus.Stopped) throw new InvalidOperationException("请先停止现有测试业务进程。");
        var service = await context.LoadAsync(token);
        await LocalEntry.RunAsync(["--prepare", context.ConfigurationPath]);
        var studentDirectory = Path.Combine(context.Root, "apps", "student");
        if (Directory.Exists(studentDirectory)) File.Copy(Path.Combine(context.Root, "student-server.json"), Path.Combine(studentDirectory, "server.json"), true);
        try
        {
            foreach (var node in new[] { "a", "b" })
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = service.DataDirectory,
                    RedirectStandardOutput = true, RedirectStandardError = true };
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(typeof(LocalEntry).Assembly.Location);
                foreach (var value in new[] { "--config", context.ConfigurationPath, "--node", node }) start.ArgumentList.Add(value);
                start.Environment["TEMP"] = service.DataDirectory;
                start.Environment["TMP"] = service.DataDirectory;
                var process = new Process { StartInfo = start };
                process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine($"[{node}] {e.Data}"); };
                process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine($"[{node}] {e.Data}"); };
                process.Start();
                _children.Add(process);
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                var index = node == "a" ? 0 : 1;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(45));
                while (!await ReadyNodeAsync(index, deadline.Token))
                {
                    if (process.HasExited) throw new LocalStartupException(process.ExitCode);
                    await Task.Delay(200, deadline.Token);
                }
            }
        }
        catch { await StopAsync(CancellationToken.None); throw; }
    }

    public async Task StopAsync(CancellationToken token)
    {
        foreach (var process in _children.AsEnumerable().Reverse())
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(token); }
            process.Dispose();
        }
        _children.Clear();
    }

    public async Task<bool> ReadyAsync(CancellationToken token) => Status == BusinessServiceStatus.Running &&
        await ReadyNodeAsync(0, token) && await ReadyNodeAsync(1, token);

    private async Task<bool> ReadyNodeAsync(int index, CancellationToken token)
    {
        var settings = context.Settings;
        using var expected = LocalEntry.LoadCertificate(context.Root, settings, index);
        using var client = new HttpClient(new HttpClientHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) => certificate is not null &&
                (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None &&
                certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow &&
                certificate.RawData.AsSpan().SequenceEqual(expected.RawData)
        }) { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await client.GetAsync($"https://127.0.0.{index + 1}:{settings.HttpsPort}/health/ready", token);
            if (!response.IsSuccessStatusCode) return false;
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            return body.RootElement.GetProperty("status").GetString() == "ready" &&
                body.RootElement.GetProperty("serverId").GetString() == settings.ServerId &&
                body.RootElement.GetProperty("nodeId").GetString() == $"local-node-{(index == 0 ? "a" : "b")}";
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    public async Task<IReadOnlyList<StartupCheckResult>> RunAsync(ServiceConfiguration configuration, CancellationToken token)
    {
        var checks = new List<StartupCheckResult>();
        try
        {
            await using var connection = new NpgsqlConnection(context.Connection());
            await connection.OpenAsync(token);
            await using var command = new NpgsqlCommand("SELECT count(*) FROM exam.schema_migrations", connection);
            var ready = Convert.ToInt64(await command.ExecuteScalarAsync(token)) > 0;
            checks.Add(new("database", ready, ready ? "测试数据库可读。" : "数据库尚未完成初始化。", true));
        }
        catch (Exception error) when (error is NpgsqlException or IOException)
        { checks.Add(new("database", false, "测试数据库不可用。", true)); }
        using var certificate = LocalEntry.LoadCertificate(context.Root, context.Settings, 0);
        var valid = certificate.NotBefore.ToUniversalTime() <= DateTime.UtcNow && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow;
        checks.Add(new("certificate", valid, valid ? "本机测试证书有效。" : "测试证书已过期。", true));
        checks.Add(new("storage", Directory.Exists(configuration.DataDirectory), "检查本次测试业务目录。", true));
        checks.Add(new("backup-tools", new[] { "pg_dump.exe", "pg_restore.exe" }.All(name => File.Exists(Path.Combine(context.PostgresBin, name))),
            "检查 PostgreSQL 备份与恢复工具。", true));
        return checks;
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);
}
