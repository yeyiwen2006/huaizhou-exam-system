using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Exam.Admin.Services;

/// <summary>Fixed business request identity, independent of a network session or node address.</summary>
public sealed class PendingBusinessMutationStore(string directory)
{
    public sealed record Mutation(string ServerId, string Actor, string Method, string Path, string BodyHash,
        string RequestId, long Epoch);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Exam.Admin.BusinessRequests.v1");
    private readonly string _root = Path.GetFullPath(directory);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private string Scope(string server, string actor) => Path.Combine(_root, Hash(server + "\0" + actor));
    private static string Key(string method, string path, string body) => Hash(method + "\0" + path + "\0" + body);

    public Mutation GetOrCreate(string server, string actor, long epoch, string method, string path, string body)
    {
        var bodyHash = Hash(body);
        var scope = Scope(server, actor);
        Check(scope); Directory.CreateDirectory(scope); Check(scope);
        using var guard = new FileStream(Path.Combine(scope, "requests.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var file = Path.Combine(scope, Key(method, path, bodyHash) + ".pending");
        var current = Read(file);
        if (current is not null)
        {
            if (current.ServerId != server || current.Actor != actor || current.Method != method || current.Path != path ||
                current.BodyHash != bodyHash || current.Epoch > epoch || string.IsNullOrWhiteSpace(current.RequestId))
                throw new InvalidDataException("未确认的业务请求与服务器身份、内容或代次不符。");
            if (epoch > current.Epoch) Write(file, current = current with { Epoch = epoch });
            return current;
        }
        if (Directory.EnumerateFiles(scope, "*.pending").Take(128).Count() >= 128)
            throw new InvalidOperationException("未确认的写操作过多，请先核对原请求。");
        var created = new Mutation(server, actor, method, path, bodyHash, Guid.NewGuid().ToString("N"), epoch);
        Write(file, created);
        return created;
    }

    public void Complete(Mutation expected)
    {
        var scope = Scope(expected.ServerId, expected.Actor);
        Check(scope);
        using var guard = new FileStream(Path.Combine(scope, "requests.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var file = Path.Combine(scope, Key(expected.Method, expected.Path, expected.BodyHash) + ".pending");
        if (Read(file) is { } current && current.RequestId == expected.RequestId && current.BodyHash == expected.BodyHash)
        {
            if (expected.Epoch < current.Epoch) throw new InvalidDataException("旧代次响应不能清除新节点上的未确认业务请求。");
            File.Delete(file);
        }
    }

    private static Mutation? Read(string file)
    {
        Check(file);
        if (!File.Exists(file)) return null;
        if (new FileInfo(file).Length > 4 * 1024 * 1024) throw new IOException("业务请求记录超限。");
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<Mutation>(bytes) ?? throw new IOException("业务请求记录损坏。"); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void Write(string file, Mutation value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            if (bytes.Length > 4 * 1024 * 1024) throw new IOException("业务请求记录超限。");
            var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(encrypted); output.Flush(true); }
            File.Move(temporary, file, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Check(string path)
    {
        for (var current = new FileInfo(path) as FileSystemInfo; current is not null;
             current = current is DirectoryInfo folder ? folder.Parent : ((FileInfo)current).Directory)
            if ((File.Exists(current.FullName) || Directory.Exists(current.FullName)) &&
                (File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("业务请求记录不能经过链接。");
    }
}
