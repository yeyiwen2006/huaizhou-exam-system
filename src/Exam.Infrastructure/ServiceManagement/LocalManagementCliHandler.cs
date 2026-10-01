using System.Net;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>F01 handler supplied to E's CLI registration; shares the same transport with F04.</summary>
public sealed class LocalManagementCliHandler(LocalManagementClient client)
{
    public async Task<int> RunAsync(string command, string? inputJson, string? requestId,
        TextWriter output, TextWriter errors, CancellationToken token = default)
    {
        try
        {
            return command switch
            {
                "status" when inputJson is null && requestId is null => await WriteAsync(await client.GetStatusAsync(token), output, errors),
                "configuration" when inputJson is null && requestId is null => await WriteAsync(await client.GetConfigurationAsync(token), output, errors),
                "logs" when inputJson is null && requestId is null => await WriteAsync(await client.GetLogsAsync(50, token), output, errors),
                "runtime-logs" when inputJson is null && requestId is null => await WriteAsync(await client.GetRuntimeLogsAsync(50, token), output, errors),
                "operation" when inputJson is null && requestId is not null => await WriteAsync(await client.GetOperationAsync(requestId, token), output, errors),
                "start" or "stop" or "checks" or "configure" or "backup" or "restore" when inputJson is not null && requestId is null =>
                    await WriteAsync(await client.ExecuteAsync(Kind(command), inputJson, token), output, errors),
                _ => throw new ArgumentException("管理命令或输入无效。")
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { await errors.WriteLineAsync("操作已取消；已受理的变更须用原 requestId 查询，不自动重试。"); return 130; }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        { await errors.WriteLineAsync("INVALID_INPUT: 管理命令或 JSON 输入无效。"); return 2; }
        catch (UnauthorizedAccessException)
        { await errors.WriteLineAsync("UNAUTHORIZED: 缺少可用的管理凭据。"); return 3; }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        { await errors.WriteLineAsync("NETWORK_UNAVAILABLE: 无法核实管理响应；变更须按原 requestId 查询，不能重新生成请求。"); return 5; }
        catch (Exception)
        { await errors.WriteLineAsync("CLI_ERROR: 管理处理器失败，请检查受保护的本机配置。"); return 6; }
    }

    private static ManagementOperationKind Kind(string command) => command switch
    {
        "start" => ManagementOperationKind.Start, "stop" => ManagementOperationKind.Stop,
        "checks" => ManagementOperationKind.Checks, "configure" => ManagementOperationKind.Configure,
        "backup" => ManagementOperationKind.Backup, "restore" => ManagementOperationKind.Restore,
        _ => throw new ArgumentException("管理操作无效。")
    };

    private static async Task<int> WriteAsync<T>(ManagementClientResponse<T> response, TextWriter output, TextWriter errors)
    {
        var envelope = response.Envelope;
        var error = envelope.Error ?? (envelope.Result as ManagementOperation)?.Error;
        var code = error is not null ? error.Code switch
        {
            ErrorCodes.InvalidInput => 2, "UNAUTHORIZED" or ErrorCodes.Forbidden => 3,
            ErrorCodes.NotFound or ErrorCodes.VersionConflict => 4, _ => 6
        } : response.StatusCode switch
        {
            HttpStatusCode.BadRequest => 2, HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => 3,
            HttpStatusCode.NotFound or HttpStatusCode.Conflict => 4,
            _ when (int)response.StatusCode >= 400 => 6, _ => 0
        };
        if (envelope.Result is ManagementOperation operation && code == 0)
        {
            if (operation.Phase is ManagementOperationPhase.Accepted or ManagementOperationPhase.Staged) code = 4;
            else if (operation.Phase is ManagementOperationPhase.Failed or ManagementOperationPhase.NeedsVerification ||
                operation.Checks?.Any(check => check.BlocksWrites && !check.Passed) == true) code = 6;
        }
        await (code == 0 ? output : errors).WriteLineAsync(JsonSerializer.Serialize(envelope, JsonDefaults.Create()));
        return code;
    }
}
