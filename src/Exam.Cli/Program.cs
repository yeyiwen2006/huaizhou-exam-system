using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Common;

return await ExamCli.RunAsync(args, CancellationToken.None);

internal static class ExamCli
{
    private const string ServerEnvironment = "EXAM_SERVER_URL";
    private const string OperatorTokenEnvironment = "EXAM_OPERATOR_TOKEN";
    private const string DeviceIdEnvironment = "EXAM_DEVICE_ID";
    private const string DeviceCredentialEnvironment = "EXAM_DEVICE_CREDENTIAL";
    private const string InsecureLoopbackEnvironment = "EXAM_ALLOW_INSECURE_LOOPBACK";

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            return args switch
            {
                ["version"] => PrintVersion(),
                ["error-codes"] => PrintErrorCodes(),
                [] or ["help"] or ["--help"] or ["-h"] => PrintHelp(),
                ["health", .. var options] => await SendAsync(HttpMethod.Get, "/health/ready", options, cancellationToken),
                ["time", .. var options] => await SendAsync(HttpMethod.Get, "/api/v1/commands/time", options, cancellationToken),
                ["management", var command, .. var options] => await ManagementCli.RunAsync(command, options, cancellationToken),
                ["api", var method, var path, .. var options] => await SendAsync(
                    new HttpMethod(method.ToUpperInvariant()),
                    path,
                    options,
                    cancellationToken),
                _ => UnknownCommand(args)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine("操作已取消。");
            return 130;
        }
        catch (HttpRequestException exception)
        {
            Console.Error.WriteLine($"NETWORK_UNAVAILABLE: {exception.Message}");
            return 5;
        }
        catch (TaskCanceledException exception)
        {
            Console.Error.WriteLine($"NETWORK_UNAVAILABLE: {exception.Message}");
            return 5;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"CLI_ERROR: {exception.Message}");
            return 6;
        }
    }

    private static int PrintVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "unknown";
        Console.WriteLine(version);
        return 0;
    }

    private static int PrintErrorCodes()
    {
        var values = typeof(ErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Console.WriteLine(JsonSerializer.Serialize(values, JsonDefaults.Create()));
        return 0;
    }

    private static int PrintHelp()
    {
        Console.WriteLine("怀籀传承在线考试系统 CLI");
        Console.WriteLine("  health [--server URL]                 检查业务服务及数据库是否就绪");
        Console.WriteLine("  time [--server URL]                   获取带服务身份的服务器时间");
        Console.WriteLine("  api METHOD PATH [选项]                调用任意正式 HTTP API");
        Console.WriteLine("  management COMMAND [选项]             调用独立本机管理宿主（status/configuration/logs/runtime-logs/operation/start/stop/checks/configure/backup/restore）");
        Console.WriteLine("  version                               显示当前程序版本");
        Console.WriteLine("  error-codes                           以 JSON 输出公共错误码");
        Console.WriteLine();
        Console.WriteLine("API 选项：");
        Console.WriteLine("  --server URL                          服务地址；默认读取 EXAM_SERVER_URL");
        Console.WriteLine("  --input FILE|-                        从 UTF-8 JSON 文件或标准输入读取请求体");
        Console.WriteLine("  --request-id VALUE                    设置 X-Request-Id 请求头");
        Console.WriteLine();
        Console.WriteLine("管理选项：--management-url HTTPS_URL --server-id ID --node-id ID；写操作用 --input FILE|-，operation 用 --request-id ID。");
        Console.WriteLine("也可用 EXAM_MANAGEMENT_URL、EXAM_SERVER_ID、EXAM_NODE_ID；令牌仍由 EXAM_OPERATOR_TOKEN 提供。");
        Console.WriteLine();
        Console.WriteLine("考务令牌只从 EXAM_OPERATOR_TOKEN 读取；设备身份只从 EXAM_DEVICE_ID 和");
        Console.WriteLine("EXAM_DEVICE_CREDENTIAL 读取，不接受命令行明文，以免进入命令历史和进程列表。");
        Console.WriteLine();
        Console.WriteLine("示例：exam-cli api POST /api/v1/activation/approve --input approve.json");
        return 0;
    }

    private static async Task<int> SendAsync(
        HttpMethod method,
        string path,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(arguments);
        var server = options.Server ?? Environment.GetEnvironmentVariable(ServerEnvironment);
        if (string.IsNullOrWhiteSpace(server) || !Uri.TryCreate(server, UriKind.Absolute, out var baseUri))
        {
            Console.Error.WriteLine($"必须用 --server 或 {ServerEnvironment} 提供有效的 HTTPS 服务地址。");
            return 2;
        }

        var insecureLoopback = baseUri.Scheme == Uri.UriSchemeHttp &&
                               IPAddress.TryParse(baseUri.Host, out var address) &&
                               IPAddress.IsLoopback(address) &&
                               string.Equals(
                                   Environment.GetEnvironmentVariable(InsecureLoopbackEnvironment),
                                   "true",
                                   StringComparison.OrdinalIgnoreCase);
        if (baseUri.Scheme != Uri.UriSchemeHttps && !insecureLoopback)
        {
            Console.Error.WriteLine(
                $"拒绝通过明文 HTTP 发送考务或设备凭据。仅本机调试可显式设置 {InsecureLoopbackEnvironment}=true。");
            return 2;
        }

        if (!path.StartsWith("/", StringComparison.Ordinal) ||
            !Uri.TryCreate(baseUri, path, out var requestUri) ||
            requestUri.Host != baseUri.Host || requestUri.Port != baseUri.Port)
        {
            Console.Error.WriteLine("API PATH 必须是以 / 开头、且仍指向同一服务的相对路径。");
            return 2;
        }

        using var client = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromMinutes(65) };
        using var request = new HttpRequestMessage(method, requestUri);
        AddCredentials(request);
        if (!string.IsNullOrWhiteSpace(options.RequestId))
        {
            request.Headers.TryAddWithoutValidation("X-Request-Id", options.RequestId);
        }

        if (options.Input is not null)
        {
            var json = options.Input == "-"
                ? await Console.In.ReadToEndAsync(cancellationToken)
                : await File.ReadAllTextAsync(Path.GetFullPath(options.Input), Encoding.UTF8, cancellationToken);
            try
            {
                using var _ = JsonDocument.Parse(json);
            }
            catch (JsonException exception)
            {
                Console.Error.WriteLine($"输入不是有效 JSON：{exception.Message}");
                return 2;
            }

            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var output = FormatJson(body);
        if (response.IsSuccessStatusCode)
        {
            Console.WriteLine(output);
            return 0;
        }

        Console.Error.WriteLine(output);
        return response.StatusCode switch
        {
            HttpStatusCode.BadRequest => 2,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => 3,
            HttpStatusCode.NotFound => 4,
            HttpStatusCode.Conflict => 4,
            HttpStatusCode.ServiceUnavailable => 6,
            _ when (int)response.StatusCode >= 500 => 6,
            _ => 4
        };
    }

    private static CliOptions ParseOptions(IReadOnlyList<string> arguments)
    {
        string? server = null;
        string? input = null;
        string? requestId = null;
        for (var index = 0; index < arguments.Count; index++)
        {
            var name = arguments[index];
            if (name is not ("--server" or "--input" or "--request-id") || index + 1 >= arguments.Count)
            {
                throw new ArgumentException($"无法识别或缺少值的选项：{name}");
            }

            var value = arguments[++index];
            switch (name)
            {
                case "--server": server = value; break;
                case "--input": input = value; break;
                case "--request-id": requestId = value; break;
            }
        }

        return new CliOptions(server, input, requestId);
    }

    private static void AddCredentials(HttpRequestMessage request)
    {
        var operatorToken = Environment.GetEnvironmentVariable(OperatorTokenEnvironment);
        if (!string.IsNullOrWhiteSpace(operatorToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", operatorToken);
        }

        var deviceId = Environment.GetEnvironmentVariable(DeviceIdEnvironment);
        var deviceCredential = Environment.GetEnvironmentVariable(DeviceCredentialEnvironment);
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            request.Headers.TryAddWithoutValidation("X-Exam-Device-Id", deviceId);
        }

        if (!string.IsNullOrWhiteSpace(deviceCredential))
        {
            request.Headers.TryAddWithoutValidation("X-Exam-Device-Credential", deviceCredential);
        }
    }

    private static string FormatJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static int UnknownCommand(IReadOnlyList<string> arguments)
    {
        Console.Error.WriteLine($"未知命令：{string.Join(' ', arguments)}");
        Console.Error.WriteLine("运行 exam-cli help 查看可用命令。");
        return 2;
    }

    private sealed record CliOptions(string? Server, string? Input, string? RequestId);
}
