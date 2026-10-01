using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Grading;

internal static partial class ExamCli
{
    /// <summary>
    /// 读取某场考试的计分状态：当前已发布规则版本、完整答卷范围与计分完成情况。
    /// 与报表命令配合使用——报表门只说「计分尚未完成」，本命令说明还缺哪些答卷、原因是什么。
    /// </summary>
    private static async Task<int> RunGradingAsync(string action, string examId, string[] arguments,
        CancellationToken cancellationToken)
    {
        var userCancellation = cancellationToken;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        cancellationToken = deadline.Token;
        if (action is not "status") return 2;

        var options = ParseOptions(arguments);
        var server = options.Server ?? Environment.GetEnvironmentVariable(ServerEnvironment);
        if (!TryResolveServer(server, out var address)) return 2;

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            BaseAddress = address,
            Timeout = TimeSpan.FromMinutes(5)
        };

        try
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
        catch (OperationCanceledException) when (!userCancellation.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            Console.Error.WriteLine("NETWORK_UNAVAILABLE: 计分状态请求超时。");
            return 5;
        }
        catch (GradingCliException error)
        {
            Console.Error.WriteLine($"计分状态请求被拒绝：HTTP {(int)error.Status}。");
            return error.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? 3 :
                (int)error.Status >= 500 ? 6 : 4;
        }
    }

    private sealed class GradingCliException(HttpStatusCode status) : Exception
    {
        public HttpStatusCode Status { get; } = status;
    }
}
