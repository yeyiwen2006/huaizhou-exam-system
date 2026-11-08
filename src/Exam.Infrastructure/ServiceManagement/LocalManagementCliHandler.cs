using System.Net;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Contracts.HighAvailability;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>local management handler supplied to E's CLI registration; shares the same transport with service management.</summary>
public sealed class LocalManagementCliHandler(LocalManagementClient client)
{
    public async Task<int> RunAsync(string command, string? inputJson, string? requestId,
        TextWriter output, TextWriter errors, CancellationToken token = default, string? sourceDirectory = null)
    {
        try
        {
            if (command == "backup-info" != (sourceDirectory is not null))
                throw new ArgumentException("备份资料查询须指定来源目录。");
            return command switch
            {
                "status" when inputJson is null && requestId is null => await WriteAsync(await client.GetStatusAsync(token), output, errors),
                "ha-status" when inputJson is null && requestId is null => await WriteAsync(await client.GetHighAvailabilityAsync(token), output, errors),
                "ha-local-status" when inputJson is null && requestId is null => await WriteAsync(await client.GetLocalHighAvailabilityAsync(token), output, errors),
                "configuration" when inputJson is null && requestId is null => await WriteAsync(await client.GetConfigurationAsync(token), output, errors),
                "backup-info" when inputJson is null && requestId is null => await WriteAsync(await client.GetBackupInfoAsync(sourceDirectory!, token), output, errors),
                "logs" when inputJson is null && requestId is null => await WriteAsync(await client.GetLogsAsync(50, token), output, errors),
                "runtime-logs" when inputJson is null && requestId is null => await WriteAsync(await client.GetRuntimeLogsAsync(50, token), output, errors),
                "operation" when inputJson is null && requestId is not null => await WriteAsync(await client.GetOperationAsync(requestId, token), output, errors),
                "cancel" when inputJson is null && requestId is not null => await WriteAsync(await client.CancelOperationAsync(requestId, token), output, errors),
                "start" or "stop" or "checks" or "configure" or "backup" or "restore" or "takeover" or "emergency" or "rebuild" or "reprotect" when inputJson is not null && requestId is null =>
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
        "takeover" => ManagementOperationKind.Takeover, "emergency" => ManagementOperationKind.Emergency,
        "rebuild" => ManagementOperationKind.Rebuild, "reprotect" => ManagementOperationKind.Reprotect,
        _ => throw new ArgumentException("管理操作无效。")
    };

    private static async Task<int> WriteAsync<T>(ManagementClientResponse<T> response, TextWriter output, TextWriter errors)
    {
        var envelope = response.Envelope;
        var error = envelope.Error ?? (envelope.Result as ManagementOperation)?.Error;
        var code = error is not null ? error.Code switch
        {
            ErrorCodes.InvalidInput => 2, "UNAUTHORIZED" or ErrorCodes.Forbidden => 3,
            ErrorCodes.NotFound or ErrorCodes.VersionConflict or ErrorCodes.StaleServerEpoch => 4, _ => 6
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
        if (envelope.Result is HighAvailabilityStatus availability && code == 0 &&
            (availability.State is null || availability.VerifiedAt is not { } verified ||
             DateTimeOffset.UtcNow - verified >= HighAvailabilityStatus.ObservationLifetime || verified > DateTimeOffset.UtcNow.AddSeconds(5) ||
             availability.SynchronousProtectionVerified && availability.State.Nodes.Values.Any(n =>
                 DateTimeOffset.UtcNow - n.ObservedAt >= HighAvailabilityStatus.ObservationLifetime || n.ObservedAt > DateTimeOffset.UtcNow.AddSeconds(5)) ||
             availability.Checks.Any(c => c.BlocksWrites && !c.Passed))) code = 6;
        // A failed readiness check or unfinished request is still a verified diagnostic result.
        // Keep it on stdout so the public stderr boundary cannot discard the information needed to repair it.
        // Protected journal inputs and arbitrary error prose are never part of the CLI projection.
        if (envelope.Result is ManagementOperation observed)
            envelope = envelope with { Result = (T)(object)(observed with
            {
                RequestJson = null, RecoveryWorkingDirectory = null, FailoverPlan = null,
                Error = PublicError(observed.Error)
            }) };
        envelope = envelope with { Error = PublicError(envelope.Error) };
        if (code == 0 || envelope.Result is not null)
            await output.WriteLineAsync(JsonSerializer.Serialize(envelope, JsonDefaults.Create()));
        if (code != 0)
            await errors.WriteLineAsync(JsonSerializer.Serialize(new
            {
                error = PublicError(error) ?? new ApiError(code == 4 ? ErrorCodes.VersionConflict : "CLI_ERROR",
                    "管理检查未通过或原请求尚未完成，请核对输出中的状态和检查项。")
            }, JsonDefaults.Create()));
        return code;
    }

    private static ApiError? PublicError(ApiError? error) => error is null ? null :
        new(error.Code, "管理操作未完成，请核对检查结果和原请求状态。", Retryable: error.Retryable);
}
