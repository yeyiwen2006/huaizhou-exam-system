using System.Text.Json;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Management;

public static class AutomaticBackupStatusFile
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<BackupMaintenanceStatus?> ReadAsync(bool enabled, string? directory, CancellationToken token)
    {
        var now = DateTimeOffset.UtcNow;
        if (!enabled) return new() { Enabled = false, ObservedAt = now,
            BackupPhase = BackupMaintenancePhase.Disabled, CleanupPhase = BackupMaintenancePhase.Disabled,
            Message = "此节点未启用自动备份维护。" };
        BackupMaintenanceStatus Unknown(string warning) => new() { Enabled = true, ObservedAt = now,
            BackupPhase = BackupMaintenancePhase.Deferred, CleanupPhase = BackupMaintenancePhase.Deferred, Warnings = [warning] };
        try
        {
            if (string.IsNullOrWhiteSpace(directory)) return Unknown("自动备份已启用，但维护状态目录未配置。");
            var path = Path.Combine(Path.GetFullPath(directory), "status.json");
            BackupRetentionPolicy.RejectLinks(path);
            if (!File.Exists(path)) return Unknown("自动备份尚未产生维护状态；请核对管理宿主是否正在运行。");
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (input.Length > 65536) return Unknown("自动备份维护状态无效，无法核实磁盘与备份情况。");
            using var document = await JsonDocument.ParseAsync(input, cancellationToken: token);
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1)
                return Unknown("维护状态来自旧版本，等待修复后的管理宿主完成一轮检查。");
            var status = document.RootElement.Deserialize<BackupMaintenanceStatus>(Json);
            if (status is null || !Enum.IsDefined(status.BackupPhase) || !Enum.IsDefined(status.CleanupPhase) ||
                status.Warnings is null || status.Warnings.Count > 16 || status.Warnings.Any(w => w is null || w.Length > 1000) ||
                status.Message is null || status.Message.Length > 1000)
                return Unknown("自动备份维护状态无效，无法核实磁盘与备份情况。");
            if (now - status.ObservedAt > TimeSpan.FromMinutes(15) || status.ObservedAt > now.AddMinutes(5))
                return status with { Warnings = status.Warnings.Append("维护状态超过 15 分钟未更新或时间无效，请核对管理宿主；上次磁盘数值不代表当前空间。").Take(16).ToArray() };
            return status;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return Unknown("无法读取自动备份维护状态；备份与磁盘情况尚未核实。"); }
    }

    internal static async Task SaveAsync<T>(string path, T value, CancellationToken token)
    {
        BackupRetentionPolicy.RejectLinks(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(output, value, Json, token);
                await output.FlushAsync(token);
                output.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
