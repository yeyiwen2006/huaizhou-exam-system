using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.IO.Compression;

namespace Exam.Reporting;

// BCL-only shared source compiled by Infrastructure and the WPF client.
public sealed record DownloadedWorkbook(byte[] Bytes, string ServerId, string NodeId, long ServerEpoch, string SnapshotId);
public static class ReportWorkbookDownload
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const int MaximumBytes = 128 * 1024 * 1024;
    public static async Task<DownloadedWorkbook> ReadAsync(HttpResponseMessage response, string expectedSnapshot,
        CancellationToken cancellationToken = default)
    {
        response.EnsureSuccessStatusCode();
        string Header(string name)
        {
            if (!response.Headers.TryGetValues(name, out var values))
                throw new InvalidDataException("工作簿响应缺少校验信息。");
            var items = values.Take(2).ToArray();
            if (items.Length != 1 || string.IsNullOrWhiteSpace(items[0]))
                throw new InvalidDataException("工作簿响应校验信息不唯一或为空。");
            return items[0];
        }
        var server = Header("X-Exam-Server-Id"); var node = Header("X-Exam-Node-Id");
        if (!long.TryParse(Header("X-Exam-Server-Epoch"), out var epoch) || epoch < 1 ||
            string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(node) ||
            Header("X-Exam-Report-Snapshot") != expectedSnapshot ||
            response.Content.Headers.ContentType?.MediaType != ContentType ||
            response.Content.Headers.ContentLength > MaximumBytes)
            throw new InvalidDataException("工作簿身份、版本或类型无效。");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + read > MaximumBytes) throw new InvalidDataException("工作簿超过下载上限。");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        var bytes = output.ToArray();
        if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'K' ||
            response.Content.Headers.ContentLength is { } length && length != bytes.Length ||
            !string.Equals(Header("X-Exam-Workbook-Sha256"), Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.Ordinal))
            throw new InvalidDataException("工作簿传输不完整或内容校验失败。");
        using (var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
        {
            foreach (var part in new[] { "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml" })
                if (archive.Entries.Count(entry => entry.FullName == part && entry.Length > 0) != 1 ||
                    archive.Entries.Count(entry => entry.FullName == part) != 1)
                    throw new InvalidDataException("工作簿容器缺少完整且唯一的必需部件。");
        }
        return new(bytes, server, node, epoch, expectedSnapshot);
    }

    public static async Task SaveAsync(DownloadedWorkbook workbook, string destinationPath,
        Func<Task> validateBeforeCommit, CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(directory, ".g-download-" + Guid.NewGuid().ToString("N") + ".partial");
        var created = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                created = true;
                await file.WriteAsync(workbook.Bytes, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(true);
            }
            await ReportFileCommit.ReplaceAsync(temporary, destination, validateBeforeCommit, cancellationToken);
        }
        finally { if (created) File.Delete(temporary); }
    }
}

// Shared by generated workbooks and downloaded workbooks (including the WPF linked source).
// A transient Windows rename denial must not cause a delete/copy fallback or a stale commit.
internal static class ReportFileCommit
{
    // Final validation and replacement form one async commit interval across both savers.
    // Downloads, durable temporary writes and retry delays do not hold the gate.
    private static readonly SemaphoreSlim CommitGate = new(1, 1);

    internal static async Task ReplaceAsync(string temporary, string destination,
        Func<Task>? validateBeforeCommit, CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var retries = 0;
        while (true)
        {
            await CommitGate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Queueing or a retry may span a version change. Validate only after entry.
                if (validateBeforeCommit is not null) await validateBeforeCommit();
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(temporary, destination, overwrite: true);
                    return; // The atomic same-directory replacement is the commit point.
                }
                catch (Exception error) when (IsWindowsSharingFailure(error) && retries < 20 &&
                    System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
                {
                    retries++;
                }
            }
            finally { CommitGate.Release(); }
            await Task.Delay(Math.Min(10 * retries, 100), cancellationToken);
        }
    }

    private static bool IsWindowsSharingFailure(Exception error) => OperatingSystem.IsWindows() &&
        error is IOException or UnauthorizedAccessException && (error.HResult & 0xffff) is 5 or 32 or 33;
}
