using Exam.Contracts.Importing;
using Exam.Domain.Importing;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Infrastructure.Compilation;

/// <summary>
/// 包内文件与依赖的存在性检查。执行指南第 164 行要求「检查字体、宏包和图片，缺项须定位报告」，
/// 其中源文件是否存在、图片能否解析、资源目录是否存在属于文件系统侧，在此实现；
/// 宏包与字体是否可用仍以编译日志为准（见 <see cref="XeLaTeXCompiler"/>）。
///
/// 所有路径都经 PathBoundary 校验：越出包根、或经过**符号链接/目录联接（重解析点）** 的路径一律拒绝 ——
/// 重解析点和依赖访问必须同时进行文件系统边界检查。
/// 本类型只读，不修改包内容。
/// </summary>
public sealed class PackageContentChecker
{
    /// <summary>编译前检查整个包，避免嵌套 TeX、样式或图片通过目录联接逃出边界。</summary>
    public IReadOnlyList<TeXIssue> CheckCompilationInputs(string packageRoot)
    {
        var root = RequireDirectory(packageRoot);
        var issues = new List<TeXIssue>();
        var pending = new Stack<string>();
        pending.Push(root);
        var entries = 0;
        while (pending.TryPop(out var directory))
        {
            PathBoundary.ResolveWithinRoot(root, directory, "导入包目录越界。");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > 10000)
                {
                    issues.Add(new TeXIssue(TeXIssueCode.ResourceLimit, "导入包超过 10000 个文件或目录。"));
                    return issues;
                }

                PathBoundary.ResolveWithinRoot(root, path, "导入包文件越界。");
                if (Directory.Exists(path))
                {
                    pending.Push(path);
                    continue;
                }

                var extension = Path.GetExtension(path);
                if (!new[] { ".tex", ".sty", ".cls", ".def", ".cfg" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, path);
                if (new FileInfo(path).Length > XeLaTeXCompiler.MaxLogBytes)
                {
                    issues.Add(new TeXIssue(TeXIssueCode.ResourceLimit, "TeX 源文件超过 8 MiB。", relative));
                    continue;
                }

                var declaration = TeXSourceInspector.Inspect(File.ReadAllText(path, System.Text.Encoding.UTF8));
                issues.AddRange(declaration.Violations.Select(issue => issue with
                {
                    Message = $"{relative}：{issue.Message}"
                }));
            }
        }

        return issues;
    }

    /// <summary>
    /// 逐条图片引用按 \graphicspath 的顺序尝试解析；没有一条候选存在时报 MissingPicture，
    /// 并把尝试过的候选路径一并给出，便于定位。
    /// </summary>
    public IReadOnlyList<TeXIssue> CheckImages(string packageRoot, TeXSourceDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        var root = RequireDirectory(packageRoot);
        var issues = new List<TeXIssue>();

        foreach (var reference in declaration.ImageReferences)
        {
            var candidates = TeXSourceInspector.ResolveImageCandidates(reference, declaration.GraphicsRoots);
            var found = false;

            foreach (var candidate in candidates)
            {
                if (TryResolveFile(root, candidate, out _))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                issues.Add(new TeXIssue(
                    TeXIssueCode.MissingPicture,
                    $"包内找不到图片 {reference}；已尝试：{string.Join('、', candidates)}。",
                    reference));
            }
        }

        return issues;
    }

    /// <summary>逐题源文件必须存在于包内，且路径不得越出包根或经过重解析点。</summary>
    public TeXIssue? CheckSourceExists(string packageRoot, string sourceRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);
        var root = RequireDirectory(packageRoot);

        if (TryResolveFile(root, sourceRelativePath, out _))
        {
            return null;
        }

        return new TeXIssue(
            TeXIssueCode.MissingFile,
            $"包内找不到题目源文件：{sourceRelativePath}。",
            sourceRelativePath);
    }

    /// <summary>清单声明的资源目录必须存在且确实是目录。</summary>
    public IReadOnlyList<TeXIssue> CheckResourceRoots(string packageRoot, IEnumerable<string> resourceRoots)
    {
        ArgumentNullException.ThrowIfNull(resourceRoots);
        var root = RequireDirectory(packageRoot);
        var issues = new List<TeXIssue>();

        foreach (var resourceRoot in resourceRoots)
        {
            if (string.IsNullOrWhiteSpace(resourceRoot))
            {
                continue;
            }

            try
            {
                var resolved = PathBoundary.ResolveWithinRoot(
                    root,
                    Path.Combine(root, resourceRoot),
                    $"资源目录越出导入包根目录：{resourceRoot}");

                if (!Directory.Exists(resolved))
                {
                    issues.Add(new TeXIssue(
                        TeXIssueCode.MissingFile,
                        $"清单声明的资源目录不存在或不是目录：{resourceRoot}。",
                        resourceRoot));
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                issues.Add(new TeXIssue(
                    TeXIssueCode.MissingFile,
                    $"资源目录不被接受：{exception.Message}",
                    resourceRoot));
            }
        }

        return issues;
    }

    /// <summary>
    /// 在包根内解析一个相对路径。越出包根或经过重解析点时返回 false —— 这两种情况都不能当作
    /// 「包内存在」，否则符号链接会把包外的文件带进导入流程。
    /// </summary>
    private static bool TryResolveFile(string root, string candidate, out string? resolved)
    {
        resolved = null;
        try
        {
            var full = PathBoundary.ResolveWithinRoot(
                root,
                Path.Combine(root, candidate),
                $"路径越出导入包根目录或经过重解析点：{candidate}");

            if (!File.Exists(full))
            {
                return false;
            }

            resolved = full;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string RequireDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"导入包根目录不存在：{path}");
        }

        return Path.GetFullPath(path);
    }
}
