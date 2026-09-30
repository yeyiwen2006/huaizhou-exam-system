using System.Text.Json;
using Exam.Application.Security;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;

namespace Exam.Management;

public static class ManagementApi
{
    public static void MapManagementApi(this WebApplication app)
    {
        var json = JsonDefaults.Create();
        app.Use(async (context, next) =>
        {
            var runtime = context.RequestServices.GetRequiredService<ManagementRuntime>();
            try
            {
                var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
                var actor = configuration["ExamSecurity:OperatorId"];
                if (!context.Request.IsHttps || !OperatorIdentityValidator.Authenticate(
                    context.Request.Headers.Authorization.ToString(), configuration["ExamSecurity:OperatorToken"], actor))
                {
                    await WriteAsync(context, runtime, null, new ApiError("UNAUTHORIZED", "缺少有效的本机考务操作令牌或 HTTPS 连接。"), 401);
                    return;
                }
                context.Items["Management.Actor"] = actor;
                if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method))
                {
                    runtime.RequireTarget(context.Request.Headers[ManagementHeaders.ServerId].ToString(),
                        context.Request.Headers[ManagementHeaders.NodeId].ToString());
                    if (!context.Request.HasJsonContentType()) throw new ArgumentException("管理写请求必须是 JSON。");
                    await BufferRequestAsync(context);
                    using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                    if (!OperatorIdentityValidator.HasMatchingActor(document.RootElement, actor!))
                    {
                        await WriteAsync(context, runtime, null, new ApiError(ErrorCodes.Forbidden, "请求操作者不一致或字段重复。"), 403);
                        return;
                    }
                    context.Request.Body.Position = 0;
                }
                await next(context);
            }
            catch (DomainRuleException error)
            { await WriteAsync(context, runtime, null, new ApiError(error.Code, error.Message, Retryable: error.Retryable), Status(error.Code)); }
            catch (Exception error) when (error is JsonException or ArgumentException or BadHttpRequestException)
            { await WriteAsync(context, runtime, null, new ApiError(ErrorCodes.InvalidInput, "管理请求格式或字段无效。"), 400); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception)
            { await WriteAsync(context, runtime, null, new ApiError(ErrorCodes.PersistenceFailed, "本机管理依赖不可用，请核对受保护配置与操作记录。", Retryable: true), 503); }
        });

        app.MapGet("/management/v1/service/status", async (HttpContext context, ManagementRuntime runtime) =>
            await WriteAsync(context, runtime, await runtime.GetStatusAsync(context.RequestAborted)));
        app.MapGet("/management/v1/service/configuration", async (HttpContext context, ManagementRuntime runtime) =>
            await WriteAsync(context, runtime, await runtime.GetConfigurationAsync(context.RequestAborted)));
        app.MapGet("/management/v1/operations/{requestId}", async (string requestId, HttpContext context, ManagementRuntime runtime) =>
            await WriteAsync(context, runtime, await runtime.FindOperationAsync(requestId, Actor(context), context.RequestAborted)));
        foreach (var (route, kind) in new[] { ("start", ManagementOperationKind.Start), ("stop", ManagementOperationKind.Stop), ("checks", ManagementOperationKind.Checks) })
            app.MapPost($"/management/v1/service/{route}", async (HttpContext context, ManagementRuntime runtime) =>
                await WriteOperationAsync(context, runtime, await runtime.ExecuteServiceAsync(
                    await ReadAsync<ManagementServiceRequest>(context, json), Actor(context), kind, context.RequestAborted)));
        app.MapPut("/management/v1/service/configuration", async (HttpContext context, ManagementRuntime runtime) =>
            await WriteOperationAsync(context, runtime, await runtime.ConfigureAsync(
                await ReadAsync<ManagementConfigurationRequest>(context, json), Actor(context), context.RequestAborted)));
        app.MapPost("/management/v1/service/backups", async (HttpContext context, ManagementRuntime runtime) =>
            await WriteOperationAsync(context, runtime, await runtime.BackupAsync(
                await ReadAsync<BackupRequest>(context, json), Actor(context), context.RequestAborted)));
        app.MapPost("/management/v1/service/restores", async (HttpContext context, ManagementRuntime runtime) =>
            await WriteOperationAsync(context, runtime, await runtime.RestoreAsync(
                await ReadAsync<RestoreRequest>(context, json), Actor(context), context.RequestAborted)));
    }

    private static async Task<T> ReadAsync<T>(HttpContext context, JsonSerializerOptions json) =>
        await JsonSerializer.DeserializeAsync<T>(context.Request.Body, json, context.RequestAborted)
        ?? throw new ArgumentException("管理请求不能为空。");
    private static async Task BufferRequestAsync(HttpContext context)
    {
        const int maximumBytes = 65536;
        if (context.Request.ContentLength > maximumBytes)
            throw new ArgumentException("管理请求超过大小限制。");
        // Check streamed bodies too; a buffering-limit IOException is not a dependency failure.
        context.Request.EnableBuffering(bufferThreshold: maximumBytes + 1, bufferLimit: maximumBytes + 1);
        var buffer = new byte[4096];
        var total = 0;
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer.AsMemory(0,
            Math.Min(buffer.Length, maximumBytes + 1 - total)), context.RequestAborted)) > 0)
        {
            total += read;
            if (total > maximumBytes) throw new ArgumentException("管理请求超过大小限制。");
        }
        context.Request.Body.Position = 0;
    }
    private static string Actor(HttpContext context) => (string)context.Items["Management.Actor"]!;
    private static Task WriteOperationAsync(HttpContext context, ManagementRuntime runtime, ManagementOperation operation) =>
        WriteAsync(context, runtime, operation, operation.Error, operation.Error is null ? 200 : Status(operation.Error.Code));

    private static int Status(string code) => code switch
    {
        ErrorCodes.InvalidInput => 400,
        ErrorCodes.Forbidden => 403,
        ErrorCodes.NotFound => 404,
        ErrorCodes.VersionConflict => 409,
        _ => 503
    };

    private static Task WriteAsync(HttpContext context, ManagementRuntime runtime, object? result, ApiError? error = null, int status = 200)
    {
        context.Response.StatusCode = status;
        // This response makes no authoritative database observation. Do not use configured/historical epochs.
        return context.Response.WriteAsJsonAsync(new ManagementEnvelope<object>(runtime.ServerId, runtime.NodeId,
            null, false, null, context.TraceIdentifier, result, error), JsonDefaults.Create(), context.RequestAborted);
    }
}
