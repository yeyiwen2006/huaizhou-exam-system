using System.Text.Json;
using Exam.Contracts.Importing;
using Exam.Domain.Importing;
using Exam.Infrastructure.Compilation;
using Exam.Infrastructure.ServiceManagement;

internal static partial class ExamCli
{
    /// <summary>
    /// 在导入包上跑一遍 D 组流水线并报告结果：D01 清单与答案校验 → D02 逐题编译 → D03 选项坐标抽取。
    ///
    /// 这是**本地**命令（不入库、不连服务器）：在材料所在机器上核对「这套包能不能导入、能不能编译、
    /// 每个选项能不能定位」。抽取依赖包内已带隐藏外观链接标记；未适配的材料会在这里报出抽取失败，
    /// 而不是留到学生端才发现点不了。需要 `--no-regions` 时只做清单校验与编译。
    /// </summary>
    private static async Task<int> RunImportAsync(string action, string packageRoot, string[] arguments,
        CancellationToken cancellationToken)
    {
        if (action is "preview" or "publish" or "materials")
        {
            return await RunImportRemoteAsync(action, packageRoot, arguments, cancellationToken);
        }

        if (action is not "verify") return 2;
        var options = ParseOptions(arguments);
        var keepOutput = options.Input is not null;
        var manifestPath = Path.Combine(packageRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"找不到导入清单：{manifestPath}");
            return 2;
        }

        var manifestText = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var answerKeyPath = Path.Combine(packageRoot, "server-only", "answer-key.json");
        var answerKeyText = File.Exists(answerKeyPath)
            ? await File.ReadAllTextAsync(answerKeyPath, cancellationToken)
            : null;

        var validation = new ExamManifestValidator().ValidateJson(manifestText, answerKeyText);
        if (!validation.IsValid)
        {
            Console.Error.WriteLine($"清单或答案未通过校验，共 {validation.Issues.Count} 条：");
            foreach (var issue in validation.Issues.Take(20))
            {
                Console.Error.WriteLine($"  {issue.Rule} @ {issue.Location}");
            }

            return 3;
        }

        if (validation.RequiresAnswerKeyArithmetic)
        {
            Console.Error.WriteLine("提示：未提供完整参考答案，满分核对未执行（server-only/answer-key.json 缺失）。");
        }

        var manifest = JsonSerializer.Deserialize<ExamManifest>(manifestText, ImportJson.Create())!;
        var toolchain = XeLaTeXToolchain.TryDetect(XeLaTeXPlatform.TeXLive);
        if (toolchain is null)
        {
            Console.Error.WriteLine("找不到可用的 XeLaTeX：本命令需要 TeX 发行版编译逐题源文件。");
            return 5;
        }

        var runner = new ExternalProcessRunner();
        var version = await runner.RunAsync(toolchain.ExecutablePath, ["--version"],
            new Dictionary<string, string>(), TimeSpan.FromSeconds(10), cancellationToken);
        var platform = version.StandardOutput.Contains("MiKTeX", StringComparison.OrdinalIgnoreCase)
            ? XeLaTeXPlatform.MiKTeX
            : XeLaTeXPlatform.TeXLive;
        var compiler = new XeLaTeXCompiler(runner, toolchain with { Platform = platform }, TimeSpan.FromMinutes(3));
        var extractor = new OptionRegionExtractor();
        var output = Path.Combine(Path.GetTempPath(), "exam-import-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);

        var compiled = 0;
        var compileFailed = 0;
        var extractFailed = 0;
        try
        {
            foreach (var question in manifest.Questions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await compiler.CompileAsync(
                    new TeXCompileRequest(packageRoot, question.Source, output), cancellationToken);
                if (!result.Succeeded)
                {
                    compileFailed++;
                    Console.Error.WriteLine(
                        $"编译失败 {question.QuestionId}：{string.Join("；", result.Issues.Select(i => i.Code))}");
                    continue;
                }

                compiled++;
                if (keepOutput) continue;

                var extraction = extractor.Extract(new OptionRegionRequest(manifest.ExamId, 1,
                    // D01 校验已保证清单的选项非空且唯一。
                    [new OptionRegionSourceQuestion(question.QuestionId, result.OutputPdfPath!, question.Options!)]));
                if (extraction.Succeeded) continue;

                extractFailed++;
                Console.Error.WriteLine("抽取失败 " + question.QuestionId + "：" + string.Join("；",
                    extraction.Issues.Select(issue => issue.Code +
                        (issue.OptionId is null ? string.Empty : "(" + issue.OptionId + ")"))));
            }
        }
        finally
        {
            if (!keepOutput && Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }

        Console.WriteLine($"题目 {manifest.Questions.Count}：编译成功 {compiled}、编译失败 {compileFailed}" +
            (keepOutput ? "（仅校验与编译）" : $"、抽取失败 {extractFailed}"));
        if (keepOutput) Console.WriteLine("编译产物保留在：" + output);
        return compileFailed == 0 && extractFailed == 0 ? 0 : 4;
    }
}
