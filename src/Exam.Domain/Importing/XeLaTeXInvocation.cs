using Exam.Contracts.Importing;

namespace Exam.Domain.Importing;

/// <summary>编译结果判定：成功与阻断性问题。纯逻辑，不启动进程、不读文件。</summary>
public sealed record XeLaTeXOutcome(bool Succeeded, IReadOnlyList<TeXIssue> Issues);

/// <summary>
/// XeLaTeX 的命令行参数与成功判定。纯逻辑，便于在没有 TeX 的环境下用单元测试钉住行为。
///
/// 两个发行版的隔离开关不同：
/// - MiKTeX 默认允许按需安装缺失宏包，必须用 -disable-installer 关掉，
///   否则「导入包不得联网取依赖」会被悄悄破坏；它还需要 -disable-write18 彻底关闭 \write18。
/// - TeX Live 本身不会自动安装，不需要也不接受上面两个参数。
/// 两者都支持 -no-shell-escape，作为禁止外部命令的第二道保险。
/// </summary>
public static class XeLaTeXInvocation
{
    public const string InteractionMode = "-interaction=nonstopmode";

    public static IReadOnlyList<string> BuildArguments(
        XeLaTeXPlatform platform,
        string sourceRelativePath,
        string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (!PackagePathRules.IsRelativeAndInside(sourceRelativePath)
            || !PackagePathRules.HasValidWindowsSegments(sourceRelativePath)
            || !sourceRelativePath.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("源文件必须是包内有效的相对 .tex 路径。", nameof(sourceRelativePath));
        }

        var arguments = new List<string>();

        switch (platform)
        {
            case XeLaTeXPlatform.MiKTeX:
                arguments.Add("-disable-installer");
                arguments.Add("-disable-write18");
                arguments.Add("-dont-parse-first-line");
                break;
            case XeLaTeXPlatform.TeXLive:
                arguments.Add("-no-parse-first-line");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(platform), platform, "未知的 TeX 发行版。");
        }

        arguments.Add("-no-shell-escape");
        arguments.Add(InteractionMode);

        // 编译产物写到独立目录：材料目录必须保持零改动。
        arguments.Add($"-output-directory={outputDirectory}");

        // 相对路径按包根目录解析，因此必须以包根目录为工作目录运行。
        // ./ 防止根目录下以 - 或 & 开头的合法文件名被解释成选项或格式命令。
        arguments.Add("./" + PackagePathRules.Normalize(sourceRelativePath));
        return arguments;
    }

    /// <summary>
    /// 综合判定一次编译是否成功。**不能只看退出码**：实测缺图片时退出码为 0、缺字体时仍会写出 PDF，
    /// 因此必须同时满足「未超时、退出码为 0、日志无阻断性问题、确实写出了 PDF」。
    /// </summary>
    public static XeLaTeXOutcome Evaluate(
        int exitCode,
        bool timedOut,
        bool outputPdfProduced,
        TeXCompileReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var issues = new List<TeXIssue>();

        if (timedOut)
        {
            issues.Add(new TeXIssue(
                TeXIssueCode.Timeout,
                "编译超过时限被终止。请检查依赖是否在等待输入或试图访问网络。"));
        }

        issues.AddRange(report.BlockingIssues);

        if (!timedOut && exitCode != 0)
        {
            issues.Add(new TeXIssue(
                TeXIssueCode.ProcessFailed,
                $"XeLaTeX 返回非零退出码 {exitCode}。"));
        }

        var outputMissing = !report.HasOutput || !outputPdfProduced;
        if (outputMissing)
        {
            issues.Add(new TeXIssue(
                TeXIssueCode.OutputMissing,
                "编译结束但没有写出 PDF。缺依赖或致命错误都会导致这种情况，不得按半份试卷发布。"));
        }

        var succeeded = !timedOut && exitCode == 0 && report.IsClean && !outputMissing;
        return new XeLaTeXOutcome(succeeded, Deduplicate(issues));
    }

    private static List<TeXIssue> Deduplicate(IEnumerable<TeXIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<TeXIssue>();
        foreach (var issue in issues)
        {
            if (seen.Add($"{issue.Code}|{issue.File}|{issue.Message}"))
            {
                result.Add(issue);
            }
        }

        return result;
    }
}
