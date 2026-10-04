using System.Net;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Grading;
using Exam.Domain.Exams;
using Exam.Infrastructure.Packaging;

internal static partial class ExamCli
{
    /// <summary>Rules use the same identity and immutable preview/retry contract as GUI; no TeX is run.</summary>
    private static async Task<int> RunGradingAsync(string action, string examReference, string[] arguments,
        CancellationToken cancellationToken)
    {
        if (action is not ("status" or "revise" or "preview" or "publish")) return 2;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var confirm = false;
        for (var i = 0; i < arguments.Length; i++)
        {
            var key = arguments[i];
            if (key == "--confirm") { if (confirm) return 2; confirm = true; continue; }
            if (key is not ("--server" or "--package" or "--reason" or "--output") ||
                i + 1 >= arguments.Length || arguments[i + 1].StartsWith("--", StringComparison.Ordinal) ||
                !options.TryAdd(key, arguments[++i]) || string.IsNullOrWhiteSpace(options[key])) return 2;
        }
        var preparing = action is "revise" or "preview";
        if (preparing && (confirm || !options.ContainsKey("--package") || !options.ContainsKey("--reason") || !options.ContainsKey("--output")))
        { Console.Error.WriteLine("规则预览需要 --package 材料包目录 --reason 修改原因 --output 新预览目录；尚不发布。"); return 2; }
        if (!preparing && options.Keys.Any(key => key != "--server") || action == "status" && confirm || action == "publish" && !confirm)
        { Console.Error.WriteLine("确认发布使用 grading publish 规则预览目录 --confirm，不能改动原预览参数。"); return 2; }
        if (!TryResolveServer(options.GetValueOrDefault("--server") ?? Environment.GetEnvironmentVariable(ServerEnvironment), out var address)) return 2;
        if (string.IsNullOrWhiteSpace(OperatorToken())) { Console.Error.WriteLine("请先登录考务账号。"); return 3; }
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = address, Timeout = TimeSpan.FromMinutes(5) };
        var api = new HttpExamPublicationClient(http, AddCredentials);
        try
        {
            if (action == "publish")
            {
                using var prepared = await PreparedGradingRevision.LoadAsync(examReference, cancellationToken);
                var result = await prepared.PublishAsync(api, cancellationToken);
                Console.WriteLine(JsonSerializer.Serialize(new ApiEnvelope<Exam.Contracts.Versioning.ExamVersionSnapshot>(
                    api.ServerId!, api.GradingNodeId!, api.GradingServerEpoch, prepared.Request.RequestId, result, null), JsonDefaults.Create()));
                Console.Error.WriteLine($"规则发布成功：规则版本 {result.Versions.GradingVersion}，试卷版本 {result.Versions.PaperVersion}。原预览可安全重试，不会重复建版。");
                try
                {
                    var report = await api.GetGradingStatusAsync(prepared.Request.ExamId, cancellationToken);
                    if (report.ExamId != prepared.Request.ExamId || (report.CurrentGradingVersion ?? 0) < result.Versions.GradingVersion)
                        throw new InvalidDataException("状态版本不一致。");
                    Console.Error.WriteLine(DescribeGradingState(report));
                }
                catch (Exception)
                {
                    // A committed, verified receipt remains successful even if the subsequent read fails.
                    Console.Error.WriteLine("重算状态核对失败（发布结果不受影响）；请用 grading status 复查，勿创建另一份发布请求。");
                }
                return 0;
            }
            var matches = (await api.ListExamsAsync(cancellationToken)).Where(exam =>
                ExamSettingsRules.NameKey(exam.Name) == ExamSettingsRules.NameKey(examReference) || exam.ExamId == examReference).ToArray();
            if (matches.Length != 1) { Console.Error.WriteLine("没有找到唯一匹配的考试，请核对考试名称。"); return 4; }
            var examId = matches[0].ExamId;
            if (action == "status")
            {
                var report = await api.GetGradingStatusAsync(examId, cancellationToken);
                if (report.ExamId != examId) throw new InvalidDataException("计分状态属于另一场考试。");
                Console.WriteLine(JsonSerializer.Serialize(new ApiEnvelope<GradingStatusReport>(api.ServerId!, api.GradingNodeId!,
                    api.GradingServerEpoch, "grading-status", report, null), JsonDefaults.Create()));
                Console.Error.WriteLine(DescribeGradingState(report));
                return 0;
            }
            using var preview = await PreparedGradingRevision.PrepareAsync(api, examId, options["--package"], options["--reason"], cancellationToken);
            await preview.ExportAsync(options["--output"], cancellationToken);
            Console.WriteLine($"规则检查通过：{preview.Preview.Count} 题；试卷版本 {preview.PaperVersion}，规则版本 {preview.Request.ExpectedVersion} → {preview.Request.ExpectedVersion + 1}。尚未发布。");
            Console.WriteLine("私有预览目录：" + Path.GetFullPath(options["--output"]));
            Console.WriteLine("请核对目录中的 preview.md、清单和完整答案，再执行 grading publish 该预览目录 --confirm。终端不输出参考答案。");
            return 0;
        }
        catch (ExamPublicationApiException error)
        {
            Console.Error.WriteLine($"计分操作未确认成功（{error.Code}）；确认发布时请保留同一规则预览重试。");
            return error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? 3 : (int)error.StatusCode >= 500 ? 6 : 4;
        }
        catch (OperationCanceledException)
        { Console.Error.WriteLine("操作取消或超时；已尝试发布时请保留同一规则预览，不能据此判定没有提交。"); return cancellationToken.IsCancellationRequested ? 130 : 5; }
        catch (HttpRequestException)
        { Console.Error.WriteLine("网络请求失败；请保留同一规则预览，不重新生成请求编号。"); return 5; }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or UnauthorizedAccessException)
        { Console.Error.WriteLine("规则预览或发布核对失败，请检查本机材料、版本、目标服务器与账号；未确认成功。"); return 4; }
    }

    private static string DescribeGradingState(GradingStatusReport report)
    {
        if (report.CurrentGradingVersion is not { } version) return "尚未发布任何计分规则：没有可核对的结果版本。";
        if (report.Readiness is not { } readiness) return $"已发布规则版本 {version}，但没有就绪结论。";
        var scope = $"范围 {report.FinalSubmissionCount} 份最终答卷，已按版本 {version} 计分 {report.GradedAtCurrentVersion} 份。";
        return readiness.RecalculationReady ? $"结果就绪：规则版本 {version}，{scope}" :
            $"重算中：规则版本 {version}，{scope} 未完成 {readiness.Outstanding.Count} 份。重算期间旧报表不可查询或导出。";
    }
}
