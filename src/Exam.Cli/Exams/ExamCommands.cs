using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Domain.Exams;

/// <summary>
/// 考试目录 CLI，与考务端主页使用同一组服务器接口。考试一律按唯一的考试名称指定，
/// 不需要输入内部编号。
/// </summary>
internal static partial class ExamCli
{
    private static async Task<int> RunExamsAsync(string[] arguments, CancellationToken cancellationToken)
    {
        // exams list | show NAME | create --input FILE | update NAME --input FILE | visibility NAME on|off
        var (action, name, rest) = arguments switch
        {
            ["list", .. var tail] => ("list", (string?)null, tail),
            ["create", .. var tail] => ("create", null, tail),
            ["show", var exam, .. var tail] => ("show", exam, tail),
            ["update", var exam, .. var tail] => ("update", exam, tail),
            ["visibility", var exam, var state, .. var tail] when state is "on" or "off" => ("visibility-" + state, exam, tail),
            _ => ((string?)null, (string?)null, Array.Empty<string>()),
        };
        if (action is null) return UnknownCommand(["exams", .. arguments]);
        var options = ParseOptions(rest);
        var server = options.Server ?? Environment.GetEnvironmentVariable(ServerEnvironment);
        if (!Uri.TryCreate(server, UriKind.Absolute, out var address) || address.UserInfo.Length != 0 ||
            address.Scheme != "https" && !(address.Scheme == "http" && IPAddress.TryParse(address.Host, out var ip) &&
                IPAddress.IsLoopback(ip) && Environment.GetEnvironmentVariable(InsecureLoopbackEnvironment) == "true"))
        {
            Console.Error.WriteLine($"必须用 --server 或 {ServerEnvironment} 提供有效的 HTTPS 服务地址。");
            return 2;
        }
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = address, Timeout = TimeSpan.FromMinutes(2) };
        var json = JsonDefaults.Create();

        async Task<T> Send<T>(HttpMethod method, string path, object? body) where T : class
        {
            using var request = new HttpRequestMessage(method, path);
            AddCredentials(request);
            if (body is not null) request.Content = JsonContent.Create(body, options: json);
            using var response = await client.SendAsync(request, cancellationToken);
            var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(json, cancellationToken);
            if (!response.IsSuccessStatusCode || envelope?.Error is not null || envelope?.Result is null)
                throw new ExamCliException(response.StatusCode, envelope?.Error?.Message ?? $"服务器未完成本次操作（{(int)response.StatusCode}）。");
            if (string.IsNullOrWhiteSpace(envelope.ServerId) || envelope.ServerEpoch < 1)
                throw new InvalidDataException("响应缺少服务器身份。");
            return envelope.Result;
        }

        async Task<ExamSummary> Find(string examName)
        {
            var key = ExamSettingsRules.NameKey(examName);
            return (await Send<ExamSummary[]>(HttpMethod.Get, "/api/v1/exams", null))
                .SingleOrDefault(item => ExamSettingsRules.NameKey(item.Name) == key)
                ?? throw new ExamCliException(HttpStatusCode.NotFound, $"没有名为“{examName}”的考试。");
        }

        async Task<string> ReadInput()
        {
            if (options.Input is null) throw new ArgumentException("需要用 --input FILE|- 提供 JSON。");
            return options.Input == "-"
                ? await Console.In.ReadToEndAsync(cancellationToken)
                : await File.ReadAllTextAsync(Path.GetFullPath(options.Input), Encoding.UTF8, cancellationToken);
        }

        string RequestId() => string.IsNullOrWhiteSpace(options.RequestId) ? Guid.NewGuid().ToString("N") : options.RequestId;
        object result;
        try
        {
            switch (action)
            {
                case "list":
                    result = await Send<ExamSummary[]>(HttpMethod.Get, "/api/v1/exams", null);
                    break;
                case "show":
                    result = await Send<ExamDetail>(HttpMethod.Get, "/api/v1/exams/" + Uri.EscapeDataString((await Find(name!)).ExamId), null);
                    break;
                case "create":
                    // 输入为 { name, schedule: { startsAt, endsAt }, settings }；未给出 requestId 时自动生成。
                    var create = JsonSerializer.Deserialize<CreateExamRequest>(await ReadInput(), json)
                        ?? throw new ArgumentException("输入不是有效的新建考试内容。");
                    result = await Send<ExamDetail>(HttpMethod.Post, "/api/v1/exams",
                        create with { RequestId = string.IsNullOrWhiteSpace(create.RequestId) ? RequestId() : create.RequestId });
                    break;
                case "update":
                    // 输入为 { name, settings, expectedVersion? }；未给出版本时使用服务器当前版本。
                    var target = await Find(name!);
                    var update = JsonSerializer.Deserialize<UpdateExamSettingsRequest>(await ReadInput(), json)
                        ?? throw new ArgumentException("输入不是有效的考试设置。");
                    result = await Send<ExamDetail>(HttpMethod.Put, $"/api/v1/exams/{Uri.EscapeDataString(target.ExamId)}/settings",
                        update with
                        {
                            RequestId = string.IsNullOrWhiteSpace(update.RequestId) ? RequestId() : update.RequestId,
                            ExpectedVersion = update.ExpectedVersion > 0 ? update.ExpectedVersion : target.SettingsVersion,
                        });
                    break;
                default:
                    var exam = await Find(name!);
                    result = await Send<ExamSummary>(HttpMethod.Put, $"/api/v1/exams/{Uri.EscapeDataString(exam.ExamId)}/visibility",
                        new SetExamVisibilityRequest(RequestId(), action == "visibility-on"));
                    break;
            }
        }
        catch (ExamCliException error)
        {
            Console.Error.WriteLine(error.Message);
            return error.Status switch
            {
                HttpStatusCode.BadRequest => 2,
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => 3,
                HttpStatusCode.ServiceUnavailable => 6,
                var status when (int)status >= 500 => 6,
                _ => 4,
            };
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidDataException)
        {
            Console.Error.WriteLine("输入或响应无效：" + error.Message);
            return 2;
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine("无法连接服务器。");
            return 5;
        }
        Console.WriteLine(JsonSerializer.Serialize(result, json));
        return 0;
    }

    private sealed class ExamCliException(HttpStatusCode status, string message) : Exception(message)
    {
        public HttpStatusCode Status { get; } = status;
    }
}
