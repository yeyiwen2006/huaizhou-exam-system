using System.Diagnostics;
using System.Text.Json;
using Exam.Application.ServiceManagement;
using Exam.Contracts.ServiceManagement;
using Npgsql;

// Local test snapshots use pg_dump. All versioned papers, encrypted packages and source
// archives are stored in PostgreSQL; node keys and configuration are copied alongside it.
internal sealed class LocalBackupCoordinator(LocalServiceContext context) : IBackupCoordinator
{
    private sealed record Snapshot(string ServerId, string RequestId, BackupResult Result, ServiceConfiguration Configuration);
    private sealed record WorkingCopy(string DatabaseName, string PreviousDatabaseName, string SourceDirectory, string BackupId);
    private static string RequestKey(string id) => Guid.TryParse(id, out var key) ? key.ToString("N") :
        throw new ArgumentException("本机测试管理请求标识必须为 GUID。");
    private string WorkingDirectory(string id) => Path.Combine(context.Root, "restores", RequestKey(id));
    private string WorkingPath(string id) => Path.Combine(WorkingDirectory(id), "working.json");
    private static readonly string[] ProtectedFiles = ["local-settings.json", "local-node-a.pfx", "local-node-b.pfx"];

    public async Task<BackupResult> CreateAsync(BackupRequest request, CancellationToken token)
    {
        var parent = LocalServiceContext.Inside(context.Root, request.DestinationDirectory);
        var destination = LocalServiceContext.Inside(parent, Path.Combine(parent, "backup-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(destination);
        await RunToolAsync("pg_dump", ["--format=custom", "--no-owner", "--no-privileges", "--file", Path.Combine(destination, "database.dump")],
            context.Settings.DatabaseName, token);
        foreach (var name in ProtectedFiles) File.Copy(Path.Combine(context.Root, name), Path.Combine(destination, name));
        var result = new BackupResult(Path.GetFileName(destination), destination, DateTimeOffset.UtcNow, true, true, true);
        await LocalServiceContext.WriteAsync(Path.Combine(destination, "snapshot.json"),
            new Snapshot(context.Settings.ServerId, request.RequestId, result, await context.LoadAsync(token)), token);
        return result;
    }

    public async Task RestoreAsync(RestoreRequest request, CancellationToken token)
    {
        var source = LocalServiceContext.Inside(context.Root, request.SourceDirectory);
        var snapshot = await ReadAsync<Snapshot>(Path.Combine(source, "snapshot.json"), token);
        if (snapshot.ServerId != context.Settings.ServerId || snapshot.Result.BackupId != request.BackupId ||
            !snapshot.Result.IncludesDatabase || !snapshot.Result.IncludesProtectedConfiguration || !snapshot.Result.IncludesVersionedResources)
            throw new ArgumentException("所选备份不属于本次测试环境，或备份未完成。");
        var staged = WorkingDirectory(request.RequestId);
        LocalServiceContext.Inside(context.Root, staged);
        Directory.CreateDirectory(staged);
        if (File.Exists(Path.Combine(staged, "snapshot.json")))
        {
            var existing = await ReadAsync<Snapshot>(Path.Combine(staged, "snapshot.json"), token);
            if (existing != snapshot) throw new IOException("原恢复请求的备份已改变。");
            return;
        }
        foreach (var name in ProtectedFiles.Append("database.dump"))
        {
            var input = LocalServiceContext.Inside(source, Path.Combine(source, name));
            File.Copy(input, Path.Combine(staged, name), true);
        }
        // Both recipients' existing keys must still open restored packages. No system trust
        // store is modified and no keys from another test environment are installed.
        var old = JsonSerializer.Deserialize<LocalEntry.LocalSettings>(await File.ReadAllTextAsync(Path.Combine(staged, "local-settings.json"), token), LocalServiceContext.Json)!;
        for (var index = 0; index < 2; index++)
        {
            using var saved = LocalEntry.LoadCertificate(staged, old, index);
            using var current = LocalEntry.LoadCertificate(context.Root, context.Settings, index);
            if (!saved.RawData.AsSpan().SequenceEqual(current.RawData)) throw new IOException("备份的试卷解密证书与当前测试环境不同。");
        }
        await LocalServiceContext.WriteAsync(Path.Combine(staged, "snapshot.json"), snapshot, token);
    }

    public async Task PrepareAsync(RestoreRequest request, CancellationToken token)
    {
        if (File.Exists(WorkingPath(request.RequestId))) return;
        var name = "exam_local_restore_" + Guid.NewGuid().ToString("N");
        await using (var database = new NpgsqlConnection(context.Connection("postgres")))
        {
            await database.OpenAsync(token);
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name} TEMPLATE template0 ENCODING 'UTF8'", database);
            await create.ExecuteNonQueryAsync(token);
        }
        await RunToolAsync("pg_restore", ["--exit-on-error", "--single-transaction", "--no-owner", "--no-privileges",
            "--dbname", name, Path.Combine(WorkingDirectory(request.RequestId), "database.dump")], name, token);
        await LocalServiceContext.WriteAsync(WorkingPath(request.RequestId), new WorkingCopy(name,
            context.Settings.DatabaseName, request.SourceDirectory, request.BackupId), token);
    }

    public async Task<IReadOnlyList<StartupCheckResult>> VerifyAsync(RestoreRequest request, ServiceConfiguration configuration, CancellationToken token)
    {
        var working = await ReadAsync<WorkingCopy>(WorkingPath(request.RequestId), token);
        await using var database = new NpgsqlConnection(context.Connection(working.DatabaseName));
        await database.OpenAsync(token);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM exam.schema_migrations", database);
        var initialized = Convert.ToInt64(await command.ExecuteScalarAsync(token)) > 0;
        return [new("recovery-target", initialized, "所选备份已导入独立测试数据库，检查其业务结构。", true),
            new("local-snapshot", working.BackupId == request.BackupId,
                "本机测试恢复到所选备份的状态；恢复前的数据库保留，未覆盖或删除。", true)];
    }

    public async Task<ManagementOperation> ActivateAsync(ManagementOperation operation, LocalBusinessControl business,
        Exam.Application.ServiceManagement.IManagementOperationStore journal, CancellationToken token)
    {
        var working = await ReadAsync<WorkingCopy>(WorkingPath(operation.RequestId), token);
        var original = context.Settings.DatabaseName;
        var snapshot = await ReadAsync<Snapshot>(Path.Combine(WorkingDirectory(operation.RequestId), "snapshot.json"), token);
        var previousConfiguration = await context.LoadAsync(token);
        var next = operation with { RecoveryActivation = RecoveryActivationPhase.SelectionPending };
        await journal.SaveAsync(next, token);
        try
        {
            await LocalServiceContext.WriteAsync(context.SelectionPath, new { working.DatabaseName, operation.RequestId }, token);
            await context.SaveAsync(snapshot.Configuration, previousConfiguration.Version, token);
            await journal.SaveAsync(next with { RecoveryActivation = RecoveryActivationPhase.StartingBusiness }, token);
            await business.StartAsync(token);
            var activeConfiguration = await context.LoadAsync(token);
            return next with { Phase = ManagementOperationPhase.Completed, RecoveryActivation = RecoveryActivationPhase.BusinessReady,
                Restore = new(working.BackupId, true, true, true),
                Configuration = activeConfiguration,
                Status = new(business.Status, BusinessReadiness.Ready, activeConfiguration, operation.Checks ?? []) };
        }
        catch
        {
            await business.StopAsync(CancellationToken.None);
            await LocalServiceContext.WriteAsync(context.SelectionPath, new { DatabaseName = original }, CancellationToken.None);
            var current = await context.LoadAsync(CancellationToken.None);
            await context.SaveAsync(previousConfiguration, current.Version, CancellationToken.None);
            await journal.SaveAsync(operation with { Phase = ManagementOperationPhase.Staged,
                RecoveryActivation = RecoveryActivationPhase.RolledBack }, CancellationToken.None);
            throw;
        }
    }

    public async Task<ManagementOperation?> ObserveAsync(ManagementOperation operation, LocalBusinessControl business, CancellationToken token)
    {
        if (!File.Exists(WorkingPath(operation.RequestId))) return null;
        var working = await ReadAsync<WorkingCopy>(WorkingPath(operation.RequestId), token);
        if (operation.RecoveryActivation == RecoveryActivationPhase.RolledBack &&
            business.Status == BusinessServiceStatus.Stopped && context.Settings.DatabaseName == working.PreviousDatabaseName)
            return operation with { Phase = ManagementOperationPhase.Staged, Error = null };
        if (context.Settings.DatabaseName != working.DatabaseName || !await business.ReadyAsync(token)) return null;
        return operation with { Phase = ManagementOperationPhase.Completed, RecoveryActivation = RecoveryActivationPhase.BusinessReady,
            Error = null, Restore = new(working.BackupId, true, true, true) };
    }

    private async Task RunToolAsync(string tool, string[] arguments, string database, CancellationToken token)
    {
        var settings = context.Settings;
        var start = new ProcessStartInfo(Path.Combine(context.PostgresBin, tool + ".exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.Environment["PGHOST"] = "127.0.0.1"; start.Environment["PGPORT"] = settings.DatabasePort.ToString();
        start.Environment["PGUSER"] = "postgres"; start.Environment["PGPASSWORD"] = settings.DatabasePassword;
        start.Environment["PGDATABASE"] = database; start.Environment["PGCLIENTENCODING"] = "UTF8";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 PostgreSQL 工具。");
        var errors = process.StandardError.ReadToEndAsync(token);
        var output = process.StandardOutput.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await output;
        var errorText = await errors;
        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine(errorText);
            throw new IOException("测试数据库备份或恢复失败，请查看 management.log；原数据库已保留。");
        }
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken token) =>
        JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, token), LocalServiceContext.Json)
        ?? throw new InvalidDataException("测试备份记录无效。");
}
