using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Grading;
using Exam.Contracts.Operators;
using Exam.Contracts.Versioning;
using Exam.Domain.Exams;
using Exam.Domain.Importing;

internal static partial class ExamCli
{
    /// <summary>
    /// 计分相关命令：
    /// - `grading status 考试`：读取当前已发布规则版本、完整答卷范围与计分完成情况。
    /// - `grading revise 考试 --package 材料包目录 --reason 说明`：**只改答案与计分规则**，
    ///   从本地材料包读清单与完整答案，复用既有规则发布端点提交（不重新编译、不组包）。
    ///   发布成功后读一次计分状态，把「发布成功 / 重算中 / 结果就绪」分别显示；
    ///   重算由服务器在发布提交后及时安排，后台循环补偿——**没有**「触发重算」接口。
    /// </summary>
    private static async Task<int> RunGradingAsync(string action, string examReference, string[] arguments,
        CancellationToken cancellationToken)
    {
        var userCancellation = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        cancellationToken = deadline.Token;
        if (action is not ("status" or "revise")) return 2;

        var parseError = string.Empty;
        var revise = action == "revise" ? ParseGradingReviseOptions(arguments, out parseError) : null;
        if (action == "revise" && revise is null)
        {
            Console.Error.WriteLine(parseError);
            return 2;
        }

        // 两个命令的选项集不同：status 只有通用选项，revise 另有 --package 与 --reason。
        var server = action == "revise"
            ? revise!.Server ?? Environment.GetEnvironmentVariable(ServerEnvironment)
            : ParseOptions(arguments).Server ?? Environment.GetEnvironmentVariable(ServerEnvironment);
        if (!TryResolveServer(server, out var address)) return 2;
        if (string.IsNullOrWhiteSpace(OperatorToken()))
        {
            Console.Error.WriteLine("请先登录考务账号，再读取或修改计分规则。");
            return 3;
        }

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = address,
            Timeout = TimeSpan.FromMinutes(30)
        };

        try
        {
            return action == "revise"
                ? await ReviseGradingRulesAsync(client, examReference, revise!, cancellationToken)
                : await ReadGradingStatusAsync(client, examReference, cancellationToken);
        }
        catch (OperationCanceledException) when (!userCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            Console.Error.WriteLine("NETWORK_UNAVAILABLE: 计分请求超时。");
            return 5;
        }
        catch (GradingCliException error)
        {
            Console.Error.WriteLine($"计分请求被拒绝：HTTP {(int)error.Status}。" +
                (string.IsNullOrWhiteSpace(error.Code) ? string.Empty : $" 问题码 {error.Code}：{error.Message}"));
            return error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? 3 :
                (int)error.Status >= 500 ? 6 : 4;
        }
    }

    private static async Task<int> ReadGradingStatusAsync(HttpClient client, string examReference,
        CancellationToken cancellationToken)
    {
        // status 沿用既有语义：位置参数就是考试标识，stdout 仍是业务封套（既有输出契约）。
        var (report, envelope) = await GetGradingStatusAsync(client, examReference, cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(envelope, JsonDefaults.Create()));
        Console.Error.WriteLine(DescribeGradingState(report));
        return 0;
    }

    private static async Task<int> ReviseGradingRulesAsync(HttpClient client, string examReference,
        GradingReviseOptions options, CancellationToken cancellationToken)
    {
        var examId = await ResolveExamIdAsync(client, examReference, cancellationToken);
        var snapshot = await GetSnapshotAsync(client, examId, cancellationToken);
        if (snapshot.Versions.PaperVersion < 1 || snapshot.Paper is null)
        {
            Console.Error.WriteLine("该考试尚未发布试卷版本；答案与计分规则必须对应一份已发布的试卷。");
            return 4;
        }

        var packageRoot = Path.GetFullPath(options.PackageRoot!);
        var manifestJson = await File.ReadAllTextAsync(Path.Combine(packageRoot, "manifest.json"), cancellationToken);
        var answerKeyPath = Path.Combine(packageRoot, "server-only", "answer-key.json");
        if (!File.Exists(answerKeyPath))
        {
            Console.Error.WriteLine("材料包里缺少 server-only/answer-key.json：换规则必须带完整参考答案。");
            return 4;
        }

        var answerJson = await File.ReadAllTextAsync(answerKeyPath, cancellationToken);
        var validation = new ExamManifestValidator().ValidateJson(manifestJson, answerJson);
        if (!validation.IsValid)
        {
            Console.Error.WriteLine("清单或参考答案未通过校验：");
            foreach (var issue in validation.Issues.Take(20))
            {
                Console.Error.WriteLine($"  {issue.Rule}@{issue.Location}：{issue.Message}");
            }

            return 3;
        }

        if (validation.RequiresAnswerKeyArithmetic)
        {
            Console.Error.WriteLine("参考答案不完整：缺少计分算术所需的条目，不能改计分规则。");
            return 4;
        }

        var operatorId = await CurrentOperatorAsync(client, cancellationToken);
        var ruleSetId = "rules-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson + answerJson)))[..16].ToLowerInvariant();
        var body = new
        {
            requestId = Guid.NewGuid().ToString("N"),
            expectedVersion = snapshot.Versions.GradingVersion,
            ruleSetId,
            validation = new { answersValidated = true, rulesValidated = true, recalculationReady = true },
            @operator = operatorId,
            reason = options.Reason,
            manifestJson,
            answerKeyJson = answerJson
        };

        using var request = new HttpRequestMessage(HttpMethod.Post,
            "/api/v1/versioning/exams/" + Uri.EscapeDataString(examId) + "/grading-publications")
        {
            Content = JsonContent.Create(body)
        };
        AddCredentials(request);
        using var response = await client.SendAsync(request, cancellationToken);
        var envelope = await ReadEnvelopeAsync<ExamVersionSnapshot>(response, cancellationToken);
        if (!response.IsSuccessStatusCode || envelope?.Result is null)
        {
            throw new GradingCliException(response.StatusCode,
                envelope?.Error?.Code ?? string.Empty, envelope?.Error?.Message ?? "服务器未返回已发布的规则版本。");
        }

        Console.WriteLine(JsonSerializer.Serialize(envelope, JsonDefaults.Create()));
        var published = envelope.Result;
        // 「发布成功」与「成绩重算完」是两件事：分别显示，不合并成一个「完成」。
        Console.Error.WriteLine($"规则发布成功：版本 {published.Versions.GradingVersion}" +
            $"（试卷版本 {published.Versions.PaperVersion}，规则集 {published.Grading?.RuleSetId ?? "未记录"}）。");
        var status = await GetGradingStatusAsync(client, examId, cancellationToken);
        Console.Error.WriteLine(DescribeGradingState(status.Report));
        Console.Error.WriteLine("可用 `grading status` 复查重算进度；重算期间旧报表不可查询或导出。");
        return 0;
    }

    /// <summary>把计分状态翻成「重算中 / 结果就绪」两种可读状态，并带上范围与阻断原因。</summary>
    private static string DescribeGradingState(GradingStatusReport report)
    {
        if (report.CurrentGradingVersion is not { } version)
        {
            return "尚未发布任何计分规则：没有可核对的结果版本。";
        }

        if (report.Readiness is not { } readiness)
        {
            return $"已发布规则版本 {version}，但本次读取没有就绪结论。";
        }

        var scope = $"范围 {report.FinalSubmissionCount} 份最终答卷，已按版本 {version} 计分 {report.GradedAtCurrentVersion} 份。";
        if (readiness.RecalculationReady)
        {
            return $"结果就绪：规则版本 {version}，{scope}";
        }

        var blockers = readiness.Blockers.Count == 0 ? string.Empty : " 阻断：" + string.Join("；", readiness.Blockers);
        return $"重算中：规则版本 {version}，{scope} 未完成 {readiness.Outstanding.Count} 份。{blockers}".TrimEnd();
    }

    /// <summary>读计分状态：返回报告与**原样封套**（命令把封套写到 stdout，与既有输出约定一致）。</summary>
    private static async Task<(GradingStatusReport Report, ApiEnvelope<GradingStatusReport> Envelope)>
        GetGradingStatusAsync(HttpClient client, string examId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/grading/" + Uri.EscapeDataString(examId) + "/status");
        AddCredentials(request);
        using var response = await client.SendAsync(request, cancellationToken);
        var envelope = await ReadEnvelopeAsync<GradingStatusReport>(response, cancellationToken);
        if (!response.IsSuccessStatusCode || envelope?.Result is null || envelope.Result.ExamId != examId)
        {
            throw new GradingCliException(response.StatusCode, envelope?.Error?.Code ?? string.Empty,
                envelope?.Error?.Message ?? "计分状态响应缺少有效结果。");
        }

        return (envelope.Result, envelope);
    }

    private static async Task<ExamVersionSnapshot> GetSnapshotAsync(HttpClient client, string examId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/versioning/exams/" + Uri.EscapeDataString(examId));
        AddCredentials(request);
        using var response = await client.SendAsync(request, cancellationToken);
        var envelope = await ReadEnvelopeAsync<ExamVersionSnapshot>(response, cancellationToken);
        if (!response.IsSuccessStatusCode || envelope?.Result is null || envelope.Result.ExamId != examId)
        {
            throw new GradingCliException(response.StatusCode, envelope?.Error?.Code ?? string.Empty,
                envelope?.Error?.Message ?? "考试版本响应缺少有效结果。");
        }

        return envelope.Result;
    }

    private static async Task<string> CurrentOperatorAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/operators/me");
        AddCredentials(request);
        using var response = await client.SendAsync(request, cancellationToken);
        var envelope = await ReadEnvelopeAsync<OperatorAccount>(response, cancellationToken);
        if (!response.IsSuccessStatusCode || envelope?.Result is null ||
            string.IsNullOrWhiteSpace(envelope.Result.Username))
        {
            throw new GradingCliException(response.StatusCode, envelope?.Error?.Code ?? string.Empty,
                envelope?.Error?.Message ?? "无法确认当前考务身份，请重新登录。");
        }

        return envelope.Result.Username;
    }

    /// <summary>按名称解析考试；名称找不到时按内部标识再试一次（两者都是只读查询）。</summary>
    private static async Task<string> ResolveExamIdAsync(HttpClient client, string examReference,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(examReference))
        {
            throw new GradingCliException(HttpStatusCode.BadRequest, "INVALID_INPUT", "缺少考试名称。");
        }

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/exams");
        AddCredentials(listRequest);
        using var listResponse = await client.SendAsync(listRequest, cancellationToken);
        var list = await ReadEnvelopeAsync<Exam.Contracts.Exams.ExamSummary[]>(listResponse, cancellationToken);
        var matches = (list?.Result ?? [])
            .Where(exam => ExamSettingsRules.NameKey(exam.Name) == ExamSettingsRules.NameKey(examReference))
            .ToArray();
        if (matches.Length == 1)
        {
            return matches[0].ExamId;
        }

        if (matches.Length > 1)
        {
            throw new GradingCliException(HttpStatusCode.Conflict, "EXAM_NAME_AMBIGUOUS",
                "有多场同名考试，请先在考务端确认要改哪一场。");
        }

        // 名称没命中：允许按内部标识传入（只读核对存在性）。
        var snapshot = await GetSnapshotAsync(client, examReference, cancellationToken);
        return snapshot.ExamId;
    }

    private static async Task<ApiEnvelope<T>?> ReadEnvelopeAsync<T>(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            if (response.Content.Headers.ContentLength == 0)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(JsonDefaults.Create(), cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static GradingReviseOptions? ParseGradingReviseOptions(string[] arguments, out string error)
    {
        var options = new GradingReviseOptions();
        error = string.Empty;
        for (var index = 0; index < arguments.Length; index++)
        {
            var name = arguments[index];
            if (index + 1 >= arguments.Length)
            {
                error = $"选项 {name} 缺少取值。";
                return null;
            }

            var value = arguments[++index];
            switch (name)
            {
                case "--package": options.PackageRoot = value; break;
                case "--reason": options.Reason = value; break;
                case "--server": options.Server = value; break;
                default:
                    error = $"未知选项 {name}。";
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(options.PackageRoot)) { error = "缺少 --package <材料包目录>。"; return null; }
        if (string.IsNullOrWhiteSpace(options.Reason)) { error = "缺少 --reason <修改原因>。"; return null; }
        return options;
    }

    private sealed class GradingReviseOptions
    {
        public string? Server { get; set; }
        public string? PackageRoot { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class GradingCliException(HttpStatusCode status, string code = "", string message = "")
        : Exception(message)
    {
        public HttpStatusCode Status { get; } = status;
        public string Code { get; } = code;
    }
}
