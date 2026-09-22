namespace Exam.Domain.Importing;

/// <summary>
/// Windows 文件名合法性规则。依据 requirements.md「创建考试时检查名称能否用于 Windows 文件名」。
/// 这里手工实现而**不使用 Path.GetInvalidFileNameChars()**：该 API 的取值随运行平台变化
/// （例如 ':' 在 Linux 上合法），而本系统要求在任何平台上按 Windows 规则判定。
/// </summary>
public static class WindowsFileNameRules
{
    public const int MaxLength = 255;

    private static readonly char[] ForbiddenCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static bool IsValidFileName(string? candidate) => Describe(candidate) is null;

    /// <summary>返回不可用的中文原因；合法时返回 null。</summary>
    public static string? Describe(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return "文件名为空。";
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return "文件名不能只由空白字符组成。";
        }

        if (candidate.Length > MaxLength)
        {
            return $"文件名长度不能超过 {MaxLength} 个字符。";
        }

        foreach (var character in candidate)
        {
            if (char.IsControl(character))
            {
                return $"文件名不能包含控制字符（0x{(int)character:X2}）。";
            }

            if (ForbiddenCharacters.Contains(character))
            {
                return $"文件名不能包含字符“{character}”。";
            }
        }

        if (candidate[^1] == ' ' || candidate[^1] == '.')
        {
            return "文件名不能以空格或句点结尾。";
        }

        var stem = candidate;
        var dot = stem.IndexOf('.');
        if (dot >= 0)
        {
            stem = stem[..dot];
        }

        stem = stem.TrimEnd(' ', '.');

        if (stem.Length == 0)
        {
            return "文件名不能只由句点组成。";
        }

        if (ReservedNames.Contains(stem))
        {
            return $"“{stem}”是 Windows 保留设备名，不能用作文件名。";
        }

        return null;
    }
}
