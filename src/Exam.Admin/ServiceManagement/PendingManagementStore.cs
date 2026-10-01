using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;

namespace Exam.Admin.ServiceManagement;

public sealed record PendingManagementRequest(ManagementTarget Target, string Actor,
    ManagementOperationKind Kind, string RequestId, string Body, string BodyHash, DateTimeOffset CreatedAt)
{
    public static PendingManagementRequest Create(ManagementTarget target, string actor, ManagementOperationKind kind,
        string id, string body) => new(target, actor, kind, id, body, Hash(body), DateTimeOffset.UtcNow);
    public static string Hash(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    public void Validate()
    {
        Target.Validate();
        if (string.IsNullOrWhiteSpace(Actor) || string.IsNullOrWhiteSpace(RequestId) || RequestId.Length > 200 ||
            !Enum.IsDefined(Kind) || Body.Length > 65536 || BodyHash != Hash(Body)) throw new InvalidDataException("待核对请求损坏。");
        using var json = JsonDocument.Parse(Body);
        if (json.RootElement.GetProperty("requestId").GetString() != RequestId ||
            json.RootElement.EnumerateObject().Any(p => p.Name is "password" or "token" or "authorization"))
            throw new InvalidDataException("待核对请求绑定无效。");
    }
}

public interface IPendingManagementStore
{
    PendingManagementRequest? Load(ManagementTarget target);
    void Save(PendingManagementRequest request);
    void Clear(PendingManagementRequest request);
}

/// <summary>Only an immutable operation request is stored. No password, session or response body is persisted.</summary>
public sealed class ProtectedPendingManagementStore(string directory) : IPendingManagementStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Exam.Admin.F04.Pending.v1");
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly JsonSerializerOptions _json = JsonDefaults.Create();

    public PendingManagementRequest? Load(ManagementTarget target)
    {
        var path = FilePath(target);
        if (!File.Exists(path)) return null;
        CheckBoundary(path);
        if (new FileInfo(path).Length > 128 * 1024) throw new InvalidDataException("待核对请求超过限制。");
        byte[]? plaintext = null;
        try
        {
            plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var result = JsonSerializer.Deserialize<PendingManagementRequest>(plaintext, _json)
                ?? throw new InvalidDataException("待核对请求为空。");
            result.Validate();
            if (result.Target.Key != target.Key) throw new InvalidDataException("待核对请求属于其他节点。");
            return result;
        }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void Save(PendingManagementRequest request)
    {
        request.Validate();
        var path = FilePath(request.Target);
        CheckBoundary(path);
        Directory.CreateDirectory(_directory);
        CheckBoundary(path);
        using var guard = Acquire(request.Target);
        var existing = Load(request.Target);
        if (existing is not null && existing != request)
            throw new InvalidOperationException("该节点仍有待核对请求，请先查询原结果。");
        if (existing is not null) return;
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(request, _json);
        var temporary = Path.Combine(_directory, ".pending-" + Guid.NewGuid().ToString("N"));
        try
        {
            var encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(encrypted); output.Flush(true); }
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void Clear(PendingManagementRequest request)
    {
        using var guard = Acquire(request.Target);
        var current = Load(request.Target);
        if (current is null) return;
        if (current.RequestId != request.RequestId || current.BodyHash != request.BodyHash || current.Actor != request.Actor)
            throw new InvalidDataException("不能删除其他待核对请求。");
        File.Delete(FilePath(request.Target));
    }
    private string FilePath(ManagementTarget target) => Path.Combine(_directory, target.Key + ".pending");
    private FileStream Acquire(ManagementTarget target)
    {
        var path = Path.Combine(_directory, target.Key + ".lock");
        CheckBoundary(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private void CheckBoundary(string path)
    {
        for (var current = new DirectoryInfo(Path.GetDirectoryName(path)!); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("请求记录目录不能经过链接。");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("请求记录不能是链接。");
    }
}
