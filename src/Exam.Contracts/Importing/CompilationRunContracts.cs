namespace Exam.Contracts.Importing;

/// <summary>
/// TeX 发行版。两者的自动安装与外部命令开关不同，参数由 Domain 的 XeLaTeXInvocation 统一给出：
/// MiKTeX 需要显式关闭包安装器与 \write18；TeX Live 本身不会自动安装。
/// </summary>
public enum XeLaTeXPlatform
{
    MiKTeX,
    TeXLive
}

/// <summary>
/// 一次逐题编译的输入。PackageRoot 为导入包根目录（也是 XeLaTeX 的工作目录，
/// 这样清单与 \graphicspath 里的相对路径才能按包内结构解析）；
/// OutputDirectory 必须是包根之外的独立目录，编译产物不写回材料目录。
/// </summary>
public sealed record TeXCompileRequest(
    string PackageRoot,
    string SourceRelativePath,
    string OutputDirectory,
    TimeSpan? Timeout = null);

/// <summary>
/// 一次编译的结果。Succeeded 已综合「退出码、是否超时、是否写出 PDF、有无阻断性问题」，
/// 调用方不要再单独用 ExitCode 判断（实测缺图片时退出码为 0、缺字体时仍会写出 PDF）。
/// </summary>
public sealed record TeXCompileResult(
    bool Succeeded,
    int ExitCode,
    bool TimedOut,
    TeXCompileReport Report,
    string? OutputPdfPath,
    IReadOnlyList<TeXIssue> Issues)
{
    public static TeXCompileResult NotStarted(TeXIssue issue) =>
        new(false, -1, false, new TeXCompileReport(false, 0, null, [issue], 0), null, [issue]);
}
