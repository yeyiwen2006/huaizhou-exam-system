using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Operators;

/// <summary>考务账号 CLI：register / login / logout / whoami / operators list|approve|disable|enable。</summary>
internal static partial class ExamCli
{
    internal const string BusinessSessionKind = "business";
    internal const string ManagementSessionKind = "management";

    /// <summary>业务请求使用的会话令牌：环境变量优先，其次是 login 保存的会话。</summary>
    internal static string? OperatorToken() =>
        Environment.GetEnvironmentVariable(OperatorTokenEnvironment) is { Length: > 0 } token
            ? token
            : CliSessionStore.Load(BusinessSessionKind);

    private static async Task<int> RunAccountAsync(string command, string[] arguments, CancellationToken cancellationToken)
    {
        string? username = null;
        string? displayName = null;
        var rest = new List<string>();
        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index] == "--username" && index + 1 < arguments.Length) username = arguments[++index];
            else if (arguments[index] == "--name" && index + 1 < arguments.Length) displayName = arguments[++index];
            else rest.Add(arguments[index]);
        }
        var target = command is "approve" or "disable" or "enable" && rest.Count > 0 && !rest[0].StartsWith("--", StringComparison.Ordinal)
            ? rest[0] : null;
        if (target is not null) rest.RemoveAt(0);
        var options = ParseOptions(rest);
        var server = options.Server ?? Environment.GetEnvironmentVariable(ServerEnvironment);
        if (!Uri.TryCreate(server, UriKind.Absolute, out var address) ||
            address.Scheme != "https" && !(address.Scheme == "http" && IPAddress.TryParse(address.Host, out var ip) &&
                IPAddress.IsLoopback(ip) && Environment.GetEnvironmentVariable(InsecureLoopbackEnvironment) == "true"))
        {
            Console.Error.WriteLine($"必须用 --server 或 {ServerEnvironment} 提供有效的 HTTPS 服务地址。");
            return 2;
        }
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = address, Timeout = TimeSpan.FromMinutes(1) };
        var json = JsonDefaults.Create();

        async Task<T> Send<T>(HttpMethod method, string path, object? body, bool authenticated) where T : class
        {
            using var request = new HttpRequestMessage(method, path);
            if (authenticated)
            {
                var token = OperatorToken() ?? throw new AccountCliException(HttpStatusCode.Unauthorized, "尚未登录，请先执行 login。");
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            }
            if (body is not null) request.Content = JsonContent.Create(body, options: json);
            using var response = await client.SendAsync(request, cancellationToken);
            ApiEnvelope<T>? envelope = null;
            try { envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(json, cancellationToken); }
            catch (JsonException) { }
            if (!response.IsSuccessStatusCode || envelope?.Error is not null || envelope?.Result is null)
                throw new AccountCliException(response.StatusCode, envelope?.Error?.Message ?? $"服务器未完成本次操作（{(int)response.StatusCode}）。");
            return envelope.Result;
        }

        try
        {
            switch (command)
            {
                case "register":
                {
                    username ??= Prompt("用户名：");
                    displayName ??= Prompt("姓名：");
                    var password = ReadPassword("口令：");
                    if (password != ReadPassword("再次输入口令：")) { Console.Error.WriteLine("两次输入的口令不一致。"); return 2; }
                    var result = await Send<RegisterOperatorResult>(HttpMethod.Post, "/api/v1/operators/register",
                        new RegisterOperatorRequest(username, displayName, password), authenticated: false);
                    Console.WriteLine(result.Account.Status == OperatorStatus.Active
                        ? "注册成功。这是服务器上的第一个账号，已成为管理员，可以直接登录。"
                        : "注册成功，正在等待管理员批准，批准后才能登录。");
                    return 0;
                }
                case "login":
                {
                    username ??= Prompt("用户名：");
                    var password = ReadPassword("口令：");
                    var session = await Send<OperatorSession>(HttpMethod.Post, "/api/v1/operators/login",
                        new LoginOperatorRequest(username, password), authenticated: false);
                    CliSessionStore.Save(BusinessSessionKind, session.Token);
                    Console.WriteLine($"已登录：{session.Account.DisplayName}（{session.Account.Username}）{(session.Account.IsAdministrator ? "，管理员" : string.Empty)}。");
                    return 0;
                }
                case "logout":
                {
                    if (OperatorToken() is not null)
                    {
                        try { await Send<object>(HttpMethod.Post, "/api/v1/operators/logout", null, authenticated: true); }
                        catch (AccountCliException) { /* 会话已失效时仍删除本机保存的会话 */ }
                    }
                    CliSessionStore.Delete(BusinessSessionKind);
                    Console.WriteLine("已退出登录。");
                    return 0;
                }
                case "whoami":
                {
                    var me = await Send<OperatorAccount>(HttpMethod.Get, "/api/v1/operators/me", null, authenticated: true);
                    Console.WriteLine(JsonSerializer.Serialize(me, json));
                    return 0;
                }
                case "list":
                {
                    var accounts = await Send<OperatorAccount[]>(HttpMethod.Get, "/api/v1/operators", null, authenticated: true);
                    Console.WriteLine(JsonSerializer.Serialize(accounts, json));
                    return 0;
                }
                case "approve" or "enable" or "disable" when target is not null:
                {
                    var status = command == "disable" ? OperatorStatus.Disabled : OperatorStatus.Active;
                    var account = await Send<OperatorAccount>(HttpMethod.Put, "/api/v1/operators/status",
                        new SetOperatorStatusRequest(target, status), authenticated: true);
                    Console.WriteLine(JsonSerializer.Serialize(account, json));
                    return 0;
                }
                default:
                    return UnknownCommand([command, .. arguments]);
            }
        }
        catch (AccountCliException error)
        {
            Console.Error.WriteLine(error.Message);
            return error.Status switch
            {
                HttpStatusCode.BadRequest => 2,
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => 3,
                var status when (int)status >= 500 => 6,
                _ => 4,
            };
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine("无法连接服务器。");
            return 5;
        }
    }

    private static string Prompt(string label)
    {
        Console.Error.Write(label);
        return Console.ReadLine()?.Trim() ?? string.Empty;
    }

    /// <summary>口令从标准输入读取；在终端中不回显。重定向输入时按行读取，便于自动化。</summary>
    internal static string ReadPassword(string label)
    {
        Console.Error.Write(label);
        if (Console.IsInputRedirected) return Console.ReadLine() ?? string.Empty;
        var builder = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (builder.Length > 0) builder.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) builder.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return builder.ToString();
    }

    private sealed class AccountCliException(HttpStatusCode status, string message) : Exception(message)
    {
        public HttpStatusCode Status { get; } = status;
    }
}
