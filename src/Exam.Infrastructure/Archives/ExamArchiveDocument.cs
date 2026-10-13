using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Exam.Contracts.Archives;
using Exam.Contracts.Common;

namespace Exam.Infrastructure.Archives;

public sealed record ArchiveResource(string ResourceId, string ResourceKind, long Version,
    DateTimeOffset CreatedAt, string CreatedBy, string ContentEntry);
public sealed record ExamArchiveManifest(int FormatVersion, string ArchiveId, string SourceExamId,
    string OriginExamId, string ExamName, DateTimeOffset ExportedAt,
    Dictionary<string, JsonArray> Tables, IReadOnlyList<ArchiveResource> Resources);

/// <summary>A fixed system format; archive names never become filesystem paths or SQL identifiers.</summary>
public sealed class ExamArchiveDocument(ExamArchiveManifest manifest, Dictionary<string, byte[]> contents)
{
    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();
    public ExamArchiveManifest Manifest { get; } = manifest;
    public Dictionary<string, byte[]> Contents { get; } = contents;

    public async Task WriteAsync(Stream destination, CancellationToken ct = default)
    {
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(Manifest, Json);
        if (Contents.Values.Sum(bytes => bytes.LongLength) + manifestBytes.LongLength > ExamArchiveFormat.MaximumExpandedBytes)
            throw new InvalidDataException("考试归档内容超过 1 GiB 限制。");
        using var bounded = new ArchiveOutputStream(destination);
        using var zip = new ZipArchive(bounded, ZipArchiveMode.Create, leaveOpen: true);
        await using (var output = zip.CreateEntry("manifest.json", CompressionLevel.Fastest).Open())
            await output.WriteAsync(manifestBytes, ct);
        foreach (var item in Contents.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            await using var output = zip.CreateEntry(item.Key, CompressionLevel.Fastest).Open();
            await output.WriteAsync(item.Value, ct);
        }
    }

    public static async Task<ExamArchiveDocument> ReadAsync(Stream source, CancellationToken ct = default)
    {
        using var input = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(buffer, ct)) != 0)
        {
            if (input.Length + count > ExamArchiveFormat.MaximumArchiveBytes)
                throw new InvalidDataException("考试归档超过 512 MiB 大小限制。");
            await input.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        input.Position = 0;
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        if (zip.Entries.Count is < 1 or > 16384) throw new InvalidDataException("考试归档文件数量无效。");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.Length > 256 || entry.FullName.Contains('\\') ||
                entry.FullName.Split('/').Any(part => part is "" or "." or "..") ||
                !(entry.FullName == "manifest.json" || entry.FullName.StartsWith("content/", StringComparison.Ordinal)) ||
                entry.Length > int.MaxValue || (expanded += entry.Length) > ExamArchiveFormat.MaximumExpandedBytes)
                throw new InvalidDataException("考试归档包含无效路径或内容超过限制。");
            using var output = new MemoryStream();
            await using var content = entry.Open();
            while ((count = await content.ReadAsync(buffer, ct)) != 0)
            {
                if (output.Length + count > entry.Length) throw new InvalidDataException("考试归档内容长度无效。");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            if (!files.TryAdd(entry.FullName, output.ToArray())) throw new InvalidDataException("考试归档包含重复文件。");
        }
        if (!files.Remove("manifest.json", out var json)) throw new InvalidDataException("未找到考试归档清单。");
        var manifest = JsonSerializer.Deserialize<ExamArchiveManifest>(json, Json)
            ?? throw new InvalidDataException("考试归档清单无效。");
        if (manifest.FormatVersion != ExamArchiveFormat.Version ||
            string.IsNullOrWhiteSpace(manifest.ArchiveId) || string.IsNullOrWhiteSpace(manifest.SourceExamId) ||
            string.IsNullOrWhiteSpace(manifest.OriginExamId) || string.IsNullOrWhiteSpace(manifest.ExamName) ||
            manifest.ExamName.Length > 128 || manifest.Tables is null || manifest.Resources is null)
            throw new InvalidDataException("考试归档格式不受支持或缺少考试信息。");
        return new(manifest, files);
    }

    private sealed class ArchiveOutputStream(Stream destination) : Stream
    {
        private long written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() => destination.Flush();
        public override Task FlushAsync(CancellationToken ct) => destination.FlushAsync(ct);
        private void Count(int length)
        {
            if (written + length > ExamArchiveFormat.MaximumArchiveBytes) throw new InvalidDataException("考试归档超过 512 MiB 大小限制。");
            written += length;
        }
        public override void Write(byte[] buffer, int offset, int count) { Count(count); destination.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Count(buffer.Length); destination.Write(buffer); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { Count(buffer.Length); await destination.WriteAsync(buffer, ct); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
