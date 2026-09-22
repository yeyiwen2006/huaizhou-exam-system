using System.Security.Cryptography;
using System.Text.Json;
using Exam.Contracts.Common;
using Exam.Contracts.Resources;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace Exam.Server.Api;

public static class PackageUnlockEndpointExtensions
{
    public static RouteGroupBuilder MapPackageUnlockApi(this RouteGroupBuilder api)
    {
        api.MapPost("/resources/unlock", async (
            PackageUnlockRequest request,
            [FromServices] PostgresPackageUnlockService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            if (!context.Request.IsHttps)
            {
                return ApiEndpointResults.Error(context, configuration, request.RequestId,
                    ErrorCodes.Forbidden, "题包解锁材料只能通过 HTTPS 获取。", StatusCodes.Status403Forbidden);
            }

            try
            {
                var material = await service.UnlockAsync(request,
                    ExamApiAccessMiddleware.RequireAuthenticatedDeviceId(context), cancellationToken);
                return new SensitiveResult(
                    ApiEndpointResults.Success(context, configuration, request.RequestId, material), material);
            }
            catch (PackageUnlockException exception)
            {
                var status = exception.Code switch
                {
                    ErrorCodes.InvalidInput => StatusCodes.Status400BadRequest,
                    ErrorCodes.NotFound => StatusCodes.Status404NotFound,
                    ErrorCodes.NotActivated or ErrorCodes.AuthorizationRevoked => StatusCodes.Status403Forbidden,
                    ErrorCodes.ServiceRecovering or ErrorCodes.NotActiveServer => StatusCodes.Status503ServiceUnavailable,
                    _ => StatusCodes.Status409Conflict
                };
                return ApiEndpointResults.Error(context, configuration, request.RequestId,
                    exception.Code, exception.Message, status,
                    retryable: status == StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception exception) when (exception is CryptographicException or FormatException or
                                              JsonException or NpgsqlException or TimeoutException)
            {
                // Do not log envelopes, private key details, or released key bytes.
                return ApiEndpointResults.Error(context, configuration, request.RequestId,
                    ErrorCodes.ServiceRecovering, "服务器题包解锁材料暂不可用，请稍后重试。",
                    StatusCodes.Status503ServiceUnavailable, retryable: true);
            }
        }).WithTags("A/B Package unlock");
        return api;
    }

    private sealed class SensitiveResult(IResult result, PackageUnlockMaterial material) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            try
            {
                await result.ExecuteAsync(httpContext);
            }
            finally
            {
                material.Dispose();
            }
        }
    }
}
