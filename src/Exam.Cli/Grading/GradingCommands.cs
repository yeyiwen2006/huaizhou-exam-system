using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Grading;
using Exam.Contracts.Importing;

internal static partial class ExamCli
{
    /// <summary>
    /// 计分相关命令：
    /// - `grading status EXAM`：读取当前已发布规则版本、完整答卷范围与计分完成情况。
    ///   与报表命令配合使用——报表门只说「计分尚未完成」，本命令说明还缺哪些答卷、原因是什么。
    /// - `grading revise EXAM --package 目录 --reason 说明 [--version N]`：只改**答案与计分规则**
    ///   （不换试卷版本、不重新组包），发布新的规则版本并按新版本触发重算。
    ///   规则发布与重算完成分开报告：规则已发布但重算没跑起来时如实报出，不把「规则已换」说成「成绩已重算完」。
    /// </summary>
    private static async Task<int> RunGradingAsync(string action, string examId, string[] arguments,
        CancellationToken cancellationToken)
    {
        var userCancellation = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        cancellationToken = deadline.Token;
        if (action is not ("status" or "revise")) return 2;

        var parseError = string.Empty;
        var options = action == "revise" ? ParseGradingReviseOptions(arguments, out parseError) : null;
        if (action == "revise" && options is null)
        {
            Console.Error.WriteLine(parseError);
            return 2;
        }

        // 两个命令的选项集不同：status 只有通用选项，revise 另有 --package/--version/--reason。
        var server = action == "revise"
            ? options!.Server ?? Environment.GetEnvironmentVariable(ServerEnvironment)
            : ParseOptions(arguments).Server ?? Environment.GetEnvironmentVariable(ServerEnvironment);
        if (!TryResolveServer(server, out var address)) return 2;

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = address,
            Timeout = TimeSpan.FromMinutes(30)
        };

        try
        {
            return action == "revise"
                ? await ReviseGradingRulesAsync(client, examId, options!, cancellationToken)
                : await ReadGradingStatusAsync(client, examId, cancellationToken);
        }
        catch (OperationCanceledException) when (!userCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            Console.Error.WriteLine("NETWORK_UNAVAILABLE: 计分请求超时。");
            return 5;
        }
        catch (GradingCliException error)
        {
            Console.Error.WriteLine($"计分请求被拒绝：HTTP {(int)error.Status}。");
            return error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? 3 :
                (int)error.Status >= 500 ? 6 : 4;
        }
    }

    private static async Task<int> ReadGradingStatusAsync(HttpClient client, string examId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/grading/" + Uri.EscapeDataString(examId) + "/status");
        AddCredentials(request);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new GradingCliException(response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<GradingStatusReport>>(
            JsonDefaults.Create(), cancellationToken);
        if (envelope is null || envelope.Error is not null || envelope.Result is null ||
            string.IsNullOrWhiteSpace(envelope.ServerId) || string.IsNullOrWhiteSpace(envelope.NodeId) ||
            envelope.ServerEpoch <= 0)
        {
            throw new InvalidDataException("计分状态响应缺少有效结果或服务器身份。");
        }

        if (envelope.Result.ExamId != examId) throw new InvalidDataException("计分状态考试不一致。");
        Console.WriteLine(JsonSerializer.Serialize(envelope, JsonDefaults.Create()));
        return 0;
    }

    private static async Task<int> ReviseGradingRulesAsync(HttpClient client, string examId,
        GradingReviseOptions options, CancellationToken cancellationToken)
    {
        var payload = new
        {
            packageRoot = options.PackageRoot,
            gradingVersion = options.GradingVersion,
            reason = options.Reason,
            requestId = options.RequestId ?? string.Empty
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post, "/api/v1/imports/" + Uri.EscapeDataString(examId) + "/rule-revisions")
        {
            Content = JsonContent.Create(payload)
        };
        AddCredentials(request);
        if (!string.IsNullOrWhiteSpace(options.RequestId))
        {
            request.Headers.TryAddWithoutValidation("X-Request-Id", options.RequestId);
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new GradingCliException(response.StatusCode);

        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<GradingRuleRevisionResult>>(
            JsonDefaults.Create(), cancellationToken);
        if (envelope is null || envelope.Error is not null || envelope.Result is null)
        {
            throw new InvalidDataException("规则修订响应缺少有效结果。");
        }

        Console.WriteLine(JsonSerializer.Serialize(envelope, JsonDefaults.Create()));
        var result = envelope.Result;
        if (!result.Succeeded)
        {
            Console.Error.WriteLine("规则修订未完成。");
            foreach (var issue in result.Issues)
            {
                Console.Error.WriteLine($"  {issue.Code}：{issue.Message}");
            }

            return 4;
        }

        if (result.AlreadyInEffect)
        {
            // 内容没变就不推新版本：说清楚是「已是该规则」，不是「又发布了一次」。
            Console.Error.WriteLine($"材料与当前已发布规则内容一致，未发布新版本（当前规则版本 {result.PublishedGradingVersion}）。");
            return 0;
        }

        // 规则已经发布；重算范围与就绪单独说明，不把两者混成一句话。
        Console.Error.WriteLine($"规则版本 {result.PublishedGradingVersion} 已发布。");
        Console.Error.WriteLine($"重算范围 {result.SubmissionCount} 份答卷，本次入队 {result.Enqueued} 份；" +
            $"就绪：{(result.Readiness?.RecalculationReady == true ? "是" : "否")}" +
            (result.Readiness is { Blockers.Count: > 0 } readiness
                ? $"（阻断：{string.Join("；", readiness.Blockers)}）"
                : string.Empty));
        if (result.RecalculationError is not null)
        {
            Console.Error.WriteLine($"重算未触发：{result.RecalculationError}");
        }

        return 0;
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
                case "--version":
                    if (!long.TryParse(value, out var version) || version < 0)
                    {
                        error = "--version 必须是非负整数（0 表示由服务器取当前规则版本的下一版）。";
                        return null;
                    }

                    options.GradingVersion = version;
                    break;
                case "--reason": options.Reason = value; break;
                case "--request-id": options.RequestId = value; break;
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
        public long GradingVersion { get; set; }
        public string? Reason { get; set; }
        public string? RequestId { get; set; }
    }

    private sealed class GradingCliException(HttpStatusCode status) : Exception
    {
        public HttpStatusCode Status { get; } = status;
    }
}
