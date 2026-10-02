using System.IO.Compression;
using System.Text;

namespace Exam.Admin.Services;

/// <summary>Copies an untrusted local source into one private, bounded snapshot before validation.</summary>
public sealed class ExamImportWorkspace : IDisposable
{
    private const long MaxTotalBytes = 256L * 1024 * 1024;
    private const int MaxFiles = 2048;
    private readonly string _ownedRoot;
    private bool _disposed;

    private ExamImportWorkspace(string ownedRoot)
    {
        _ownedRoot = ownedRoot;
        PackageRoot = Path.Combine(ownedRoot, "source");
        CompilationRoot = Path.Combine(ownedRoot, "compiled");
        StudentRoot = Path.Combine(ownedRoot, "student");
        EncryptedPath = Path.Combine(ownedRoot, "package.enc");
    }

    public string PackageRoot { get; }
    public string CompilationRoot { get; }
    public string StudentRoot { get; }
    public string EncryptedPath { get; }

    public static async Task<ExamImportWorkspace> CreateAsync(string source, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var root = Path.Combine(Path.GetTempPath(), "huaizhou-c05-" + Guid.NewGuid().ToString("N"));
        var workspace = new ExamImportWorkspace(root);
        Directory.CreateDirectory(workspace.PackageRoot);
        try
        {
            if (Directory.Exists(source))
                await workspace.CopyDirectoryAsync(Path.GetFullPath(source), ct);
            else if (File.Exists(source) && Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                await workspace.ExtractZipAsync(Path.GetFullPath(source), ct);
            else
                throw new InvalidDataException("请选择存在的试题目录或 ZIP 包。");
            if (!File.Exists(Path.Combine(workspace.PackageRoot, "manifest.json")))
                throw new InvalidDataException("试题包根目录缺少 manifest.json。");
            if (!File.Exists(Path.Combine(workspace.PackageRoot, "server-only", "answer-key.json")))
                throw new InvalidDataException("试题包缺少独立参考答案 server-only/answer-key.json。");
            return workspace;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private async Task CopyDirectoryAsync(string source, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(source);
        var count = 0;
        long total = 0;
        while (pending.Count != 0)
        {
            ct.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("试题目录包含链接，已拒绝读取。");
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("试题目录包含链接，已拒绝读取。");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                var info = new FileInfo(entry);
                if (++count > MaxFiles || info.Length < 0 || info.Length > MaxTotalBytes - total)
                    throw new InvalidDataException("试题包文件数量或总大小超过安全上限。");
                total += info.Length;
                var target = ResolveTarget(Path.GetRelativePath(source, entry));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var copied = await CopyBoundedAsync(input, output, info.Length, ct);
                if (copied != info.Length) throw new InvalidDataException("读取期间试题文件发生变化。");
            }
        }
    }

    private async Task ExtractZipAsync(string source, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(source);
        if (zip.Entries.Count > MaxFiles) throw new InvalidDataException("ZIP 文件数量超过安全上限。");
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.FullName.Contains('\\') || Path.IsPathRooted(entry.FullName) ||
                ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("ZIP 含非法路径或链接。");
            var target = ResolveTarget(entry.FullName);
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            if (entry.Length < 0 || entry.Length > MaxTotalBytes - total)
                throw new InvalidDataException("ZIP 解压总大小超过安全上限。");
            total += entry.Length;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var copied = await CopyBoundedAsync(input, output, entry.Length, ct);
            if (copied != entry.Length) throw new InvalidDataException("ZIP 条目读取长度不一致。");
        }
    }

    private static async Task<long> CopyBoundedAsync(Stream input, Stream output, long maximum, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        long copied = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct);
            if (count == 0) return copied;
            if (count > maximum - copied) throw new InvalidDataException("试题文件在读取时超过安全上限。");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
            copied += count;
        }
    }

    private string ResolveTarget(string relative)
    {
        var target = Path.GetFullPath(Path.Combine(PackageRoot, relative));
        if (!target.StartsWith(PackageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("试题包路径越界。");
        return target;
    }

    public static async Task<string> ReadUtf8Async(string path, CancellationToken ct = default)
    {
        var info = new FileInfo(path);
        if (info.Length is < 1 or > 10 * 1024 * 1024)
            throw new InvalidDataException("试题清单或答案为空，或超过 10 MB 上限。");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var temp = Path.GetFullPath(Path.GetTempPath());
        var full = Path.GetFullPath(_ownedRoot);
        if (Path.GetDirectoryName(full) == temp.TrimEnd(Path.DirectorySeparatorChar) &&
            Path.GetFileName(full).StartsWith("huaizhou-c05-", StringComparison.Ordinal) && Directory.Exists(full))
        {
            // A PDF reader may hold a preview file open. Cleanup must never turn a committed publication
            // into a false UI failure; the temporary directory remains isolated if Windows refuses deletion.
            try { Directory.Delete(full, recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
