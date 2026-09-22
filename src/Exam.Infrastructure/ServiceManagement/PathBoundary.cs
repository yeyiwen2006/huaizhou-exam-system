namespace Exam.Infrastructure.ServiceManagement;

internal static class PathBoundary
{
    public static string ResolveWithinRoot(string root, string requested, string errorMessage)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullRequested = Path.GetFullPath(requested);
        var relative = Path.GetRelativePath(fullRoot, fullRequested);
        if (relative == ".." || Path.IsPathFullyQualified(relative) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(errorMessage);
        }

        RejectExistingReparsePoints(fullRoot, fullRequested);
        return fullRequested;
    }

    public static void RejectExistingReparsePoints(string fullRoot, string fullRequested)
    {
        var root = Path.GetFullPath(fullRoot);
        var requested = Path.GetFullPath(fullRequested);
        InspectExistingPathComponents(root);

        var relative = Path.GetRelativePath(root, requested);
        if (relative == ".")
        {
            return;
        }

        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            InspectExistingPath(current);
        }
    }

    private static void InspectExistingPathComponents(string path)
    {
        var root = Path.GetPathRoot(path)
            ?? throw new InvalidOperationException("无法确定受控目录所在的磁盘根路径。");
        var current = root;
        InspectExistingPath(current);
        foreach (var segment in path[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            InspectExistingPath(current);
        }
    }

    private static void InspectExistingPath(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException($"受控目录不允许经过符号链接或目录联接：{path}");
        }
    }
}
