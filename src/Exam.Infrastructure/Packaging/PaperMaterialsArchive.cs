using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Exam.Contracts.Common;
using Exam.Domain.Importing;

namespace Exam.Infrastructure.Packaging;

/// <summary>
/// **源材料归档**：把一次导入的原始材料（清单、题目 `.tex`、共享样式、图片、独立答案）打成一个
/// 确定性 ZIP，随发布一起交给服务器保存——既供后续修题取回，也供服务器核对「实际变更题号」。
///
/// 约定（同步写在 `docs/import-package-spec.md`）：
/// - 条目路径一律**相对材料包根**、`/` 分隔，与清单里的 `source`、`resourceRoots` 同一坐标系；
/// - 条目按路径 ordinal 排序、时间戳固定（1980-01-01），同样输入得到同样字节；
/// - 归档内 `materials.json` 逐个登记路径、长度与 SHA-256：既能发现归档被截断/篡改，
///   也让「同一版本不同内容」这种请求在任何写入之前就被拒绝；
/// - 上限：归档 ≤ <see cref="MaxArchiveBytes"/>、条目 ≤ <see cref="MaxEntries"/>、
///   文本类文件 ≤ <see cref="MaxTextFileBytes"/>（沿用 TeX 源文件的既有上限）。
/// </summary>
public static class PaperMaterialsArchive
{
    /// <summary>归档格式版本。格式或校验语义一变就递增；服务器按库内记录拒绝跨版本比较。</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>归档内材料清单的文件名。</summary>
    public const string ManifestFileName = "materials.json";

    /// <summary>归档字节上限。</summary>
    public const int MaxArchiveBytes = 64 * 1024 * 1024;

    /// <summary>归档条目数上限（含材料清单）。</summary>
    public const int MaxEntries = 2048;

    /// <summary>文本类材料的单文件上限。</summary>
    public const int MaxTextFileBytes = 8 * 1024 * 1024;

    /// <summary>条目相对路径长度上限（与导入工作区的路径规则一致）。</summary>
    public const int MaxPathLength = 512;

    private static readonly string[] TextExtensions = [".tex", ".sty", ".cls", ".def", ".cfg"];

    private static readonly JsonSerializerOptions ManifestJson = new(JsonDefaults.Create())
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>按确定性格式打归档。同样的材料必然得到同样的字节。</summary>
    public static byte[] Build(string examId, long paperVersion,
        IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(examId);
        ArgumentOutOfRangeException.ThrowIfLessThan(paperVersion, 1);
        ArgumentNullException.ThrowIfNull(files);

        // 忽略大小写：归档里不能同时存在只差大小写的两个路径（读侧同样拒绝）。
        var ordered = new SortedDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, content) in files)
        {
            var normalized = NormalizeEntryPath(path);
            if (content is null)
            {
                throw new ArgumentException($"材料内容不能为 null：{path}", nameof(files));
            }

            if (!ordered.TryAdd(normalized, content))
            {
                throw new ArgumentException($"归档里存在重复路径（忽略大小写）：{path}", nameof(files));
            }
        }

        if (ordered.Count + 1 > MaxEntries)
        {
            throw new InvalidDataException($"材料文件过多（最多 {MaxEntries - 1} 个），无法生成归档。");
        }

        var manifest = new PaperMaterialsManifest(
            CurrentFormatVersion,
            examId,
            paperVersion,
            ordered.Select(pair => new PaperMaterialsEntry(
                pair.Key,
                pair.Value.Length,
                Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant())).ToList());

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in ordered)
            {
                WriteEntry(zip, path, content);
            }

            WriteEntry(zip, ManifestFileName, Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(manifest, ManifestJson)));
        }

        if (buffer.Length > MaxArchiveBytes)
        {
            throw new InvalidDataException($"材料归档超过 {MaxArchiveBytes} 字节上限。");
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// 读并**校验**归档：格式版本、上限、路径合法性、逐文件长度与 SHA-256、清单与条目双向一致。
    /// 任一项不符都返回稳定错误码，不把坏归档当成材料集合使用。
    /// </summary>
    public static PaperMaterialsArchiveResult Read(byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.Length == 0 || archive.Length > MaxArchiveBytes)
        {
            return Failed("MaterialsArchiveInvalid", "材料归档为空或超过大小上限。");
        }

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        byte[]? manifestBytes = null;
        try
        {
            using var stream = new MemoryStream(archive, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            if (zip.Entries.Count is 0 || zip.Entries.Count > MaxEntries)
            {
                return Failed("MaterialsArchiveInvalid", "材料归档条目数超出允许范围。");
            }

            foreach (var entry in zip.Entries)
            {
                var path = NormalizeEntryPath(entry.FullName);
                if (!IsValidEntryPath(path))
                {
                    return Failed("MaterialsArchiveInvalid", $"材料归档里有非法路径：{entry.FullName}");
                }

                if (entry.Length > MaxArchiveBytes)
                {
                    return Failed("MaterialsArchiveInvalid", $"材料归档条目过大：{path}");
                }

                // 逐条按上限读取：压缩后 64 MiB 的归档解压后可以膨胀很多倍，不能无界读进内存。
                using var content = new MemoryStream();
                using (var source = entry.Open())
                {
                    var buffer = new byte[64 * 1024];
                    while (true)
                    {
                        var read = source.Read(buffer, 0, buffer.Length);
                        if (read <= 0)
                        {
                            break;
                        }

                        if (content.Length + read > MaxArchiveBytes)
                        {
                            return Failed("MaterialsArchiveInvalid", $"材料归档条目解压后超出上限：{path}");
                        }

                        content.Write(buffer, 0, read);
                    }
                }

                if (content.Length != entry.Length)
                {
                    return Failed("MaterialsArchiveInvalid", $"材料归档条目长度与声明不符：{path}");
                }

                if (path == ManifestFileName)
                {
                    manifestBytes = content.ToArray();
                    continue;
                }

                if (IsTextLike(path) && content.Length > MaxTextFileBytes)
                {
                    return Failed("MaterialsArchiveInvalid", $"文本材料超过单文件上限：{path}");
                }

                if (!files.TryAdd(path, content.ToArray()))
                {
                    return Failed("MaterialsArchiveInvalid", $"材料归档里有重复路径（忽略大小写）：{path}");
                }
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or NotSupportedException)
        {
            return Failed("MaterialsArchiveInvalid", "材料归档不是可读的 ZIP。");
        }

        if (manifestBytes is null)
        {
            return Failed("MaterialsArchiveInvalid", $"材料归档缺少 {ManifestFileName}。");
        }

        PaperMaterialsManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PaperMaterialsManifest>(manifestBytes, ManifestJson);
        }
        catch (JsonException)
        {
            return Failed("MaterialsArchiveInvalid", "材料归档的清单不是有效 JSON。");
        }

        if (manifest is null || manifest.Files is null)
        {
            return Failed("MaterialsArchiveInvalid", "材料归档的清单不完整。");
        }

        if (manifest.FormatVersion != CurrentFormatVersion)
        {
            return Failed("MaterialsFormatUnsupported",
                $"材料归档格式版本 {manifest.FormatVersion} 不被支持（当前 {CurrentFormatVersion}）。");
        }

        if (manifest.Files.Count != files.Count)
        {
            return Failed("MaterialsArchiveInvalid", "材料归档的清单与条目数量不一致。");
        }

        foreach (var declared in manifest.Files)
        {
            var path = NormalizeEntryPath(declared.Path);
            if (!IsValidEntryPath(path) || path == ManifestFileName ||
                !files.TryGetValue(path, out var content) || content.LongLength != declared.Length ||
                !StringComparer.Ordinal.Equals(
                    Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), declared.Sha256))
            {
                return Failed("MaterialsArchiveInvalid", $"材料内容与清单不符：{declared.Path}");
            }
        }

        return new PaperMaterialsArchiveResult(true, null,
            new InMemoryMaterialFileSource(files), manifest);
    }

    /// <summary>
    /// 把已校验的归档解出成**可编辑的材料包目录**：目录里就是清单、题目 `.tex`、共享样式、图片与独立答案，
    /// 可直接用于再次检查与修订。归档自身的 `materials.json` 不写出（它不是材料包的一部分）。
    /// 目标目录必须不存在；解出后按既有限制只向当前账户与 SYSTEM 开放。
    /// </summary>
    public static async Task ExtractAsync(PaperMaterialsArchiveResult archive, string targetDirectory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (!archive.Succeeded || archive.Materials is null)
            throw new InvalidDataException("材料归档不可用：" + (archive.Failure?.Value ?? "未知原因"));
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        var target = Path.GetFullPath(targetDirectory);
        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException("材料输出目录已存在，请选择新的目录。");
        var parent = Path.GetDirectoryName(target) ?? throw new ArgumentException("材料输出目录无效。");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("材料输出目录的父目录不存在。");
        ExamImportWorkspace.RejectLinks(parent);
        var staging = Path.Combine(parent, ".c05-materials-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        ExamImportWorkspace.RestrictAccess(staging);
        try
        {
            foreach (var path in archive.Materials.Paths)
            {
                ct.ThrowIfCancellationRequested();
                if (string.Equals(path, ManifestFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var output = Path.GetFullPath(Path.Combine(staging, path.Replace('/', Path.DirectorySeparatorChar)));
                if (!output.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"材料路径越界：{path}");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await File.WriteAllBytesAsync(output, archive.Materials.Read(path), ct);
            }

            ct.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static void WriteEntry(ZipArchive zip, string path, byte[] content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        // 固定时间戳：同样的材料必然得到同样的字节。
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static PaperMaterialsArchiveResult Failed(string code, string message) =>
        new(false, new KeyValuePair<string, string>(code, message), null, null);

    private static string NormalizeEntryPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.Replace('\\', '/').Trim();
    }

    private static bool IsValidEntryPath(string path) =>
        path.Length is > 0 and <= MaxPathLength &&
        PackagePathRules.IsRelativeAndInside(path) &&
        PackagePathRules.HasValidWindowsSegments(path);

    private static bool IsTextLike(string path) =>
        TextExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
}

/// <summary>归档结果：失败时给出稳定错误码与说明；成功时给出材料集合与清单。</summary>
public sealed record PaperMaterialsArchiveResult(
    bool Succeeded,
    KeyValuePair<string, string>? Failure,
    IMaterialFileSource? Materials,
    PaperMaterialsManifest? Manifest);

/// <summary>归档内的材料清单（`materials.json`）。</summary>
public sealed record PaperMaterialsManifest(
    int FormatVersion,
    string ExamId,
    long PaperVersion,
    IReadOnlyList<PaperMaterialsEntry> Files);

/// <summary>清单里的一条材料：相对路径、字节长度与内容 SHA-256。</summary>
public sealed record PaperMaterialsEntry(string Path, long Length, string Sha256);
