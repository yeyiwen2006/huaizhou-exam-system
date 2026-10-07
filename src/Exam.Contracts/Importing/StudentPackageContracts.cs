namespace Exam.Contracts.Importing;

/// <summary>学生端组包的问题类别。稳定取值，客户端不解析中文文案分支。</summary>
public enum StudentPackageIssueCode
{
    None,

    /// <summary>清单不可读或缺少必需字段。</summary>
    MetadataInvalid,

    /// <summary>找不到某道题编译好的 PDF。</summary>
    QuestionPdfMissing,

    /// <summary>找不到题目引用的图片。</summary>
    RequiredImageMissing,

    /// <summary>清单声明的资源目录缺失。</summary>
    ResourceRootMissing,

    /// <summary>需要打包的文件被允许清单挡下——说明它本不该被需要。</summary>
    RequiredResourceRejected,

    /// <summary>组包过程中发现答案或解析内容，必须停止。</summary>
    AnswerResourceDetected,

    /// <summary>路径越出包根目录或经过重解析点。</summary>
    PathEscaped,

    /// <summary>清单里没有题目。</summary>
    NoQuestions,

    /// <summary>加密失败。</summary>
    EncryptionFailed,

    /// <summary>提供了选项坐标产物，但它与本包的考试、试卷版本或题目选项集合不一致。</summary>
    OptionRegionInvalid
}

/// <summary>一条组包问题。Path 为包内相对路径（能确定时给出）。</summary>
public sealed record StudentPackageIssue(StudentPackageIssueCode Code, string Message, string? Path = null);

/// <summary>被排除的文件及原因，供 试题修订 的导入预览逐条展示。</summary>
public sealed record StudentPackageExclusion(string Path, string Reason);

/// <summary>
/// 组包计划：进包的文件与被挡下的文件。试题修订 的导入预览核对题量、题型、答案与选项绑定后，
/// 与本计划一起交给使用者确认。
/// </summary>
public sealed record StudentPackagePlan(
    string ExamId,
    long PaperVersion,
    IReadOnlyList<string> IncludedPaths,
    IReadOnlyList<StudentPackageExclusion> Excluded)
{
    /// <summary>学生端包明文内的元数据文件名（无答案）。</summary>
    public const string MetadataFileName = "manifest.json";
}
