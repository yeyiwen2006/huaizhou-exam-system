using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using Exam.Infrastructure.ServiceManagement;

public static class ManagementCli
{
    public static async Task<int> RunAsync(string command, IReadOnlyList<string> arguments, CancellationToken token,
        Func<Uri, string, string, LocalManagementClient>? clientFactory = null,
        TextWriter? output = null, TextWriter? errors = null)
    {
        output ??= Console.Out;
        errors ??= Console.Error;
        try
        {
            string? endpoint = null, serverId = null, nodeId = null, inputPath = null, requestId = null;
            for (var index = 0; index < arguments.Count; index++)
            {
                var name = arguments[index];
                if (name is not ("--management-url" or "--server-id" or "--node-id" or "--input" or "--request-id") ||
                    index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("管理选项无效或缺少值。");
                var value = arguments[++index];
                switch (name)
                {
                    case "--management-url": endpoint = SetOnce(endpoint, value); break;
                    case "--server-id": serverId = SetOnce(serverId, value); break;
                    case "--node-id": nodeId = SetOnce(nodeId, value); break;
                    case "--input": inputPath = SetOnce(inputPath, value); break;
                    case "--request-id": requestId = SetOnce(requestId, value); break;
                }
            }
            endpoint ??= Environment.GetEnvironmentVariable("EXAM_MANAGEMENT_URL");
            serverId ??= Environment.GetEnvironmentVariable("EXAM_SERVER_ID");
            nodeId ??= Environment.GetEnvironmentVariable("EXAM_NODE_ID");
            if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(nodeId))
                throw new ArgumentException("须指定本机 HTTPS 管理地址、预期服务器和节点。");
            if (command is "login" or "logout")
                return await LoginAsync(command, uri, serverId, output, errors, token);
            var reads = command is "status" or "ha-status" or "configuration" or "logs" or "runtime-logs" or "operation";
            var writes = command is "start" or "stop" or "checks" or "configure" or "backup" or "restore" or "takeover" or "emergency" or "rebuild" or "reprotect";
            if (!reads && !writes || reads && inputPath is not null || writes && (inputPath is null || requestId is not null) ||
                command == "operation" != (requestId is not null))
                throw new ArgumentException("管理命令及输入组合无效。");
            string? input = null;
            if (inputPath is not null)
            {
                input = inputPath == "-" ? await Console.In.ReadToEndAsync(token) :
                    await File.ReadAllTextAsync(Path.GetFullPath(inputPath), Encoding.UTF8, token);
                if (Encoding.UTF8.GetByteCount(input) > 65536) throw new ArgumentException("管理请求超过大小限制。");
            }
            using var client = clientFactory is null
                ? new LocalManagementClient(uri, serverId, nodeId, () =>
                    Environment.GetEnvironmentVariable("EXAM_OPERATOR_TOKEN") is { Length: > 0 } configured
                        ? configured : CliSessionStore.Load(ExamCli.ManagementSessionKind))
                : clientFactory(uri, serverId, nodeId);
            return await new LocalManagementCliHandler(client).RunAsync(command, input, requestId,
                output, errors, token);
        }
        catch (Exception error) when (error is ArgumentException or UriFormatException or IOException or UnauthorizedAccessException)
        {
            errors.WriteLine("INVALID_INPUT: 管理命令、地址或输入文件无效。");
            return 2;
        }
    }

    /// <summary>用考务账号登录本机服务管理；会话加密保存在本机，供后续 management 命令使用。</summary>
    private static async Task<int> LoginAsync(string command, Uri endpoint, string serverId, TextWriter output, TextWriter errors,
        CancellationToken token)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps || !(endpoint.IsLoopback ||
            System.Net.IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var address) && System.Net.IPAddress.IsLoopback(address)))
        {
            errors.WriteLine("INVALID_INPUT: 服务管理只能通过本机 HTTPS 地址登录。");
            return 2;
        }
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(30) };
        if (command == "logout")
        {
            var saved = CliSessionStore.Load(ExamCli.ManagementSessionKind);
            if (saved is not null)
            {
                using var logout = new HttpRequestMessage(HttpMethod.Post, "/management/v1/logout");
                logout.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", saved);
                try { using var _ = await client.SendAsync(logout, token); } catch (HttpRequestException) { }
            }
            CliSessionStore.Delete(ExamCli.ManagementSessionKind);
            output.WriteLine("已退出服务管理登录。");
            return 0;
        }
        errors.Write("用户名：");
        var username = Console.ReadLine()?.Trim() ?? string.Empty;
        var password = ExamCli.ReadPassword("口令：");
        try
        {
            using var response = await client.PostAsJsonAsync("/management/v1/login",
                new Exam.Contracts.Operators.LoginOperatorRequest(username, password), token);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            if (!response.IsSuccessStatusCode || !document.RootElement.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("token", out var session) ||
                session.ValueKind != JsonValueKind.String)
            {
                errors.WriteLine("登录失败：用户名或口令不正确，或账号尚未启用。");
                return 3;
            }
            CliSessionStore.Save(ExamCli.ManagementSessionKind, session.GetString()!);
            output.WriteLine($"已登录服务管理（{serverId}）：{result.GetProperty("username").GetString()}。");
            return 0;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException && !token.IsCancellationRequested)
        {
            // 不输出异常正文，避免带出地址或证书细节以外的信息。
            errors.WriteLine("无法连接本机服务管理，或其响应无效；请确认服务管理程序正在运行且证书受信任。");
            return 5;
        }
    }

    private static string SetOnce(string? previous, string value) => previous is null && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException("管理选项重复或值为空。");
}
