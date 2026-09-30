using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;

namespace Exam.Infrastructure.ServiceManagement;

public sealed record RecoveryDeploymentSelection(string ServerId, string NodeId, string RequestId,
    string ActorId, string BackupId, string WorkingDirectory, long ServerEpoch, string ConnectionReference)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool ArchiveRecovery { get; init; }
}

/// <summary>Protected non-secret reference to one of two pre-deployed connections. Corruption never falls back.</summary>
public sealed class FileRecoveryDeploymentSelection
{
    private readonly string _path;
    private readonly string _directory;
    public FileRecoveryDeploymentSelection(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("部署选择路径必须为绝对路径。");
        _path = Path.GetFullPath(path);
        _directory = Path.GetDirectoryName(_path)!;
        Check();
    }

    private void Check()
    {
        if (!Directory.Exists(_directory)) throw new DirectoryNotFoundException("受保护部署目录未创建。");
        PathBoundary.RejectExistingReparsePoints(_directory, _path);
    }

    public RecoveryDeploymentSelection? Read()
    {
        Check();
        FileStream input;
        try { input = new(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
        catch (FileNotFoundException) { return null; }
        using (input)
        {
            if (input.Length is <= 0 or > 16384) throw new InvalidDataException("部署选择记录大小无效。");
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            var result = JsonSerializer.Deserialize<RecoveryDeploymentSelection>(bytes, JsonDefaults.Create())
                ?? throw new InvalidDataException("部署选择记录无效。");
            Validate(result);
            if (!bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(result, JsonDefaults.Create())))
                throw new InvalidDataException("部署选择记录不是完整规范记录。");
            return result;
        }
    }

    public async Task SelectAsync(RecoveryDeploymentSelection selection, CancellationToken token)
    {
        Validate(selection);
        if (selection.ConnectionReference != "ExamRecovery") throw new ArgumentException("恢复选择必须指向预部署恢复连接。");
        await using var guard = Lock();
        var previous = Read();
        if (previous is { ConnectionReference: "ExamRecovery" })
        {
            if (previous != selection) throw new InvalidOperationException("另一恢复操作已经选择部署目标。");
            return;
        }
        if (previous is not null && (previous.ServerId != selection.ServerId || previous.NodeId != selection.NodeId))
            throw new InvalidDataException("原部署选择身份不匹配。");
        await WriteAsync(selection, token);
    }

    public async Task RollbackAsync(RecoveryDeploymentSelection expected, CancellationToken token)
    {
        Validate(expected);
        await using var guard = Lock();
        var current = Read();
        if (current is null) return;
        if (current == expected with { ConnectionReference = "Exam" }) return;
        if (current != expected) throw new InvalidOperationException("部署选择已变化，不能覆盖回退。");
        await WriteAsync(expected with { ConnectionReference = "Exam" }, token);
    }

    private FileStream Lock()
    {
        Check();
        var path = _path + ".lock";
        PathBoundary.RejectExistingReparsePoints(_directory, path);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private async Task WriteAsync(RecoveryDeploymentSelection value, CancellationToken token)
    {
        var temporary = Path.Combine(_directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, value, JsonDefaults.Create(), token);
                await output.FlushAsync(token);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            Check();
            File.Move(temporary, _path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(RecoveryDeploymentSelection value)
    {
        if (string.IsNullOrWhiteSpace(value.ServerId) || string.IsNullOrWhiteSpace(value.NodeId) ||
            string.IsNullOrWhiteSpace(value.RequestId) || string.IsNullOrWhiteSpace(value.ActorId) ||
            string.IsNullOrWhiteSpace(value.BackupId) || !Path.IsPathFullyQualified(value.WorkingDirectory) ||
            value.ServerEpoch < 1 || value.ConnectionReference is not ("Exam" or "ExamRecovery"))
            throw new InvalidDataException("部署选择缺少有效身份、目标或代次。");
    }

    public static RecoveryDeploymentSelection? ReadConfigured(Func<string, string?> get, string serverId, string nodeId, long epoch)
    {
        var path = get("Recovery:SelectionPath");
        if (string.IsNullOrWhiteSpace(path)) return null;
        var selection = new FileRecoveryDeploymentSelection(path).Read();
        if (selection is not null && (selection.ServerId != serverId || selection.NodeId != nodeId || selection.ServerEpoch != epoch))
            throw new InvalidDataException("部署选择与本机服务器身份或预期代次不匹配。");
        return selection;
    }

    public static string? ResolveConnection(Func<string, string?> get, string serverId, string nodeId, long epoch)
    {
        var selection = ReadConfigured(get, serverId, nodeId, epoch);
        var reference = selection?.ConnectionReference ?? "Exam";
        var result = get("ConnectionStrings:" + reference);
        if (reference == "ExamRecovery" && string.IsNullOrWhiteSpace(result))
            throw new InvalidOperationException("被选择的受保护恢复连接未配置，拒绝回退原库。");
        return result;
    }
}

public sealed class FileRecoveryDeploymentSwitch(FileRecoveryDeploymentSelection store, long epoch,
    Func<ManagementOperation, CancellationToken, Task<bool>>? allowRollback = null, bool archiveRecovery = false)
    : Exam.Application.ServiceManagement.IRecoveryDeploymentSwitch
{
    private RecoveryDeploymentSelection Bind(ManagementOperation operation)
    {
        if (operation.Kind != ManagementOperationKind.Restore || operation.Restore is not
            { IsolationStaged: true, RecoveryVerified: true, BusinessReady: false } ||
            operation.Checks?.Any(check => check.Name == "recovery-target" && check.Passed && check.BlocksWrites) != true ||
            operation.Checks?.Any(check => check.Name == "recovery-confirmed-state" && check.Passed && check.BlocksWrites) != true ||
            operation.Checks?.All(check => check.Passed || !check.BlocksWrites) != true ||
            operation.RecoveryWorkingDirectory is null)
            throw new InvalidOperationException("恢复操作尚未核对，不能选择部署。");
        return new RecoveryDeploymentSelection(operation.ServerId, operation.NodeId, operation.RequestId, operation.ActorId,
            operation.Restore.BackupId, operation.RecoveryWorkingDirectory, epoch, "ExamRecovery") { ArchiveRecovery = archiveRecovery };
    }
    public Task SelectAsync(ManagementOperation operation, CancellationToken token) => store.SelectAsync(Bind(operation), token);
    public async Task RollbackAsync(ManagementOperation operation, CancellationToken token)
    {
        var bound = Bind(operation);
        // Readiness workers or a client may have written before a startup timeout/cancellation was observed.
        // Preserve the selected database unless the stopped target still matches the original confirmed state.
        if (store.Read() == bound && allowRollback is not null && !await allowRollback(operation, token))
            throw new InvalidOperationException("恢复库可能已有业务变更，不能自动回退部署引用。");
        await store.RollbackAsync(bound, token);
    }
    public bool IsSelected(ManagementOperation operation) => store.Read() == Bind(operation);
    public bool IsRolledBack(ManagementOperation operation) => store.Read() == Bind(operation) with { ConnectionReference = "Exam" };
}
