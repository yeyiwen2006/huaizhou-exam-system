using System.Globalization;

namespace Exam.Domain.Importing;

/// <summary>
/// 逐题 TeX 文件名规则。依据 requirements.md「命名为“考试名称_题号.tex”，以最后一个下划线后的数字识别题号」，
/// 允许前导零，扩展名大小写不敏感，前导零以外的非数字一律不识别。
/// </summary>
public static class QuestionSourceFileName
{
    public const string Extension = ".tex";

    public static bool TryParse(string? packageRelativePath, out string prefix, out int questionNo)
    {
        prefix = string.Empty;
        questionNo = 0;

        if (string.IsNullOrEmpty(packageRelativePath))
        {
            return false;
        }

        var normalized = packageRelativePath.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        var fileName = separator >= 0 ? normalized[(separator + 1)..] : normalized;

        if (!fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stem = fileName[..^Extension.Length];
        var underscore = stem.LastIndexOf('_');

        // 前缀不能为空：文件名必须形如「考试名称_题号.tex」。
        if (underscore <= 0)
        {
            return false;
        }

        var digits = stem[(underscore + 1)..];
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
        {
            return false;
        }

        prefix = stem[..underscore];
        questionNo = parsed;
        return true;
    }
}
