using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Runtime.InteropServices;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Infrastructure.HighAvailability;

public sealed record ReplicaResourceState(string ServerId, string NodeId, string ActiveNodeId,
    long Epoch, string SystemIdentifier, bool InRecovery, string ResourcesDigest);

/// <summary>Authenticated node-to-node resource snapshots. PostgreSQL data continues to use WAL streaming.</summary>
public sealed partial class HttpConfirmationPeer
{
    private DurableConfirmation? _latestRequest;

    public Task SynchronizeResourcesAsync(string directory, CancellationToken token) =>
        _latestRequest is null ? Task.CompletedTask :
            SynchronizeResourcesAsync(directory, FileConfirmationStore.ResourcesDigest(directory, persist: true), token);

    public async Task SynchronizeResourcesAsync(string directory, string verifiedDigest, CancellationToken token)
    {
        var record = _latestRequest;
        if (record is null) return;
        if (verifiedDigest is null || verifiedDigest.Length != 64 || verifiedDigest.Any(c => !Uri.IsHexDigit(c)))
            throw new IOException("源资源版本无效。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var address = new Uri(Address(), "/replication/v1/resources");
        using var stateRequest = ResourceRequest(HttpMethod.Get, address, record);
        using var stateResponse = await http.SendAsync(stateRequest, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        stateResponse.EnsureSuccessStatusCode();
        if (stateResponse.Content.Headers.ContentLength is > 16384)
            throw new IOException("备节点资源状态超限。");
        await using var stateBody = await stateResponse.Content.ReadAsStreamAsync(deadline.Token);
        var state = await ReplicaResourceTransfer.ReadStateAsync(stateBody, deadline.Token);
        if (state is null || state.ServerId != serverId || state.NodeId != standbyNodeId ||
            state.ActiveNodeId != record.PrimaryNodeId || state.Epoch != record.Epoch ||
            state.SystemIdentifier != record.SystemIdentifier || !state.InRecovery)
            throw new IOException("资源接收方不是当前部署的备用节点。");
        var digest = verifiedDigest;
        if (state.ResourcesDigest == digest) return;
        var archive = await ReplicaResourceTransfer.CaptureAsync(directory, digest, deadline.Token);
        try
        {
            await using var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var request = ResourceRequest(HttpMethod.Post, address, record);
            request.Headers.Add("X-Exam-Resources-Digest", digest);
            request.Content = new StreamContent(input);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            response.EnsureSuccessStatusCode();
        }
        finally { File.Delete(archive); }
    }

    private HttpRequestMessage ResourceRequest(HttpMethod method, Uri address, DurableConfirmation record)
    {
        var secret = credential();
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
            throw new IOException("节点复制凭据未配置。");
        var request = new HttpRequestMessage(method, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Headers.Add("X-Exam-Server-Id", serverId);
        request.Headers.Add("X-Exam-Node-Id", standbyNodeId);
        request.Headers.Add("X-Exam-Primary-Node-Id", record.PrimaryNodeId);
        request.Headers.Add("X-Exam-Epoch", record.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Exam-System-Identifier", record.SystemIdentifier);
        return request;
    }
}

public static class ReplicaResourceTransfer
{
    public const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    public const int MaximumEntries = 100000;

    public static async Task RecoverAsync(string target, string owner, Func<CancellationToken, Task> verifyStandby, CancellationToken token)
    {
        var full = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        if (!File.Exists(full + ".replica-swap.json")) return;
        PathBoundary.RejectExistingReparsePoints(Path.GetDirectoryName(full)!, full);
        using var guard = new FileStream(full + ".replica-transfer.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await new ReplicaResourceExchange(full, owner).RecoverAsync(verifyStandby, token);
    }

    public static async Task<ReplicaResourceState> ReadStateAsync(Stream input, CancellationToken token)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[4096]; int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + count > 16384) throw new IOException("节点资源身份响应超限。");
            await memory.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return JsonSerializer.Deserialize<ReplicaResourceState>(memory.ToArray(), JsonDefaults.Create())
            ?? throw new IOException("节点资源身份响应缺失。");
    }

    public static async Task<string> CaptureAsync(string source, string expectedDigest, CancellationToken token)
    {
        PathBoundary.RejectExistingReparsePoints(source, source);
        var archive = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + ".replication-" + Guid.NewGuid().ToString("N") + ".zip";
        try
        {
            await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var pending = new Queue<string>(); pending.Enqueue(source);
                    var count = 0; long total = 0;
                    while (pending.TryDequeue(out var directory))
                    {
                        foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
                        {
                            token.ThrowIfCancellationRequested();
                            if (++count > MaximumEntries) throw new IOException("资源快照文件数超限。");
                            PathBoundary.RejectExistingReparsePoints(source, path);
                            var name = Path.GetRelativePath(source, path).Replace('\\', '/');
                            if (Directory.Exists(path)) { zip.CreateEntry(name + "/"); pending.Enqueue(path); continue; }
                            total = checked(total + new FileInfo(path).Length);
                            if (total > MaximumBytes) throw new IOException("资源快照大小超限。");
                            var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                            await using var body = entry.Open();
                            await input.CopyToAsync(body, token);
                        }
                    }
                }
                await output.FlushAsync(token);
            }
            if (new FileInfo(archive).Length > MaximumBytes) throw new IOException("压缩资源快照大小超限。");
            if (FileConfirmationStore.ResourcesDigest(source) != expectedDigest)
                throw new IOException("复制期间源资源发生变化，稍后重试。");
            return archive;
        }
        catch { if (File.Exists(archive)) File.Delete(archive); throw; }
    }

    // Callers hold the receiver's confirmation lock and verify that its database is still a standby.
    public static async Task ReceiveAsync(Stream input, string target, string expectedDigest,
        Func<CancellationToken, Task> verifyStandby, CancellationToken token, string owner = "")
    {
        if (expectedDigest.Length != 64 || expectedDigest.Any(c => !Uri.IsHexDigit(c)))
            throw new IOException("资源版本无效。");
        var full = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
        PathBoundary.RejectExistingReparsePoints(Path.GetDirectoryName(full)!, full);
        using var transferLock = new FileStream(full + ".replica-transfer.lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var exchange = new ReplicaResourceExchange(full, owner);
        await exchange.RecoverAsync(verifyStandby, token);
        if (!Directory.Exists(full)) throw new IOException("资源接收目录须预先部署。");
        PathBoundary.RejectExistingReparsePoints(full, full);
        var suffix = Guid.NewGuid().ToString("N");
        var archive = full + ".incoming-" + suffix + ".zip";
        var staged = full + ".incoming-" + suffix;
        try
        {
            await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                var bytes = new byte[65536]; long total = 0; int count;
                while ((count = await input.ReadAsync(bytes, token)) != 0)
                {
                    total = checked(total + count);
                    if (total > MaximumBytes) throw new IOException("资源复制请求超限。");
                    await output.WriteAsync(bytes.AsMemory(0, count), token);
                }
                await output.FlushAsync(token); output.Position = 0;
                Directory.CreateDirectory(staged);
                using var zip = new ZipArchive(output, ZipArchiveMode.Read, leaveOpen: true);
                if (zip.Entries.Count > MaximumEntries) throw new IOException("资源清单超限。");
                var required = zip.Entries.Sum(entry => checked(entry.Length));
                if (required > MaximumBytes || new DriveInfo(Path.GetPathRoot(full)!).AvailableFreeSpace < required + 16L * 1024 * 1024)
                    throw new IOException("资源接收暂存空间不足或快照超限。");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long expanded = 0;
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var name = entry.FullName;
                    var parts = name.TrimEnd('/').Split('/');
                    if (string.IsNullOrWhiteSpace(name) || name.Contains('\\') || name.Contains(':') ||
                        name.StartsWith('/') || parts.Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ')) ||
                        !names.Add(name.TrimEnd('/')) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                        throw new IOException("资源复制清单含不安全或重复路径。");
                    var path = PathBoundary.ResolveWithinRoot(staged, Path.Combine(staged, name.Replace('/', Path.DirectorySeparatorChar)),
                        "资源条目超出接收目录。");
                    if (name.EndsWith('/')) { Directory.CreateDirectory(path); continue; }
                    expanded = checked(expanded + entry.Length);
                    if (expanded > MaximumBytes) throw new IOException("资源展开后超限。");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await using var source = entry.Open();
                    // Flush once at the complete-file boundary. WriteThrough on every 64 KiB
                    // chunk made large snapshots wait for thousands of redundant disk flushes.
                    await using var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var buffer = new byte[65536]; long entryBytes = 0; int read;
                    while ((read = await source.ReadAsync(buffer, token)) > 0)
                    {
                        entryBytes = checked(entryBytes + read);
                        if (entryBytes > entry.Length) throw new IOException("资源条目实际展开长度超限。");
                        await destination.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                    if (entryBytes != entry.Length) throw new IOException("资源条目长度不符。");
                    await destination.FlushAsync(token); destination.Flush(true);
                }
            }
            // Every extracted file has already been flushed to durable storage above.
            if (FileConfirmationStore.ResourcesDigest(staged) != expectedDigest)
                throw new IOException("接收资源与活动节点版本不同。");
            // A lost success response may resend the same snapshot. Keep the current copy
            // and its previous-version directory instead of accumulating identical copies.
            var previousDigest = FileConfirmationStore.ResourcesDigest(full, persist: true);
            var alreadyCurrent = previousDigest == expectedDigest;
            if (alreadyCurrent)
            {
                await verifyStandby(token);
                token.ThrowIfCancellationRequested();
                return;
            }
            // ExchangeAsync checks the role and cancellation immediately before switching directories.
            await exchange.ExchangeAsync(suffix, expectedDigest, previousDigest, verifyStandby, token);
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (Directory.Exists(staged) && !exchange.Holds(suffix)) Directory.Delete(staged, recursive: true);
        }
    }

    internal static void MoveDirectory(string source, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!MoveFileEx(source, target, 8)) throw new IOException("资源目录持久替换失败。",
                Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }
        else Directory.Move(source, target);
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, int flags);
}
