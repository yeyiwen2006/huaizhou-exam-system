using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Infrastructure.HighAvailability;

/// <summary>A protected head, independent of PostgreSQL. A pending head never proves success.</summary>
public sealed record ConfirmationHead(DurableConfirmation Record, bool Confirmed);

public sealed class FileConfirmationStore(string directory)
{
    private string HeadPath => Path.Combine(directory, "confirmation-head.json");
    private void Check()
    {
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            throw new IOException("确认记录目录须由部署人员预建。");
        PathBoundary.RejectExistingReparsePoints(directory, HeadPath);
    }

    public FileStream Lock()
    {
        Check();
        var path = Path.Combine(directory, "confirmation.lock");
        PathBoundary.RejectExistingReparsePoints(directory, path);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public ConfirmationHead? Read()
    {
        Check();
        try
        {
            using var file = new FileStream(HeadPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length is <= 0 or > 16384) throw new InvalidDataException("确认记录大小无效。");
            var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
            var head = JsonSerializer.Deserialize<ConfirmationHead>(bytes, JsonDefaults.Create())
                ?? throw new InvalidDataException("确认记录缺失。");
            Validate(head.Record);
            if (!bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(head, JsonDefaults.Create())))
                throw new InvalidDataException("确认记录不是完整规范记录。");
            return head;
        }
        catch (FileNotFoundException) { return null; }
    }

    // Caller holds Lock throughout the primary/standby handshake, preventing crossed heads.
    public async Task SaveAsync(ConfirmationHead head, CancellationToken token)
    {
        Check(); Validate(head.Record);
        var previous = Read();
        if (previous is not null && (previous.Record.ServerId != head.Record.ServerId ||
            previous.Record.PrimaryNodeId != head.Record.PrimaryNodeId || previous.Record.Epoch != head.Record.Epoch ||
            previous.Record.SystemIdentifier != head.Record.SystemIdentifier || previous.Record.Timeline != head.Record.Timeline ||
            previous.Record.DatabaseName != head.Record.DatabaseName || previous.Record.StandbyNodeId != head.Record.StandbyNodeId ||
            head.Record.StateVersion < previous.Record.StateVersion || Lsn(head.Record.FlushLsn) < Lsn(previous.Record.FlushLsn)))
            throw new InvalidDataException("确认记录身份变化或位置回退，须独立维护核对。");
        var temporary = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(file, head, JsonDefaults.Create(), token);
                await file.FlushAsync(token); file.Flush(true);
            }
            token.ThrowIfCancellationRequested(); Check();
            if (OperatingSystem.IsWindows())
            {
                // Flush the replacement itself, not just the temporary file's contents.
                if (!MoveFileEx(temporary, HeadPath, 1 | 8))
                    throw new IOException("持久确认记录替换失败。", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
            }
            else File.Move(temporary, HeadPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, int flags);

    public static ulong Lsn(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 2 || !uint.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out var high) ||
            !uint.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out var low))
            throw new InvalidDataException("确认位置无效。");
        return ((ulong)high << 32) | low;
    }

    public static void Validate(DurableConfirmation record)
    {
        if (!Guid.TryParseExact(record.Id, "N", out _) || string.IsNullOrWhiteSpace(record.ServerId) ||
            string.IsNullOrWhiteSpace(record.PrimaryNodeId) || string.IsNullOrWhiteSpace(record.StandbyNodeId) ||
            record.PrimaryNodeId == record.StandbyNodeId || record.Epoch < 1 || record.StateVersion < 1 ||
            record.Mode is not (ProtectionMode.SynchronousDualNode or ProtectionMode.EmergencySingleNode) ||
            !ulong.TryParse(record.SystemIdentifier, out _) || string.IsNullOrWhiteSpace(record.DatabaseName) ||
            record.Timeline < 1 || Lsn(record.FlushLsn) == 0 ||
            record.ResourcesDigest.Length != 64 || record.ResourcesDigest.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidDataException("确认记录缺少有效边界。");
    }

    public static string ResourcesDigest(string root, bool persist = false)
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root)) throw new IOException("资源目录不可读。");
        PathBoundary.RejectExistingReparsePoints(root, root);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        void Visit(string path)
        {
            foreach (var item in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal))
            {
                if (++count > 100000) throw new IOException("资源清单超过容量限制。");
                PathBoundary.RejectExistingReparsePoints(root, item);
                var name = Encoding.UTF8.GetBytes(Path.GetRelativePath(root, item).Replace('\\', '/'));
                digest.AppendData(BitConverter.GetBytes(name.Length)); digest.AppendData(name);
                if (Directory.Exists(item)) { digest.AppendData([0]); Visit(item); }
                else
                {
                    digest.AppendData([1]);
                    using var file = new FileStream(item, FileMode.Open, persist ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read);
                    digest.AppendData(BitConverter.GetBytes(file.Length)); digest.AppendData(SHA256.HashData(file));
                    if (persist) file.Flush(true);
                }
            }
        }
        Visit(root); return Convert.ToHexString(digest.GetHashAndReset());
    }
}
