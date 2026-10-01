using System.IO;
using System.Text.RegularExpressions;

namespace Exam.StudentUnitTests;

/// <summary>
/// 三端 XAML 的资源键引用必须有定义。缺失的 StaticResource 只有在模板实例化时才抛异常
/// （例如答题页题号红点曾引用未定义的 BoolToVis），普通构建与非 GUI 测试发现不了。
/// </summary>
public sealed partial class XamlResourceReferenceTests
{
    private static readonly string[] Projects = ["Exam.Student", "Exam.Admin", "Exam.Desktop.Shared"];

    [Fact]
    public void EveryReferencedResourceKeyIsDefinedInSharedThemeOrSameFile()
    {
        var root = RepositoryRoot();
        var files = Projects
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.xaml", SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .ToArray();
        Assert.NotEmpty(files);
        // 共享主题对所有页面可见；其余文件只能看到自身定义（页面、控件或窗口资源）。
        var themeDirectory = Path.Combine(root, "src", "Exam.Desktop.Shared", "Themes") + Path.DirectorySeparatorChar;
        var themeKeys = files.Where(path => path.StartsWith(themeDirectory, StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => Keys(File.ReadAllText(path))).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("BoolToVis", themeKeys);

        var missing = new List<string>();
        foreach (var path in files)
        {
            var text = File.ReadAllText(path);
            var local = Keys(text).ToHashSet(StringComparer.Ordinal);
            foreach (Match reference in ReferencePattern().Matches(text))
            {
                var key = reference.Groups["key"].Value;
                if (!themeKeys.Contains(key) && !local.Contains(key))
                    missing.Add($"{Path.GetRelativePath(root, path)}: {reference.Value}");
            }
        }
        Assert.True(missing.Count == 0, "未定义的资源引用：" + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    private static IEnumerable<string> Keys(string xaml) => KeyPattern().Matches(xaml).Select(match => match.Groups["key"].Value);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Huaizhou.ExamSystem.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    [GeneratedRegex("x:Key=\"(?<key>[^\"{}]+)\"")]
    private static partial Regex KeyPattern();

    // {x:Type …} 等标记扩展形式的键不在检查范围内。
    [GeneratedRegex(@"\{(?:Static|Dynamic)Resource\s+(?<key>[^\s{}]+)\s*\}")]
    private static partial Regex ReferencePattern();
}
