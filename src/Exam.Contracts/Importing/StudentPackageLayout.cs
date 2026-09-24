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

    /// <summary>输入是已经通过 D01 校验的题目 source；PDF 放在 ZIP 的 questions/ 下。</summary>
    public static string QuestionPdfPath(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var name = source.Replace('\\', '/').Split('/')[^1];
        var extension = name.LastIndexOf('.');
        return "questions/" + (extension < 0 ? name : name[..extension]) + ".pdf";
    }
}
