namespace Exam.Contracts.Importing;

/// <summary>
/// D03 ZIP 的现有文件布局与读写双方共同执行的容量边界。
/// 仅约定文件和题目标识，不定义 B03 的显示控件或选项坐标。
/// </summary>
public static class StudentPackageLayout
{
    public const int MaxContentBytes = 256 * 1024 * 1024;
    public const int MaxMetadataBytes = 8 * 1024 * 1024;
    public const int MaxEntryCount = 10000;
    public const int MaxPathLength = 512;

    private static readonly string[] AnswerKeywords = ["答案", "解析", "answer", "solution"];

    public static bool IsSafePath(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length <= MaxPathLength &&
        !path.Contains('\\') && !path.Any(c => char.IsControl(c) || ":*?\"<>|".Contains(c)) &&
        path.Split('/').All(ValidSegment);

    private static bool ValidSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment) || segment.Length > 255 || segment.EndsWith(' ') || segment.EndsWith('.')) return false;
        var stem = segment.Split('.')[0].TrimEnd(' ', '.').ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL") &&
            !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                "123456789¹²³".Contains(stem[3]));
    }

    public static bool IsDisplayPath(string? path) => IsSafePath(path) &&
        !path!.Split('/').Contains("server-only", StringComparer.OrdinalIgnoreCase) &&
        !AnswerKeywords.Any(keyword => path.Contains(keyword, StringComparison.OrdinalIgnoreCase)) &&
        Path.GetExtension(path).ToLowerInvariant() is ".pdf" or ".png" or ".jpg" or ".jpeg";

    public static bool HasDisplaySignature(string path, ReadOnlySpan<byte> bytes) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => bytes.StartsWith("%PDF-"u8),
        ".png" => bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        ".jpg" or ".jpeg" => bytes.StartsWith(new byte[] { 255, 216, 255 }),
        _ => false
    };

    /// <summary>输入是已经通过 D01 校验的题目 source；PDF 放在 ZIP 的 questions/ 下。</summary>
    public static string QuestionPdfPath(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var name = source.Replace('\\', '/').Split('/')[^1];
        var extension = name.LastIndexOf('.');
        return "questions/" + (extension < 0 ? name : name[..extension]) + ".pdf";
    }
}
