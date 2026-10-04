using Exam.Domain.Importing;

namespace Exam.Infrastructure.Packaging;

/// <summary>
/// 以**本地目录**为材料的只读集合（预览提示用：把当前材料与上一版本归档按同一算法比较）。
/// 路径相对材料包根、`/` 分隔；符号链接与目录联接一律拒绝。
/// </summary>
public sealed class DirectoryMaterialFileSource : IMaterialFileSource
{
    private readonly string _root;
    private readonly Dictionary<string, string> _byNormalizedPath;

    private DirectoryMaterialFileSource(string root, Dictionary<string, string> byNormalizedPath)
    {
        _root = root;
        _byNormalizedPath = byNormalizedPath;
        Paths = byNormalizedPath.Values.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<string> Paths { get; }

    public static DirectoryMaterialFileSource Create(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("材料目录不存在：需要已检查过的材料包目录。");
        }

        var root = Path.GetFullPath(directory);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((root, 0));
        var count = 0;
        while (stack.Count > 0)
        {
            var (folder, depth) = stack.Pop();
            ExamImportWorkspace.RejectLinks(folder);
            if (depth > 32)
            {
                throw new InvalidDataException("材料目录层级过深。");
            }

            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                ExamImportWorkspace.RejectLinks(path);
                if (++count > PaperMaterialsArchive.MaxEntries)
                {
                    throw new InvalidDataException("材料文件数量超过归档上限，无法比较。");
                }

                if (Directory.Exists(path))
                {
                    stack.Push((path, depth + 1));
                    continue;
                }

                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (!files.TryAdd(relative, relative))
                {
                    throw new InvalidDataException($"材料目录里存在忽略大小写的重名文件：{relative}");
                }
            }
        }

        return new DirectoryMaterialFileSource(root, files);
    }

    public string? Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var normalized = InMemoryMaterialFileSource.Normalize(relativePath);
        return _byNormalizedPath.TryGetValue(normalized, out var canonical) ? canonical : null;
    }

    public byte[] Read(string canonicalPath)
    {
        var path = Path.Combine(_root, canonicalPath.Replace('/', Path.DirectorySeparatorChar));
        ExamImportWorkspace.RejectLinks(path);
        return File.ReadAllBytes(path);
    }
}
