namespace Exam.Contracts.Importing;

/// <summary>
/// 编译与依赖问题的稳定类别。C05 的导入预览、C06 的页面与 E 的 CLI 据此分类展示；
/// 客户端不解析中文文案分支。
/// </summary>
public enum TeXIssueCode
{
    /// <summary>宏包或文档类缺失（.sty/.cls 未安装）。</summary>
    MissingPackage,

    /// <summary>字体未安装或名称有误。禁止静默替换字体。</summary>
    MissingFont,

    /// <summary>题目引用的图片或图形文件缺失。</summary>
    MissingPicture,

    /// <summary>其它被引用的文件缺失（.tex、.pdf 等）。</summary>
    MissingFile,

    /// <summary>源文件试图执行外部命令（\write18 等），已按禁外部命令处理。</summary>
    ShellEscapeAttempt,

    /// <summary>其它排版错误（语法、未定义命令等）。</summary>
    SourceError,

    /// <summary>编译超过时限被终止。</summary>
    Timeout,

    /// <summary>找不到 XeLaTeX 可执行文件或工具链不可用。</summary>
    ToolchainMissing,

    /// <summary>编译结束但没有写出 PDF。</summary>
    OutputMissing,

    /// <summary>进程返回非零退出码。</summary>
    ProcessFailed,

    /// <summary>由前述错误引起的连带失败（如 Emergency stop、图形 Division by 0），不作为独立缺陷。</summary>
    Cascade,

    /// <summary>版式溢出警告（Overfull/Underfull box），不阻止发布。</summary>
    OverfullBox
}

/// <summary>
/// 一条编译或依赖问题。File 为缺失的文件名或出错时正在处理的文件，
/// Line 为该文件中的行号（能从日志确定时给出）。
/// </summary>
public sealed record TeXIssue(
    TeXIssueCode Code,
    string Message,
    string? File = null,
    int? Line = null,
    bool IsBlocking = true);

/// <summary>
/// XeLaTeX 一次编译的日志摘要。**成功判定不能只看退出码**：实测缺图片时 XeLaTeX 退出码为 0、
/// 缺字体时仍会写出 PDF，因此必须同时满足「退出码为 0、写出了 PDF、且没有任何阻断性问题」。
/// </summary>
public sealed record TeXCompileReport(
    bool HasOutput,
    int PageCount,
    string? OutputFileName,
    IReadOnlyList<TeXIssue> Issues,
    int OverfullBoxCount)
{
    /// <summary>阻断性问题：会阻止发布完整试卷的问题。</summary>
    public IReadOnlyList<TeXIssue> BlockingIssues =>
        Issues.Where(issue => issue.IsBlocking).ToList();

    /// <summary>日志本身没有发现阻断性问题且写出了 PDF；是否成功还须由调用方核对进程退出码。</summary>
    public bool IsClean => HasOutput && BlockingIssues.Count == 0;
}

/// <summary>
/// 从逐题 TeX 源文件静态读出的声明。仅做解析，不判断文件是否存在（存在性属编译与依赖检查阶段）。
/// </summary>
public sealed record TeXSourceDeclaration(
    string DocumentClass,
    IReadOnlyList<string> Packages,
    IReadOnlyList<string> Fonts,
    IReadOnlyList<string> GraphicsRoots,
    IReadOnlyList<string> ImageReferences,
    IReadOnlyList<TeXIssue> Violations);
