using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>Local journal in a pre-provisioned directory protected by the deployment account.</summary>
public sealed class FileManagementOperationStore : IManagementOperationStore
{
    private const int MaximumBytes = 1024 * 1024;
    private readonly string _directory;
    private readonly JsonSerializerOptions _json = JsonDefaults.Create();

    public FileManagementOperationStore(string directory)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("操作记录目录必须是绝对路径。");
        _directory = Path.GetFullPath(directory);
        _json.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        _json.NumberHandling = JsonNumberHandling.Strict;
        Check(_directory);
    }

    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, ".management.lock");
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(5))
            { await Task.Delay(25, cancellationToken); }
        }
    }

    public async Task<ManagementOperation?> FindAsync(string requestId, CancellationToken cancellationToken)
    {
        var path = Resolve(requestId);
        Check(path);
        FileStream input;
        try { input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
        catch (FileNotFoundException) { return null; }
        await using (input)
        {
            if (input.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("操作记录大小无效。");
            var payload = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(payload, cancellationToken);
            var operation = JsonSerializer.Deserialize<ManagementOperation>(payload, _json)
                ?? throw new InvalidDataException("操作记录不完整。");
            Validate(operation);
            // Canonical byte equality detects missing, duplicate, unknown fields and invalid enum spellings.
            if (operation.RequestId != requestId || !payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(operation, _json)))
                throw new InvalidDataException("操作记录内容或标识不一致，须人工核对。");
            return operation;
        }
    }

    public async Task SaveAsync(ManagementOperation operation, CancellationToken cancellationToken)
    {
        Validate(operation);
        var path = Resolve(operation.RequestId);
        Check(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(operation, _json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("操作结果超过记录容量限制。");
        var temporary = Path.Combine(_directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await output.WriteAsync(bytes, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Check(path);
            var replacementWait = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    break;
                }
                catch (Exception error) when (
                    (error is IOException or UnauthorizedAccessException) &&
                    ((error.HResult & 0xFFFF) is 5 or 32 or 33) && replacementWait.Elapsed < TimeSpan.FromSeconds(5))
                {
                    // Windows can refuse an overwrite while a snapshot reader still holds the target,
                    // even with FileShare.Delete. Retry only the prepared atomic replacement.
                    await Task.Delay(20, cancellationToken);
                }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public async Task<bool> HasUnverifiedOperationsAsync(CancellationToken cancellationToken)
    {
        Check(_directory);
        foreach (var path in OperationFiles())
        {
            Check(path);
            // FindAsync also verifies the canonical filename and the complete persisted record.
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("操作记录大小无效。");
            var bytes = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(bytes, cancellationToken);
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("requestId", out var requestId) || requestId.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("操作记录缺少请求标识。");
            var id = requestId.GetString() ?? throw new InvalidDataException("操作记录缺少请求标识。");
            if (Resolve(id) != path) throw new InvalidDataException("操作记录文件名与请求标识不符。");
            var record = await FindAsync(id, cancellationToken) ?? throw new InvalidDataException("操作记录消失。");
            // Self-checks never mutate service/configuration/recovery state. An interrupted diagnostic
            // (including a lost final journal write) must not prevent a later repaired startup.
            if (record.Kind != ManagementOperationKind.Checks &&
                record.Phase is ManagementOperationPhase.Accepted or ManagementOperationPhase.NeedsVerification or ManagementOperationPhase.Staged)
                return true;
        }
        return false;
    }

    private sealed record CancellationIntent(string RequestId, string ActorId);
    public Task RequestCancellationAsync(string requestId, string actorId, CancellationToken token) =>
        DurableJsonFile.WriteAsync(Resolve(requestId) + ".cancel", new CancellationIntent(requestId, actorId), token);

    public Task<bool> IsCancellationRequestedAsync(string requestId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var intent = DurableJsonFile.Read<CancellationIntent>(Resolve(requestId) + ".cancel");
        if (intent is not null && intent.RequestId != requestId) throw new InvalidDataException("取消请求与原操作不一致。");
        return Task.FromResult(intent is not null);
    }

    public async Task<bool> HasLaterBusinessStopAsync(ManagementOperation operation, CancellationToken token)
    {
        foreach (var path in OperationFiles())
        {
            Check(path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("操作记录大小无效。");
            var bytes = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(bytes, token);
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("操作记录缺少请求标识。");
            var requestId = id.GetString() ?? throw new InvalidDataException("操作记录缺少请求标识。");
            if (Resolve(requestId) != path) throw new InvalidDataException("操作记录文件名与请求标识不符。");
            var later = await FindAsync(requestId, token) ?? throw new InvalidDataException("操作记录消失。");
            if (later.Kind == ManagementOperationKind.Stop && later.RequestId != operation.RequestId &&
                later.ServerId == operation.ServerId && later.NodeId == operation.NodeId && later.AcceptedAt >= operation.AcceptedAt)
                return true;
        }
        return false;
    }

    public async Task<IReadOnlyList<ManagementAuditEntry>> ReadAuditAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        Check(_directory);
        var entries = new List<ManagementAuditEntry>();
        var scanned = 0;
        foreach (var path in OperationFiles())
        {
            if (++scanned > 10000) throw new InvalidDataException("操作记录过多，须通过受保护维护流程归档后查询。");
            Check(path);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (input.Length is <= 0 or > MaximumBytes) throw new InvalidDataException("操作记录大小无效。");
            var bytes = new byte[checked((int)input.Length)];
            await input.ReadExactlyAsync(bytes, cancellationToken);
            using var document = JsonDocument.Parse(bytes);
            if (!document.RootElement.TryGetProperty("requestId", out var id) || id.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("操作记录缺少请求标识。");
            var requestId = id.GetString() ?? throw new InvalidDataException("操作记录缺少请求标识。");
            if (Resolve(requestId) != path) throw new InvalidDataException("操作记录文件名与请求标识不符。");
            var operation = await FindAsync(requestId, cancellationToken) ?? throw new InvalidDataException("操作记录消失。");
            entries.Add(new(operation.RequestId, operation.Kind, operation.Phase,
                operation.AcceptedAt, operation.UpdatedAt, operation.Error?.Code, operation.FailoverContinuationPending));
        }
        return entries.OrderByDescending(entry => entry.UpdatedAt).ThenBy(entry => entry.RequestId, StringComparer.Ordinal)
            .Take(limit).ToArray();
    }

    private IEnumerable<string> OperationFiles()
    {
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            if (Path.GetFileName(path).Equals("node-selection.json", StringComparison.OrdinalIgnoreCase))
            {
                // Only the known, valid legacy selection is not owned by this journal.
                _ = new HighAvailability.FileFailoverDeploymentSelection(path).Read();
                continue;
            }
            yield return path;
        }
    }

    private string Resolve(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 200)
            throw new ArgumentException("requestId 必须有效且不超过 200 字符。");
        return Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestId))) + ".json");
    }

    private void Check(string path)
    {
        if (!Directory.Exists(_directory)) throw new DirectoryNotFoundException("受保护操作记录目录不存在。");
        PathBoundary.RejectExistingReparsePoints(_directory, path);
    }

    private static void Validate(ManagementOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.FailoverWorkInProgress || operation.CancellationRequested)
            throw new InvalidDataException("正在执行标记仅用于查询，不能替代持久操作阶段。");
        if (operation.FailoverFailureVerified &&
            (operation.Kind != ManagementOperationKind.Rebuild || operation.Phase != ManagementOperationPhase.Failed ||
             operation.FailoverPlan is not { Kind: ManagementOperationKind.Rebuild } || operation.Error is null ||
             operation.FailoverCompletedState is not null))
            throw new InvalidDataException("重建失败结案缺少绑定的持久意图。");
        if (operation.FailoverCompletedState is { } completed &&
            (operation.Phase != ManagementOperationPhase.Completed || operation.FailoverPlan is not { } plan ||
             operation.HighAvailability?.State is not { } current ||
             !JsonSerializer.SerializeToUtf8Bytes(completed, JsonDefaults.Create()).AsSpan().SequenceEqual(
                 JsonSerializer.SerializeToUtf8Bytes(plan.After, JsonDefaults.Create())) ||
             !Exam.Contracts.HighAvailability.FailoverStateHistory.Covers(current, completed)))
            throw new InvalidDataException("历史主备完成状态与持久意图不一致。");
        if (operation.FailoverPlan is { } intent &&
            (intent.RequestId != operation.RequestId || intent.ContentHash != operation.ContentHash || intent.Kind != operation.Kind ||
             intent.Before.ServerId != operation.ServerId || intent.After.ServerId != operation.ServerId))
            throw new InvalidDataException("主备意图与操作身份不一致。");
        if (operation.RecoveryActivation is { } activation &&
            (!Enum.IsDefined(activation) || operation.Kind != ManagementOperationKind.Restore ||
             operation.RecoveryWorkingDirectory is null || operation.Restore is not { IsolationStaged: true, RecoveryVerified: true } ||
             (activation == RecoveryActivationPhase.BusinessReady) != operation.Restore.BusinessReady))
            throw new InvalidDataException("恢复启用阶段与核对结果不一致。");
        if (operation.RecoveryWorkingDirectory is not null &&
            (operation.Kind != ManagementOperationKind.Restore || !Path.IsPathFullyQualified(operation.RecoveryWorkingDirectory)))
            throw new InvalidDataException("恢复工作目标必须为本机绝对路径并绑定恢复操作。");
        if (string.IsNullOrWhiteSpace(operation.RequestId) || string.IsNullOrWhiteSpace(operation.ServerId) ||
            string.IsNullOrWhiteSpace(operation.NodeId) || string.IsNullOrWhiteSpace(operation.ActorId) ||
            operation.ContentHash is null || operation.ContentHash.Length != 64 || !operation.ContentHash.All(char.IsAsciiHexDigit) ||
            !Enum.IsDefined(operation.Kind) || !Enum.IsDefined(operation.Phase) ||
            operation.AcceptedAt == default || operation.UpdatedAt < operation.AcceptedAt)
            throw new InvalidDataException("操作记录缺少有效的身份、内容绑定或阶段。");
        if (operation.RequestJson is not null && Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation.RequestJson))) != operation.ContentHash)
            throw new InvalidDataException("操作记录的原请求内容与绑定摘要不一致。");
    }
}
