using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Exam.Contracts.Importing;

namespace Exam.Domain.Importing;

/// <summary>
/// 把 XeLaTeX 的编译日志读成可定位的问题清单。纯文本处理：不启动进程、不读文件、不联网。
///
/// 依据实测（MiKTeX 26.5 / XeTeX 4.18）确定的几个事实：
/// 1. **不能只看退出码**：缺图片时 XeLaTeX 退出码为 0，缺字体时仍会写出 PDF。
///    因此成功必须同时满足「退出码 0 + 写出了 PDF + 无阻断性问题」，退出码由调用方核对。
/// 2. TeX 写日志时按列宽硬折行，**折行处不增删字符**，把折行拼回原样只需直接相接，不能补空格
///    （`not lo` + `adable:` 要还原成 `not loadable:`）。
/// 3. 一条消息的续行带 `(fontspec)` 这类前缀，须先剥掉。
/// 4. 缺图片会同时产生 `LaTeX Warning: File ... not found on input line N.` 和
///    `! Unable to load picture or PDF file '...'`，是同一个缺陷，按文件名去重后只报一条。
/// 5. 缺依赖之后的 `! Emergency stop.`、`Package graphics Error: Division by 0.`、
///    `Fatal error occurred`、`! Font ... not loadable` 都是同一个缺陷的连带失败，
///    标记为 Cascade 并排除在阻断性问题之外，避免一次缺依赖报出一串无关错误。
/// </summary>
public static class XeLaTeXLogReader
{
    private static readonly Regex ContinuationPrefix =
        new(@"^\([A-Za-z@._-]+\)\s*", RegexOptions.Compiled);

    private static readonly Regex OutputWritten =
        new(@"Output written on \S*?(?<name>[^\\/:]+\.pdf)\s*\(\s*(?<pages>\d+)\s*pages?\s*\)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FileNotFoundWarning =
        new(@"LaTeX Warning: File `(?<file>[^']+)' not found on input line (?<line>\d+)\.",
            RegexOptions.Compiled);

    private static readonly Regex UnableToLoadPicture =
        new(@"! Unable to load picture or PDF file '(?<file>[^']+)'\.", RegexOptions.Compiled);

    private static readonly Regex MissingPackage =
        new(@"! LaTeX Error: File `(?<file>[^']+)' not found\.", RegexOptions.Compiled);

    private static readonly Regex FontspecFontMissing =
        new(@"The font ""(?<font>[^""]+)"" cannot be found", RegexOptions.Compiled);

    private static readonly Regex FontNotLoadableName =
        new(@"=""(?<font>[^""]+)"" at [\d.]+pt\s*not loadable", RegexOptions.Compiled);

    private static readonly Regex FontNotLoadableSignature =
        new(@"not loadable:\s*Metric \(TFM\) file or installed font not found", RegexOptions.Compiled);

    private static readonly Regex ShellEscapeDisabled =
        new(@"runsystem\((?<command>.+?)\)\.\.\.disabled", RegexOptions.Compiled);

    private static readonly Regex Overfull =
        new(@"(?:Overfull|Underfull) \\[hv]box", RegexOptions.Compiled);

    /// <summary>连带失败：这些错误由前面的缺依赖或致命错误引起，不作为独立缺陷上报。</summary>
    private static readonly Regex[] CascadePatterns =
    [
        new(@"^! Emergency stop\.", RegexOptions.Compiled),
        new(@"^! Package graphics Error: Division by 0\.", RegexOptions.Compiled),
        new(@"^! ==> Fatal error occurred", RegexOptions.Compiled),
        new(@"^! Interruption\.", RegexOptions.Compiled),
        new(@"^! Font .*not loadable", RegexOptions.Compiled),
        new(@"^! Illegal unit of measure", RegexOptions.Compiled)
    ];

    public static TeXCompileReport Read(string? logText)
    {
        if (string.IsNullOrWhiteSpace(logText))
        {
            return new TeXCompileReport(false, 0, null, [], 0);
        }

        var log = Fold(logText);
        var issues = new List<TeXIssue>();

        var hasOutput = false;
        var pageCount = 0;
        string? outputFileName = null;

        // 1. 输出与页数：找到即说明写出了 PDF，但不代表编译无误。
        foreach (Match match in OutputWritten.Matches(log))
        {
            hasOutput = true;
            pageCount = int.Parse(match.Groups["pages"].Value, CultureInfo.InvariantCulture);
            outputFileName = match.Groups["name"].Value;
        }

        // 2. 缺依赖。
        issues.AddRange(CollectMissingImages(log));
        issues.AddRange(CollectMissingPackages(log));
        issues.AddRange(CollectMissingFonts(log));

        foreach (Match match in ShellEscapeDisabled.Matches(log))
        {
            issues.Add(new TeXIssue(
                TeXIssueCode.ShellEscapeAttempt,
                $"源文件试图执行外部命令，已被禁止：{match.Groups["command"].Value.Trim()}。",
                match.Groups["command"].Value.Trim()));
        }

        // 3. 其它错误：按 TeX 的错误块逐条处理。错误块从列首的 "! " 开始，延伸到下一个错误块之前。
        //    注意不能按「折叠后的记录是否以 ! 开头」判断——折行会把上一条记录并进错误块。
        foreach (var block in EnumerateErrorBlocks(logText))
        {
            if (IsHandledByDependencyRules(block))
            {
                continue;
            }

            var isCascade = CascadePatterns.Any(pattern => pattern.IsMatch(block));
            issues.Add(new TeXIssue(
                isCascade ? TeXIssueCode.Cascade : TeXIssueCode.SourceError,
                FirstLine(block),
                null,
                null,
                !isCascade));
        }

        return new TeXCompileReport(
            hasOutput,
            pageCount,
            outputFileName,
            Deduplicate(issues).ToList(),
            Overfull.Matches(log).Count);
    }

    /// <summary>
    /// 把按列宽硬折行的日志拼回原样。TeX 折行处不增删字符，因此直接相接；
    /// 上一条记录已以句点、叹号或问号结束时才认为换了一条记录。
    /// </summary>
    internal static string Fold(string logText)
    {
        var records = new List<string>();
        var current = new StringBuilder();

        foreach (var rawLine in logText.Replace("\r", string.Empty).Split('\n'))
        {
            var line = ContinuationPrefix.Replace(rawLine, string.Empty);

            if (current.Length == 0)
            {
                current.Append(line);
                continue;
            }

            var previous = current.ToString().TrimEnd();
            if (previous.EndsWith('.') || previous.EndsWith('!') || previous.EndsWith('?'))
            {
                records.Add(current.ToString());
                current.Clear();
                current.Append(line);
                continue;
            }

            current.Append(line);
        }

        if (current.Length > 0)
        {
            records.Add(current.ToString());
        }

        return string.Join('\n', records);
    }

    /// <summary>
    /// 按 TeX 的错误块切分原始日志。错误块的起始行以列首的 "! " 开头，延伸到下一个错误块之前，
    /// 块内按折叠规则拼回被折行截断的内容（缺字体的多行说明就是这样还原的）。
    /// </summary>
    private static IEnumerable<string> EnumerateErrorBlocks(string logText)
    {
        var lines = logText.Replace("\r", string.Empty).Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].StartsWith('!'))
            {
                continue;
            }

            var end = index + 1;
            while (end < lines.Length && !lines[end].StartsWith('!'))
            {
                end++;
            }

            yield return Fold(string.Join('\n', lines[index..end]));
            index = end - 1;
        }
    }

    private static List<TeXIssue> CollectMissingImages(string log)
    {
        var lineHints = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in FileNotFoundWarning.Matches(log))
        {
            lineHints[match.Groups["file"].Value] =
                int.Parse(match.Groups["line"].Value, CultureInfo.InvariantCulture);
        }

        foreach (Match match in UnableToLoadPicture.Matches(log))
        {
            lineHints.TryAdd(match.Groups["file"].Value, 0);
        }

        var issues = new List<TeXIssue>();
        foreach (var (file, line) in lineHints)
        {
            var isPicture = IsPicture(file);
            issues.Add(new TeXIssue(
                isPicture ? TeXIssueCode.MissingPicture : TeXIssueCode.MissingFile,
                isPicture
                    ? $"缺少题目图片或图形文件：{file}。导入包必须自带题目所需图片，缺少时不得发布。"
                    : $"缺少被引用的文件：{file}。",
                file,
                line == 0 ? null : line));
        }

        return issues;
    }

    private static List<TeXIssue> CollectMissingPackages(string log)
    {
        var issues = new List<TeXIssue>();
        foreach (Match match in MissingPackage.Matches(log))
        {
            var file = match.Groups["file"].Value;
            var isPackage = file.EndsWith(".sty", StringComparison.OrdinalIgnoreCase)
                            || file.EndsWith(".cls", StringComparison.OrdinalIgnoreCase);

            issues.Add(new TeXIssue(
                isPackage ? TeXIssueCode.MissingPackage : TeXIssueCode.MissingFile,
                isPackage
                    ? $"缺少宏包或文档类文件：{file}。导入包必须自带所需依赖，不得联网获取或静默替换。"
                    : $"缺少被引用的文件：{file}。",
                file));
        }

        return issues;
    }

    private static List<TeXIssue> CollectMissingFonts(string log)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? name)
        {
            var trimmed = name?.Trim().Trim('"');
            if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
            {
                names.Add(trimmed);
            }
        }

        foreach (Match match in FontspecFontMissing.Matches(log))
        {
            Add(match.Groups["font"].Value);
        }

        foreach (Match match in FontNotLoadableName.Matches(log))
        {
            Add(match.Groups["font"].Value);
        }

        if (FontNotLoadableSignature.IsMatch(log) && names.Count == 0)
        {
            // 只看到「字体不可加载」但没能取出名称：仍然必须报出，不静默放过。
            Add("（名称未能从日志确定，请检查字体安装）");
        }

        return names
            .Select(name => new TeXIssue(
                TeXIssueCode.MissingFont,
                $"缺少字体：{name}。不得静默替换为其他字体，请在该主机安装后重试。",
                name))
            .ToList();
    }

    private static bool IsHandledByDependencyRules(string record) =>
        MissingPackage.IsMatch(record)
        || UnableToLoadPicture.IsMatch(record)
        || FontspecFontMissing.IsMatch(record)
        || FontNotLoadableSignature.IsMatch(record)
        || ShellEscapeDisabled.IsMatch(record);

    private static bool IsPicture(string file)
    {
        var extension = Path.GetExtension(file);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".eps", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<TeXIssue> Deduplicate(IEnumerable<TeXIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var issue in issues)
        {
            if (seen.Add($"{issue.Code}|{issue.File}|{issue.Message}"))
            {
                yield return issue;
            }
        }
    }

    private static string FirstLine(string text)
    {
        var index = text.IndexOf('\n');
        var line = index < 0 ? text : text[..index];
        return line.Length > 300 ? line[..300] : line;
    }
}
