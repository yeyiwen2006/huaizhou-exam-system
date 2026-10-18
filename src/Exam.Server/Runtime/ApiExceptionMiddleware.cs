using Exam.Contracts.Common;
using Exam.Domain.Common;

namespace Exam.Server.Runtime;

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IConfiguration configuration)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Navigation, a client deadline or a disconnected client can cancel a read or
            // stop waiting for a committed request. Neither means the business action failed.
            // Do not try to send an error envelope over the canceled connection.
            if (!context.Response.HasStarted)
                context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
        }
        catch (DomainRuleException exception)
        {
            var identity = configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>()
                ?? new ServerIdentityOptions();
            var statusCode = exception.Code switch
            {
                ErrorCodes.Forbidden or ErrorCodes.AuthorizationRevoked => StatusCodes.Status403Forbidden,
                ErrorCodes.NotFound => StatusCodes.Status404NotFound,
                ErrorCodes.PersistenceFailed or ErrorCodes.ServiceRecovering => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status409Conflict
            };
            context.Response.StatusCode = statusCode;
            await context.Response.WriteAsJsonAsync(new ApiEnvelope<object>(
                identity.ServerId,
                identity.NodeId,
                identity.ServerEpoch,
                context.TraceIdentifier,
                null,
                new ApiError(exception.Code, exception.Message, Retryable: exception.Retryable)),
                JsonDefaults.Create(),
                context.RequestAborted);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled request failure. CorrelationId={CorrelationId}", context.TraceIdentifier);
            var identity = configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>()
                ?? new ServerIdentityOptions();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new ApiEnvelope<object>(
                identity.ServerId,
                identity.NodeId,
                identity.ServerEpoch,
                context.TraceIdentifier,
                null,
                new ApiError(ErrorCodes.PersistenceFailed, "服务器处理请求失败。", Retryable: true)),
                JsonDefaults.Create(),
                context.RequestAborted);
        }
    }
}
