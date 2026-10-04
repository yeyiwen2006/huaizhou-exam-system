namespace Exam.Domain.Importing;

/// <summary>
/// 逐题内容摘要要读的**只读材料集合**：归档内规范相对路径（`/` 分隔）、按路径解析与读取。
///
/// 解析一律**大小写不敏感**：材料来自 Windows，归档与磁盘上的大小写可能不一致，而正则解析出的引用按原样保留。
/// 参与哈希的路径一律用 <see cref="Resolve"/> 返回的**规范名**，避免大小写差异被当成内容变化。
/// </summary>
public interface IMaterialFileSource
{
    /// <summary>归档内所有规范相对路径，ordinal 排序（顺序稳定，便于逐条比对与测试）。</summary>
    IReadOnlyList<string> Paths { get; }

    /// <summary>把引用解析为归档内的规范路径；大小写不敏感，找不到返回 null。</summary>
    string? Resolve(string relativePath);

    /// <summary>按**规范路径**读取字节；调用方应先用 <see cref="Resolve"/> 判定存在。</summary>
    byte[] Read(string canonicalPath);
}

/// <summary>内存材料集合：服务器解出的归档与用例里的合成材料共用同一实现。</summary>
public sealed class InMemoryMaterialFileSource : IMaterialFileSource
{
    private readonly Dictionary<string, byte[]> _files;

    public InMemoryMaterialFileSource(IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, content) in files)
        {
            var normalized = Normalize(path);
            if (normalized.Length == 0 || content is null)
            {
                throw new ArgumentException("材料路径不能为空，内容不能为 null。", nameof(files));
            }

            // 同一归档里只允许一个大小写形态（打包端按 ordinal 去重，这里守住读侧）。
            if (!_files.TryAdd(normalized, content))
            {
                throw new ArgumentException($"材料集合里存在重复路径（忽略大小写）：{path}", nameof(files));
            }
        }

        Paths = _files.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<string> Paths { get; }

    public string? Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var normalized = Normalize(relativePath);
        return _files.ContainsKey(normalized) ? normalized : null;
    }

    public byte[] Read(string canonicalPath)
    {
        var normalized = Normalize(canonicalPath);
        return _files.TryGetValue(normalized, out var content)
            ? content
            : throw new KeyNotFoundException($"材料集合里没有这个文件：{canonicalPath}");
    }

    /// <summary>把路径归一为归档内规范形态：反斜杠转 `/`、去掉前导 `./` 与结尾 `/`，但**不改大小写**。</summary>
    public static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimEnd('/');
    }
}
