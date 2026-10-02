using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Importing;

internal static partial class ExamCli
{
    /// <summary>
    /// 走服务端的导入预检与发布。材料位于**服务器本机**可读的目录：编译、组包、加密与版本发布都在服务端完成，
    /// 客户端只提交参数与加密收件人证书，因此大材料不必经网络搬运。
    ///
    /// 预检只到编译与坐标抽取为止；发布在同一准备结果上继续。服务端把失败停在具体阶段并逐条给出问题码，
    /// 本命令据此退出非零，不把「已提交」当成「已发布」。
    /// </summary>
    private static async Task<int> RunImportRemoteAsync(string action, string packageRoot, string[] arguments,
        CancellationToken cancellationToken)
    {
        var userCancellation = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        cancellationToken = deadline.Token;

        if (!TryParseImportOptions(arguments, out var options, out var parseError))
        {
            Console.Error.WriteLine(parseError);
            return 2;
        }

        var server = options.Server ?? Environment.GetEnvironmentVariable(ServerEnvironment);
        if (!TryResolveServer(server, out var address)) return 2;

        IReadOnlyList<PackageRecipient> recipients = [];
        if (action == "publish")
        {
            if (!TryReadRecipients(options.NodeCertificates, out recipients, out var recipientError))
            {
                Console.Error.WriteLine(recipientError);
                return 2;
            }

            if (recipients.Count == 0)
            {
                Console.Error.WriteLine("发布需要至少一个 --node-cert <节点标识>=<证书文件>。");
                return 2;
            }
        }

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = address,
            Timeout = TimeSpan.FromMinutes(30)
        };

        try
        {
            // 请求体按服务端契约的字段名构造；CLI 不引用服务端程序集，保持客户端边界。
            object payload = action == "publish"
                ? new
                {
                    packageRoot,
                    examId = options.ExamId,
                    expectedPaperVersion = options.ExpectedPaperVersion,
                    compilationOutputDirectory = options.CompilationDirectory,
                    packageOutputDirectory = options.PackageOutputDirectory,
                    recipients,
                    reason = options.Reason,
                    requestId = options.RequestId
                }
                : new
                {
                    packageRoot,
                    examId = options.ExamId,
                    expectedPaperVersion = options.ExpectedPaperVersion,
                    compilationOutputDirectory = options.CompilationDirectory,
                    reason = options.Reason
                };
            var body = JsonContent.Create(payload);

            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/imports/{action}") { Content = body };
            AddCredentials(request);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new ImportCliException(response.StatusCode);

            var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<ImportPipelineResult>>(
                JsonDefaults.Create(), cancellationToken);
            if (envelope is null || envelope.Error is not null || envelope.Result is null)
                throw new InvalidDataException("导入响应缺少有效结果。");
            if (envelope.Result.Succeeded && envelope.Result.Stage != ImportStage.Completed)
                throw new InvalidDataException("导入响应声称成功但阶段不是 Completed。");

            Console.WriteLine(JsonSerializer.Serialize(envelope, JsonDefaults.Create()));
            if (!envelope.Result.Succeeded)
            {
                Console.Error.WriteLine($"导入未完成：停在 {envelope.Result.Stage} 阶段。");
                foreach (var issue in envelope.Result.Issues)
                {
                    Console.Error.WriteLine($"  {issue.Code}：{issue.Message}");
                }

                return 4;
            }

            return 0;
        }
        catch (OperationCanceledException) when (!userCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            Console.Error.WriteLine("NETWORK_UNAVAILABLE: 导入请求超时。");
            return 5;
        }
        catch (ImportCliException error)
        {
            Console.Error.WriteLine($"导入请求被拒绝：HTTP {(int)error.Status}。");
            return error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? 3 :
                (int)error.Status >= 500 ? 6 : 4;
        }
    }

    private static bool TryParseImportOptions(string[] arguments, out ImportCliOptions options, out string error)
    {
        options = new ImportCliOptions();
        error = string.Empty;
        for (var index = 0; index < arguments.Length; index++)
        {
            var name = arguments[index];
            if (index + 1 >= arguments.Length)
            {
                error = $"选项 {name} 缺少取值。";
                return false;
            }

            var value = arguments[++index];
            switch (name)
            {
                case "--exam": options.ExamId = value; break;
                case "--version":
                    if (!long.TryParse(value, out var version) || version < 1)
                    {
                        error = "--version 必须是正整数。";
                        return false;
                    }

                    options.ExpectedPaperVersion = version;
                    break;
                case "--compile": options.CompilationDirectory = value; break;
                case "--output": options.PackageOutputDirectory = value; break;
                case "--node-cert": options.NodeCertificates.Add(value); break;
                case "--reason": options.Reason = value; break;
                case "--request-id": options.RequestId = value; break;
                case "--server": options.Server = value; break;
                default:
                    error = $"未知选项 {name}。";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(options.ExamId)) { error = "缺少 --exam <考试标识>。"; return false; }
        if (options.ExpectedPaperVersion < 1) { error = "缺少 --version <试卷版本>。"; return false; }
        if (string.IsNullOrWhiteSpace(options.CompilationDirectory)) { error = "缺少 --compile <编译输出目录>。"; return false; }
        if (string.IsNullOrWhiteSpace(options.Reason)) { error = "缺少 --reason <修改原因>。"; return false; }
        return true;
    }

    private static bool TryReadRecipients(IReadOnlyList<string> certificates,
        out IReadOnlyList<PackageRecipient> recipients, out string error)
    {
        var parsed = new List<PackageRecipient>();
        recipients = parsed;
        error = string.Empty;
        foreach (var item in certificates)
        {
            var separator = item.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || separator == item.Length - 1)
            {
                error = $"--node-cert 需要 <节点标识>=<证书文件>，实际为 {item}。";
                return false;
            }

            var nodeId = item[..separator];
            var path = item[(separator + 1)..];
            if (!File.Exists(path))
            {
                error = $"找不到节点证书：{path}。";
                return false;
            }

            parsed.Add(new PackageRecipient(nodeId, File.ReadAllBytes(path)));
        }

        recipients = parsed;
        return true;
    }

    private sealed class ImportCliOptions
    {
        public string? Server { get; set; }
        public string? ExamId { get; set; }
        public long ExpectedPaperVersion { get; set; }
        public string? CompilationDirectory { get; set; }
        public string? PackageOutputDirectory { get; set; }
        public string? Reason { get; set; }
        public string? RequestId { get; set; }
        public List<string> NodeCertificates { get; } = [];
    }

    private sealed class ImportCliException(HttpStatusCode status) : Exception
    {
        public HttpStatusCode Status { get; } = status;
    }
}
