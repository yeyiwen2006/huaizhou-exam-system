using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Common;

namespace Exam.Admin.ServiceManagement;

public sealed record ProtectionWarningMemory(string TargetKey, long Epoch, long Version, DateTimeOffset ObservedAt, bool Degraded);
public interface IProtectionWarningStore
{
    ProtectionWarningMemory? Load(ManagementTarget target);
    void Save(ManagementTarget target, long epoch, long version, DateTimeOffset observedAt, bool degraded);
}

/// <summary>Persists only the last verified protection warning. It never stores an authentication session.</summary>
public sealed class ProtectedProtectionWarningStore(string directory) : IProtectionWarningStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Exam.Admin.F04.Protection.v1");
    private string Resolve(ManagementTarget target, string suffix)
    {
        var root = Path.GetFullPath(directory); var path = Path.Combine(root, target.Key + suffix);
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("保护记录目录不能经过链接。");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("保护记录不能是链接。");
        return path;
    }
    public ProtectionWarningMemory? Load(ManagementTarget target)
    {
        var path = Resolve(target, ".protection"); if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length is <= 0 or > 16384) throw new IOException("保护记录大小无效。");
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var value = JsonSerializer.Deserialize<ProtectionWarningMemory>(bytes, JsonDefaults.Create());
            if (value is null || value.TargetKey != target.Key || value.Epoch <= 0 || value.Version <= 0 || value.ObservedAt == default ||
                !bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Create()))) throw new IOException("保护记录绑定无效。");
            return value;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Save(ManagementTarget target, long epoch, long version, DateTimeOffset observedAt, bool degraded)
    {
        if (epoch <= 0 || version <= 0 || observedAt == default) throw new ArgumentException("保护记录版本无效。");
        var path = Resolve(target, ".protection"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); Resolve(target, ".protection");
        using var guard = new FileStream(Resolve(target, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var previous = Load(target);
        if (previous is not null && (epoch < previous.Epoch || epoch == previous.Epoch &&
            (version < previous.Version || version == previous.Version && observedAt < previous.ObservedAt))) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ProtectionWarningMemory(target.Key, epoch, version, observedAt, degraded), JsonDefaults.Create());
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(protectedBytes); output.Flush(true); }
            Resolve(target, ".protection"); File.Move(temporary, path, true);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
