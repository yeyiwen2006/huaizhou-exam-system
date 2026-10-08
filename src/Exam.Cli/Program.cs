using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Common;

// Console defaults follow the Windows locale; redirected Chinese diagnostics must not become question marks.
// Both stdout and stderr are UTF-8 without a BOM, including controlled success/error output.
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
return await ExamCli.RunAsync(args, cancellation.Token);

internal static partial class ExamCli
{
    private const string ServerEnvironment = "EXAM_SERVER_URL";
    private const string OperatorTokenEnvironment = "EXAM_OPERATOR_TOKEN";
    private const string DeviceIdEnvironment = "EXAM_DEVICE_ID";
    private const string DeviceCredentialEnvironment = "EXAM_DEVICE_CREDENTIAL";
    private const string InsecureLoopbackEnvironment = "EXAM_ALLOW_INSECURE_LOOPBACK";

    private static async Task<int> DispatchAsync(string[] args, CancellationToken cancellationToken)
    {
        try
        {
            return args switch
            {
                ["version"] => PrintVersion(),
                ["artifact-info", var file] => PrintArtifactInfo(file),
                ["error-codes"] => PrintErrorCodes(),
                [] or ["help"] or ["--help"] or ["-h"] => PrintHelp(),
                ["health", .. var options] => await SendAsync(HttpMethod.Get, "/health/ready", options, cancellationToken),
                ["time", .. var options] => await SendAsync(HttpMethod.Get, "/api/v1/commands/time", options, cancellationToken),
                ["exams", .. var options] => await RunExamsAsync(options, cancellationToken),
                [var administration, .. var options] when administration is "roster" or "rooms" or "devices" or "monitor" or "publications" or "activation" or "decisions" or "commands" =>
                    await RunAdministrationAsync(administration, options, cancellationToken),
                [var account, .. var options] when account is "register" or "login" or "logout" or "whoami" =>
                    await RunAccountAsync(account, options, cancellationToken),
                ["operators", "list", .. var options] => await RunAccountAsync("list", options, cancellationToken),
                ["operators", var change, .. var options] when change is "approve" or "disable" or "enable" =>
                    await RunAccountAsync(change, options, cancellationToken),
                ["reports", var action, var examId, .. var options] => await RunReportAsync(action, examId, options, cancellationToken),
                ["grading", var action, var examId, .. var options] => await RunGradingAsync(action, examId, options, cancellationToken),
                ["import", var action, var packageRoot, .. var options] => await RunImportAsync(action, packageRoot, options, cancellationToken),
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
        catch (ArgumentException)
        {
            return 2;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return 2;
        }
        catch (HttpRequestException)
        {
            return 5;
        }
        catch (TaskCanceledException)
        {
            return 5;
        }
        catch (Exception)
        {
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
        Console.WriteLine("  register [--username 用户名 --name 姓名]  注册考务账号（第一个账号为管理员，其余需批准）");
        Console.WriteLine("  login [--username 用户名]             登录考务账号，会话加密保存在本机供后续命令使用");
        Console.WriteLine("  logout | whoami                       退出登录 / 查看当前登录账号");
        Console.WriteLine("  operators list                        列出考务账号（管理员）");
        Console.WriteLine("  operators approve|disable|enable 用户名  批准、停用或重新启用考务账号（管理员）");
        Console.WriteLine("  exams list                            列出未隐藏的考试（名称、时间、状态、是否在学生端显示）");
        Console.WriteLine("  exams hidden                          列出对当前账号生效的隐藏考试及范围");
        Console.WriteLine("  exams hide|unhide 名称 self|all       隐藏或取消隐藏：当前账号 / 所有考务账号");
        Console.WriteLine("  exams show 名称                       查看考试设置与考试说明");
        Console.WriteLine("  exams create --input FILE             新建考试（name、schedule、settings）");
        Console.WriteLine("  exams schedule NAME --input FILE      修改时间（expectedVersion、schedule、reason）");
        Console.WriteLine("  exams update 名称 --input FILE        修改考试名称及科目题组设置（开考前）");
        Console.WriteLine("  exams visibility 名称 on|off          设置是否在学生端显示");
        Console.WriteLine("  roster list 名称                     查询已保存名册（保留班级与准考证号前导零）");
        Console.WriteLine("  roster import 名称 --input FILE      整批校验并原子导入，失败输出逐行问题且退出码非零");
        Console.WriteLine("  roster update|delete 名称 准考证号   更正或删除尚未入场的考生；update 使用 --input FILE");
        Console.WriteLine("  rooms list|create 名称               查询试场或创建试场");
        Console.WriteLine("  rooms rename 原名称 新名称           按版本重命名试场");
        Console.WriteLine("  rooms delete 名称                    仅允许删除没有设备的试场");
        Console.WriteLine("  rooms move 名称|未分组 --input FILE  按公开学生端编号整批转组（clientNumbers、reason）");
        Console.WriteLine("  devices list [试场]|show 编号|history 编号  查询设备、当前考生、连接状态或转组历史");
        Console.WriteLine("  monitor show|missing 名称            查询权威监控快照或未回收名单");
        Console.WriteLine("  monitor stream 名称 [间隔秒] [轮数]    持续输出权威监测快照（默认1秒，失败重试）");
        Console.WriteLine("  monitor watch 名称 [起始名单版本]    读取并发出“本场考试全部答卷已成功回收”事件后退出");
        Console.WriteLine("  monitor follow 名称 [起始名单版本] [间隔秒] [轮数]  持续发出收齐事件（默认 1 秒，不限轮数）");
        Console.WriteLine("  monitor events 名称 [起始名单版本] [间隔秒] [轮数]");
        Console.WriteLine("                                        订阅监测事件：收齐、设备冲突与失败（给间隔秒则持续订阅）");
        Console.WriteLine("  decisions keep 考试名称 学生端编号 --input FILE  设备冲突裁决（请求体给出 device 与 reason）");
        Console.WriteLine("  commands resend 考试名称 学生端编号 start|end   重发该考生仍可重发的开始或结束指令");
        Console.WriteLine("  commands reupload 考试名称 学生端编号      请求补传原答卷（重发未回收考生的结束指令）");
        Console.WriteLine("  publications publish 名称 --input FILE  原子激活已验证的试卷及计分规则");
        Console.WriteLine("  reports status|show EXAM [--server URL]  查看报表进度或同版五表");
        Console.WriteLine("  reports export EXAM FILE [--server URL]  下载并完整保存 Excel");
        Console.WriteLine("  grading status EXAM [--server URL]     读取当前规则版本、答卷范围及计分就绪状态（EXAM 为考试名称）");
        Console.WriteLine("  grading revise 考试 --package 目录 --reason 说明 --output 新目录");
        Console.WriteLine("                                        保存私有规则预览，不编译、不发布；先核对完整答案与满分");
        Console.WriteLine("  grading publish 规则预览目录 --confirm  发布同一已核对快照；重复执行安全重放原请求");
        Console.WriteLine("  import verify 包目录                  本地校验并逐题编译、抽取选项坐标（不连服务器）");
        Console.WriteLine("  import preview 本地目录或ZIP --exam 考试名称 --output 新预览目录 [--server URL]");
        Console.WriteLine("                                        在本机检查、编译并保存私有逐题预览；尚未发布");
        Console.WriteLine("                                        开考后修订再加：--reason 说明（必填）");
        Console.WriteLine("                                        --changed 题号[,题号…] 声明变更题号（省略时用按内容算出的差异）");
        Console.WriteLine("  import publish 预览目录 --confirm [--server URL]");
        Console.WriteLine("                                        确认同一预览后加密上传，原请求可安全重试");
        Console.WriteLine("  import materials 考试名称 --output 新目录 [--server URL]");
        Console.WriteLine("                                        取回当前已发布版本的源材料（含独立答案），用于继续修题");
        Console.WriteLine("  management COMMAND [选项]             调用独立本机管理宿主（status/ha-status/configuration/logs/runtime-logs/operation/start/stop/checks/configure/backup/restore/takeover/emergency/rebuild/reprotect）");
        Console.WriteLine("  version                               显示当前程序版本");
        Console.WriteLine("  artifact-info FILE                    只读核对发布程序集实际构建元数据");
        Console.WriteLine("  error-codes                           以 JSON 输出公共错误码");
        Console.WriteLine();
        Console.WriteLine("API 选项：");
        Console.WriteLine("  --server URL                          服务地址；默认读取 EXAM_SERVER_URL");
        Console.WriteLine("  --input FILE|-                        从 UTF-8 JSON 文件或标准输入读取请求体");
        Console.WriteLine("  --request-id VALUE                    设置 X-Request-Id 请求头");
        Console.WriteLine("  --error-format text|json               公共错误格式（默认 text，写入 stderr）");
        Console.WriteLine("  --timeout-seconds 1..3900              本次命令的总超时；不改变业务命令原有较短时限");
        Console.WriteLine();
        Console.WriteLine("管理选项：--management-url HTTPS_URL --server-id ID --node-id ID；写操作用 --input FILE|-，operation 用 --request-id ID。");
        Console.WriteLine("也可用 EXAM_MANAGEMENT_URL、EXAM_SERVER_ID、EXAM_NODE_ID；先执行 management login 用考务账号登录本机服务管理。");
        Console.WriteLine();
        Console.WriteLine("考务身份来自 login 保存的会话（也可用 EXAM_OPERATOR_TOKEN 指定会话令牌）；设备身份只从 EXAM_DEVICE_ID 和");
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
        if (!TryResolveServer(server, out var baseUri))
        {
            Console.Error.WriteLine($"必须用 --server 或 {ServerEnvironment} 提供有效的 HTTPS 服务地址。");
            return 2;
        }

        if (!path.StartsWith("/", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\') ||
            !Uri.TryCreate(baseUri, path, out var requestUri) ||
            requestUri.Scheme != baseUri.Scheme || requestUri.Host != baseUri.Host || requestUri.Port != baseUri.Port)
        {
            Console.Error.WriteLine("API PATH 必须是以 / 开头、且仍指向同一服务的相对路径。");
            return 2;
        }

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { BaseAddress = baseUri, Timeout = TimeSpan.FromMinutes(65) };
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
            if (name is not ("--server" or "--input" or "--request-id") || index + 1 >= arguments.Count ||
                arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("选项无法识别或缺少值。");
            }

            var value = arguments[++index];
            switch (name)
            {
                case "--server": server = SetOptionOnce(server, value); break;
                case "--input": input = SetOptionOnce(input, value); break;
                case "--request-id": requestId = SetOptionOnce(requestId, value); break;
            }
        }

        return new CliOptions(server, input, requestId);
    }

    private static void AddCredentials(HttpRequestMessage request)
    {
        var operatorToken = OperatorToken();
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
        Console.Error.WriteLine("未知命令。");
        Console.Error.WriteLine("运行 exam-cli help 查看可用命令。");
        return 2;
    }

    private sealed record CliOptions(string? Server, string? Input, string? RequestId);

    private static string SetOptionOnce(string? previous, string value) =>
        previous is null && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("选项重复或为空。");
}
