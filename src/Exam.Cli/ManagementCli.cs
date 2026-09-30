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
            var reads = command is "status" or "configuration" or "logs" or "runtime-logs" or "operation";
            var writes = command is "start" or "stop" or "checks" or "configure" or "backup" or "restore";
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
                ? new LocalManagementClient(uri, serverId, nodeId, () => Environment.GetEnvironmentVariable("EXAM_OPERATOR_TOKEN"))
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

    private static string SetOnce(string? previous, string value) => previous is null && !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException("管理选项重复或值为空。");
}
