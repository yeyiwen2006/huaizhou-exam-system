namespace Exam.Domain.Importing;

/// <summary>
/// 包内相对路径规则，与 schemas/exam-manifest.schema.json 的 relativePath 正则等价：
/// 禁止盘符、禁止绝对路径、禁止 .. 逃出包根目录。等价性由单元测试用共享用例表核对。
/// </summary>
public static class PackagePathRules
{
    public const int MaxLength = 512;

    /// <summary>是否为包内相对路径且不越出包根目录。</summary>
    public static bool IsRelativeAndInside(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (path[0] == '/' || path[0] == '\\')
        {
            return false;
        }

        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
        {
            return false;
        }

        foreach (var segment in path.Split(['/', '\\']))
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 按 Windows 规则检查每段目录或文件名；与只检查相对路径形状的 IsRelativeAndInside 分开。
    /// 这不是文件系统边界证明；符号链接、联接点与实际依赖的解析仍须由 D02 在访问时检查。
    /// </summary>
    internal static bool HasValidWindowsSegments(string path) =>
        path.Split(['/', '\\']).All(segment =>
            segment.Length == 0 || segment == "." || WindowsFileNameRules.IsValidFileName(segment));

    /// <summary>统一分隔符为 /，合并重复分隔符并去掉开头的 ./；不做越界判断。</summary>
    public static string Normalize(string path)
    {
        var unified = path.Replace('\\', '/');
        var segments = new List<string>();
        foreach (var segment in unified.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }
}
