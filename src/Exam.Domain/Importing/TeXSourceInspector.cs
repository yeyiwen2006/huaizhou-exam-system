using System.Text.RegularExpressions;

using Exam.Contracts.Importing;

namespace Exam.Domain.Importing;

/// <summary>
/// 静态读取逐题 TeX 源文件的声明：文档类、宏包、字体、图片搜索路径、图片引用，
/// 以及禁止外部命令与路径逃逸的违规。纯文本处理，不读文件、不联网。
///
/// 依赖检查按执行指南的要求同时看「源文件」与「编译日志」：本类型覆盖源侧能确定的部分，
/// 宏导致的间接引用（宏体里含 \includegraphics 的自定义命令）也在源侧展开；
/// 更隐蔽的依赖仍由编译日志兜底，见 <see cref="XeLaTeXLogReader"/>。
/// </summary>
public static class TeXSourceInspector
{
    /// <summary>需要执行外部命令才能工作的宏包：导入包一律禁止。</summary>
    private static readonly HashSet<string> ExternalCommandPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "minted", "pythontex", "sagetex", "gnuplottex", "asymptote", "dot2texi", "bashful"
    };

    private static readonly Regex DocumentClass = new(
        @"\\(?:documentclass|LoadClass)\s*(?:\[[^\]]*\])?\s*\{(?<name>[^{}]+)\}",
        RegexOptions.Compiled);

    private static readonly Regex UsePackage = new(
        @"\\(?:usepackage|RequirePackage)\s*(?:\[[^\]]*\])?\s*\{(?<names>[^{}]+)\}",
        RegexOptions.Compiled);

    private static readonly Regex SetFont = new(
        @"\\(?:setmainfont|setsansfont|setmonofont|setmathfont|setCJKmainfont|setCJKsansfont|setCJKmonofont)"
        + @"\s*(?:\[[^\]]*\])?\s*\{(?<font>[^{}]+)\}",
        RegexOptions.Compiled);

    private static readonly Regex NewFontFamily = new(
        @"\\(?:newfontfamily|newCJKfontfamily)\s*\\[A-Za-z@]+\s*(?:\[[^\]]*\])?\s*\{(?<font>[^{}]+)\}",
        RegexOptions.Compiled);

    private static readonly Regex GraphicsPath = new(
        @"\\graphicspath\s*\{(?<entries>(?:\{[^{}]*\})*)\}",
        RegexOptions.Compiled);

    private static readonly Regex GraphicsPathEntry = new(@"\{([^{}]*)\}", RegexOptions.Compiled);

    private static readonly Regex MacroDefinition = new(
        @"\\(?:newcommand|renewcommand|providecommand)\s*\{\\(?<name>[A-Za-z@]+)\}"
        + @"\s*(?:\[\d+\])?\s*(?:\[[^\]]*\])?\s*\{(?<body>(?:[^{}]|\{[^{}]*\})*)\}",
        RegexOptions.Compiled);

    private static readonly Regex IncludeGraphics = new(
        @"\\includegraphics\s*(?:\[[^\]]*\])?\s*\{(?<name>[^{}]+)\}",
        RegexOptions.Compiled);

    private static readonly Regex NestedInput = new(
        @"\\(?:input|include)(?![A-Za-z@])\s*(?:\{(?<path>[^{}]+)\}|(?<path>[^\s{}%\\]+))",
        RegexOptions.Compiled);

    private static readonly Regex ShellEscapeCommand = new(
        @"\\write\s*18\b|\\ShellEscape\b|\\input\s*\{?\s*[""']?\|",
        RegexOptions.Compiled);

    public static TeXSourceDeclaration Inspect(string? texSource)
    {
        if (string.IsNullOrEmpty(texSource))
        {
            return new TeXSourceDeclaration(string.Empty, [], [], [], [], []);
        }

        var source = StripComments(texSource);

        var documentClass = DocumentClass.Match(source) is { Success: true } documentMatch
            ? documentMatch.Groups["name"].Value.Trim()
            : string.Empty;

        var packages = UsePackage.Matches(source)
            .SelectMany(match => match.Groups["names"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                                           | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fonts = SetFont.Matches(source)
            .Concat(NewFontFamily.Matches(source))
            .Select(match => match.Groups["font"].Value.Trim())
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var graphicsRoots = GraphicsPath.Matches(source)
            .SelectMany(match => GraphicsPathEntry.Matches(match.Groups["entries"].Value))
            .Select(entry => entry.Groups[1].Value.Replace('\\', '/').Trim())
            .Where(entry => entry.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 宏体里含 \includegraphics 的自定义命令视作图片宏，展开其调用（真实材料就用这种宏包住图片）。
        var imageMacros = MacroDefinition.Matches(source)
            .Where(match => match.Groups["body"].Value.Contains("includegraphics", StringComparison.Ordinal))
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var references = CollectImageReferences(source, imageMacros);

        var violations = new List<TeXIssue>();
        violations.AddRange(CheckExternalCommands(source, packages));
        violations.AddRange(CheckPaths(source, graphicsRoots, references));
        foreach (var name in packages.Concat(DocumentClass.Matches(source).Select(match => match.Groups["name"].Value.Trim())))
        {
            if (!PackagePathRules.IsRelativeAndInside(name) || !PackagePathRules.HasValidWindowsSegments(name))
            {
                violations.Add(new TeXIssue(TeXIssueCode.MissingFile,
                    $"宏包和文档类必须使用有效的包内相对路径或已安装的依赖名：{name}。", name));
            }
        }

        foreach (Match match in Regex.Matches(source, @"\\(?:openin|openout)(?![A-Za-z@])"))
        {
            violations.Add(new TeXIssue(TeXIssueCode.SourceError,
                "导入源文件不得直接打开文件读写流，请使用包内的声明式依赖引用。", null, LineOf(source, match.Index)));
        }

        return new TeXSourceDeclaration(documentClass, packages, fonts, graphicsRoots, references, violations);
    }

    /// <summary>
    /// 按 \graphicspath 的顺序给出图片的候选相对路径（先包根目录，再各搜索路径），
    /// 供依赖检查逐个核对是否存在。只生成路径，不访问文件系统。
    /// </summary>
    public static IReadOnlyList<string> ResolveImageCandidates(string reference, IReadOnlyList<string> graphicsRoots)
    {
        var normalized = reference.Replace('\\', '/').Trim();
        var candidates = new List<string>();

        // graphicx 先查当前工作目录，再把引用（包括子目录）附在 graphicspath 后。
        var names = Path.HasExtension(normalized)
            ? new[] { normalized }
            : new[] { ".pdf", ".png", ".jpg", ".jpeg", ".eps" }.Select(extension => normalized + extension);
        foreach (var root in new[] { string.Empty }.Concat(graphicsRoots))
        {
            foreach (var name in names)
            {
                candidates.Add(root.Length == 0 ? name : $"{root.TrimEnd('/')}/{name}");
            }
        }

        return candidates.Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<string> CollectImageReferences(string source, IReadOnlyList<string> imageMacros)
    {
        var references = IncludeGraphics.Matches(source)
            .Select(match => match.Groups["name"].Value.Trim())
            .ToList();

        foreach (var macro in imageMacros)
        {
            // 真实材料里的图片宏形如 \questionimage[宽度]{文件}。先取整个参数串，再取其中**最后一个**
            // 花括号组作为文件名：这样同时兼容 [可选]{文件} 与 {文件}，也不会把宽度参数当成图片名。
            var pattern = new Regex(
                @"\\" + Regex.Escape(macro) + @"\s*(?<arguments>(?:\[[^\]]*\])?(?:\{[^{}]*\})*)",
                RegexOptions.Compiled);

            foreach (Match match in pattern.Matches(source))
            {
                var groups = Regex.Matches(match.Groups["arguments"].Value, @"\{([^{}]*)\}");
                if (groups.Count > 0)
                {
                    references.Add(groups[^1].Groups[1].Value.Trim());
                }
            }
        }

        return references
            .Where(reference => reference.Length > 0 && !reference.StartsWith('#'))
            // 校验前保留绝对路径的前导分隔符，不能把 /outside.png 洗成包内路径。
            .Select(reference => PackagePathRules.IsRelativeAndInside(reference)
                ? PackagePathRules.Normalize(reference) : reference.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<TeXIssue> CheckExternalCommands(string source, IReadOnlyList<string> packages)
    {
        foreach (Match match in ShellEscapeCommand.Matches(source))
        {
            yield return new TeXIssue(
                TeXIssueCode.ShellEscapeAttempt,
                $"源文件使用了 {match.Value.Trim()}，需要执行外部命令；导入包一律禁止。",
                match.Value.Trim(),
                LineOf(source, match.Index));
        }

        foreach (var package in packages.Where(ExternalCommandPackages.Contains))
        {
            yield return new TeXIssue(
                TeXIssueCode.ShellEscapeAttempt,
                $"宏包 {package} 需要开启外部命令才能工作；导入包一律禁止。",
                package);
        }
    }

    private static IEnumerable<TeXIssue> CheckPaths(
        string source,
        IReadOnlyList<string> graphicsRoots,
        IReadOnlyList<string> references)
    {
        // graphicspath 是实际目录列表，不把操作系统通配符当作递归搜索语法。
        foreach (var root in graphicsRoots)
        {
            if (!PackagePathRules.IsRelativeAndInside(root) || !PackagePathRules.HasValidWindowsSegments(root))
            {
                yield return new TeXIssue(
                    TeXIssueCode.MissingFile,
                    $"图片搜索路径必须是包内相对路径，不能使用盘符、绝对路径或 ..：{root}。",
                    root);
            }
        }

        // 具体的图片引用会真的落到文件系统，除形状外还要逐段按 Windows 规则校验。
        foreach (var reference in references)
        {
            if (!PackagePathRules.IsRelativeAndInside(reference))
            {
                yield return new TeXIssue(
                    TeXIssueCode.MissingFile,
                    $"图片路径必须是包内相对路径，不能使用盘符、绝对路径或 ..：{reference}。",
                    reference);
            }
            else if (!PackagePathRules.HasValidWindowsSegments(reference))
            {
                yield return new TeXIssue(
                    TeXIssueCode.MissingFile,
                    $"图片引用的每一段都必须能用作 Windows 文件或目录名：{reference}。",
                    reference);
            }
        }

        foreach (Match match in NestedInput.Matches(source))
        {
            var path = match.Groups["path"].Value.Trim().Trim('"').Replace('\\', '/');
            if (!PackagePathRules.IsRelativeAndInside(path))
            {
                yield return new TeXIssue(
                    TeXIssueCode.MissingFile,
                    $"引用的文件必须是包内相对路径，不能使用盘符、绝对路径或 ..：{path}。",
                    path,
                    LineOf(source, match.Index));
            }
            else if (!PackagePathRules.HasValidWindowsSegments(path))
            {
                yield return new TeXIssue(
                    TeXIssueCode.MissingFile,
                    $"引用的文件每一段都必须能用作 Windows 文件或目录名：{path}。",
                    path,
                    LineOf(source, match.Index));
            }
        }
    }

    /// <summary>去掉注释，避免把注释里的示例命令当成真实引用。</summary>
    private static string StripComments(string source)
    {
        var lines = source.Replace("\r", string.Empty).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var comment = FindCommentStart(line);
            if (comment >= 0)
            {
                lines[index] = line[..comment];
            }
        }

        return string.Join('\n', lines);
    }

    private static int FindCommentStart(string line)
    {
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] != '%')
            {
                continue;
            }

            var backslashes = 0;
            for (var scan = index - 1; scan >= 0 && line[scan] == '\\'; scan--)
            {
                backslashes++;
            }

            if (backslashes % 2 == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var position = 0; position < index && position < text.Length; position++)
        {
            if (text[position] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
