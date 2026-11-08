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
            string? endpoint = null, serverId = null, nodeId = null, inputPath = null, requestId = null, sourceDirectory = null;
            for (var index = 0; index < arguments.Count; index++)
            {
                var name = arguments[index];
                if (name is not ("--management-url" or "--server-id" or "--node-id" or "--input" or "--request-id" or "--source-directory") ||
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
                    case "--source-directory": sourceDirectory = SetOnce(sourceDirectory, value); break;
                }
            }
            endpoint ??= Environment.GetEnvironmentVariable("EXAM_MANAGEMENT_URL");
            serverId ??= Environment.GetEnvironmentVariable("EXAM_SERVER_ID");
            nodeId ??= Environment.GetEnvironmentVariable("EXAM_NODE_ID");
            if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
                string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(nodeId))
                throw new ArgumentException("须指定服务器 HTTPS 管理地址、预期服务器和节点。");
            if (command is "login" or "logout")
            {
                if (inputPath is not null || requestId is not null || sourceDirectory is not null)
                    throw new ArgumentException("管理登录和退出不接受请求文件或请求编号。");
                return await LoginAsync(command, uri, serverId, nodeId, output, errors, token);
            }
            var reads = command is "status" or "ha-status" or "ha-local-status" or "configuration" or "logs" or "runtime-logs" or "operation" or "backup-info";
            var writes = command is "start" or "stop" or "checks" or "configure" or "backup" or "restore" or "takeover" or "emergency" or "rebuild" or "reprotect";
            if (!reads && !writes && command != "cancel" || !writes && inputPath is not null || writes && (inputPath is null || requestId is not null) ||
                (command is "operation" or "cancel") != (requestId is not null) ||
                command == "backup-info" != (sourceDirectory is not null))
                throw new ArgumentException("管理命令及输入组合无效。");
            string? input = null;
            if (inputPath is not null)
            {
                input = inputPath == "-" ? await ExamCli.ReadStandardInputAsync(token) :
                    await File.ReadAllTextAsync(Path.GetFullPath(inputPath), Encoding.UTF8, token);
                if (Encoding.UTF8.GetByteCount(input) > 65536) throw new ArgumentException("管理请求超过大小限制。");
            }
            using var client = clientFactory is null
                ? new LocalManagementClient(uri, serverId, nodeId, () =>
                    Environment.GetEnvironmentVariable("EXAM_OPERATOR_TOKEN") is { Length: > 0 } configured
                        ? configured : CliSessionStore.Load(ExamCli.ManagementSessionKind, CliSessionStore.Scope(uri, serverId, nodeId)))
                : clientFactory(uri, serverId, nodeId);
            return await new LocalManagementCliHandler(client).RunAsync(command, input, requestId,
                output, errors, token, sourceDirectory);
        }
        catch (Exception error) when (error is ArgumentException or UriFormatException or IOException or UnauthorizedAccessException)
        {
            errors.WriteLine("INVALID_INPUT: 管理命令、地址或输入文件无效。");
            return 2;
        }
    }

    /// <summary>用考务账号登录服务器管理；会话加密保存在本机，供后续 management 命令使用。</summary>
    private static async Task<int> LoginAsync(string command, Uri endpoint, string serverId, string nodeId, TextWriter output, TextWriter errors,
        CancellationToken token)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps || endpoint.Host.Length == 0 || endpoint.Port is < 1 or > 65535 || endpoint.AbsolutePath != "/" ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            errors.WriteLine("INVALID_INPUT: 服务器管理需要 HTTPS 根地址。");
            return 2;
        }
        var sessionScope = CliSessionStore.Scope(endpoint, serverId, nodeId);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(30) };
        if (command == "logout")
        {
            var saved = CliSessionStore.Load(ExamCli.ManagementSessionKind, sessionScope);
            if (saved is not null)
            {
                using var logout = new HttpRequestMessage(HttpMethod.Post, "/management/v1/logout");
                logout.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", saved);
                try { using var _ = await client.SendAsync(logout, token); } catch (HttpRequestException) { }
            }
            CliSessionStore.Delete(ExamCli.ManagementSessionKind, sessionScope);
            output.WriteLine("已退出服务管理登录。");
            return 0;
        }
        var username = await ExamCli.PromptAsync("用户名：", token);
        var password = await ExamCli.ReadPasswordAsync("口令：", token);
        try
        {
            using var response = await client.PostAsJsonAsync("/management/v1/login",
                new Exam.Contracts.Operators.LoginOperatorRequest(username, password), token);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                errors.WriteLine("登录失败：用户名或口令不正确，或账号尚未启用。");
                return 3;
            }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("管理登录响应未成功。");
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("serverId", out var returnedServer) || returnedServer.ValueKind != JsonValueKind.String || returnedServer.GetString() != serverId ||
                !document.RootElement.TryGetProperty("nodeId", out var returnedNode) || returnedNode.ValueKind != JsonValueKind.String || returnedNode.GetString() != nodeId ||
                !document.RootElement.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("token", out var session) ||
                session.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(session.GetString()) ||
                !result.TryGetProperty("username", out var returnedUsername) || returnedUsername.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(returnedUsername.GetString()))
                throw new HttpRequestException("管理登录响应身份或会话无效。");
            CliSessionStore.Save(ExamCli.ManagementSessionKind, sessionScope, session.GetString()!);
            output.WriteLine($"已登录服务管理（{serverId}）：{returnedUsername.GetString()}。");
            return 0;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException && !token.IsCancellationRequested)
        {
            // 不输出异常正文，避免带出地址或证书细节以外的信息。
            errors.WriteLine("无法连接服务器管理，或其响应无效；请确认管理程序正在运行、网络可达且证书受信任。");
            return 5;
        }
    }

    private static string SetOnce(string? previous, string value) => previous is null && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException("管理选项重复或值为空。");
}
