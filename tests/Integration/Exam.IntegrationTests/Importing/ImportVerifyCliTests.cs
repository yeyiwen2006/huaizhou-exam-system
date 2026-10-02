using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Exam.Contracts.Importing;

namespace Exam.IntegrationTests.Importing;

/// <summary>
/// D06 导入编译对照命令的用例：真实 CLI 进程 + 合成导入包。
/// 覆盖清单校验不过、缺清单、以及真正编译并抽取（后者需要 TeX，单独标记）。
/// </summary>
public sealed class ImportVerifyCliTests : IDisposable
{
    private const string ExamId = "import-verify-exam";
    private const string ExamName = "导入对照合成考试";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "import-verify-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    [Fact]
    public async Task Verify_WithoutManifest_IsUsageError()
    {
        Directory.CreateDirectory(_root);

        var result = await RunCliAsync(["import", "verify", _root]);

        Assert.Equal(2, result.Exit);
    }

    [Fact]
    public async Task Verify_WithManifestFailingValidation_ReportsRulesAndExits3()
    {
        // 题目源文件名前缀与考试名称不一致：D01 校验必须拒绝，且不进入编译。
        WritePackage(sourceFileName: "另一场考试_1.tex");

        var result = await RunCliAsync(["import", "verify", _root]);

        Assert.Equal(3, result.Exit);
        Assert.Contains(ManifestRuleIds.QuestionSourceFileName, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Latex")]
    public async Task Verify_MarkedPackage_CompilesEveryQuestionAndExtractsEveryOption()
    {
        WritePackage(sourceFileName: $"{ExamName}_1.tex");

        var result = await RunCliAsync(["import", "verify", _root]);

        Assert.Equal(0, result.Exit);
        Assert.Contains("编译成功 1", result.Output, StringComparison.Ordinal);
        Assert.Contains("抽取失败 0", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Latex")]
    public async Task Verify_QuestionWithoutMarkers_ReportsExtractionFailure()
    {
        // 未适配标记的材料：编译能过，但选项定位抽不出来——必须在导入对照阶段报出，
        // 而不是留到学生端点不了才发现。
        WritePackage(sourceFileName: $"{ExamName}_1.tex", markOptions: false);

        var result = await RunCliAsync(["import", "verify", _root]);

        Assert.Equal(4, result.Exit);
        Assert.Contains("抽取失败 1", result.Output, StringComparison.Ordinal);
        Assert.Contains("MarkerMissing", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_WithoutRequiredOptions_IsUsageError()
    {
        // 服务端命令要求 --exam/--version/--compile/--reason；缺任何一项都属用法错误，不发请求。
        var result = await RunCliAsync(["import", "preview", _root]);

        // 退出码 2 由 CLI 统一映射为 INVALID_INPUT（命令自己的文案会被该层替换）。
        Assert.Equal(2, result.Exit);
        Assert.Contains("INVALID_INPUT", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_WithoutOutputDirectory_IsUsageError()
    {
        // 发布要按 --output 写加密包；缺它在编译之前就是用法错误，不发请求、不必等编译完才发现。
        // 加密收件人由服务器受管节点决定，命令不再需要传节点证书。
        var result = await RunCliAsync(["import", "publish", _root,
            "--exam", "import-verify-exam", "--version", "1",
            "--compile", _root, "--reason", "合成用例",
            "--server", "http://127.0.0.1:1"]);

        Assert.Equal(2, result.Exit);
    }

    private void WritePackage(string sourceFileName, bool markOptions = true)
    {
        Directory.CreateDirectory(Path.Combine(_root, "questions"));
        File.WriteAllText(Path.Combine(_root, "manifest.json"), $$"""
            {
              "schemaVersion": 1,
              "examId": "{{ExamId}}",
              "examName": "{{ExamName}}",
              "subjects": [{ "subjectId": "s", "name": "科目", "maxScore": "1" }],
              "groups": [{ "groupId": "g", "subjectId": "s", "name": "单选", "type": "single", "order": 1,
                "questionCount": 1, "maxScore": "1",
                "scoring": { "oneCorrect": { "correct": "1", "blank": "0", "wrong": "0" } } }],
              "questions": [{ "questionId": "q-001", "questionNo": 1, "subjectId": "s", "groupId": "g",
                "source": "questions/{{sourceFileName}}", "options": ["A", "B"] }],
              "resourceRoots": []
            }
            """, new UTF8Encoding(false));

        var options = markOptions
            ? """
                \item[\optmark{q-001}{A}{A.}] 甲
                \item[\optmark{q-001}{B}{B.}] 乙
                """
            : """
                \item[A.] 甲
                \item[B.] 乙
                """;
        // LaTeX 里满是连续花括号，用占位符替换而不是插值——插值原始字符串会被连续花括号截断。
        const string template = """
            \documentclass[UTF8]{ctexart}
            \pagestyle{empty}
            \usepackage{hyperref}
            \hypersetup{hidelinks}
            \DeclareRobustCommand{\optmark}[3]{\hypertarget{opt:#1:#2}{}\hyperlink{opt:#1:#2}{#3}}
            \begin{document}
            合成题：下列选项正确的是（\quad）。

            \begin{enumerate}
            __OPTIONS__
            \end{enumerate}
            \end{document}
            """;
        File.WriteAllText(Path.Combine(_root, "questions", sourceFileName),
            template.Replace("__OPTIONS__", options, StringComparison.Ordinal), new UTF8Encoding(false));
    }

    private static async Task<(int Exit, string Output, string Error)> RunCliAsync(string[] args)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Huaizhou.ExamSystem.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var host = Path.Combine(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")),
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var start = new ProcessStartInfo(host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(root.FullName, "src", "Exam.Cli", "bin", "Release", "net10.0", "Exam.Cli.dll"));
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await output, await error);
    }
}
