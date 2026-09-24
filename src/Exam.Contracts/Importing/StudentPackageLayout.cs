namespace Exam.Contracts.Importing;

/// <summary>D03 与学生读取器共用的 ZIP 布局、路径、文件头和容量约定，不包含答案或坐标。</summary>
public static class StudentPackageLayout
{
    public const int MaxContentBytes = 256 * 1024 * 1024;
    public const int MaxMetadataBytes = 8 * 1024 * 1024;
    public const int MaxEntryCount = 10000;

    private static readonly string[] ServerOnlyNames = ["答案", "解析", "answer", "solution"];
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>输入须先通过 D01 校验；PDF 仍放在 questions/ 下。</summary>
    public static string QuestionPdfPath(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var name = source.Replace('\\', '/').Split('/')[^1];
        var extension = name.LastIndexOf('.');
        return "questions/" + (extension < 0 ? name : name[..extension]) + ".pdf";
    }

    public static bool IsSafePath(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length <= 512 &&
        !path.Contains('\\') && !path.Any(c => char.IsControl(c) || ":*?\"<>|".Contains(c)) &&
        path.Split('/').All(part => part.Length is > 0 and <= 255 && part is not ("." or "..") &&
            !part.EndsWith(' ') && !part.EndsWith('.') && !ReservedNames.Contains(part.Split('.')[0]));

    /// <summary>只是文件级边界，不代替服务器对正文、答案及逐文件资源来源的核查。</summary>
    public static bool IsDisplayPath(string? path) => IsSafePath(path) &&
        !path!.Split('/').Contains("server-only", StringComparer.OrdinalIgnoreCase) &&
        !ServerOnlyNames.Any(name => path.Contains(name, StringComparison.OrdinalIgnoreCase)) &&
        Path.GetExtension(path).ToLowerInvariant() is ".pdf" or ".png" or ".jpg" or ".jpeg";

    public static bool HasDisplaySignature(string path, ReadOnlySpan<byte> bytes) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => bytes.StartsWith("%PDF-"u8),
        ".png" => bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        ".jpg" or ".jpeg" => bytes.StartsWith(new byte[] { 255, 216, 255 }),
        _ => false
    };
}
