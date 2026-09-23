namespace Exam.UnitTests.Importing;

/// <summary>
/// 定位仓库根目录，供测试读取 samples/ 与 schemas/ 下的合成样例和 schema。
/// 找不到解决方案文件时直接抛异常，绝不静默跳过——否则样例校验会在路径错误时假装通过。
/// </summary>
internal static class TestRepositoryPaths
{
    private const string SolutionFileName = "Huaizhou.ExamSystem.sln";

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string SyntheticSamplesRoot => Path.Combine(RepositoryRoot, "samples", "synthetic");

    public static string SchemasRoot => Path.Combine(RepositoryRoot, "schemas");

    public static string ReadText(params string[] relativeSegments)
    {
        var path = Path.Combine([RepositoryRoot, .. relativeSegments]);
        Assert.True(File.Exists(path), $"样例或 schema 文件不存在：{path}");
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"从 {AppContext.BaseDirectory} 向上未找到 {SolutionFileName}，无法读取合成样例。");
    }
}
