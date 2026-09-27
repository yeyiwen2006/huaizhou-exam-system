using Exam.Application.Security;
using System.Text.Json;
using Exam.Application.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;

namespace Exam.Server.Runtime;

public sealed record ExamApiSecurityOptions(string OperatorToken, string OperatorId)
{
    public const string SectionName = "ExamSecurity";
}

/// <summary>
/// Separates operator endpoints from device endpoints at the server boundary. Device requests use
/// the same protected device credential as activation and must also pass the application-layer
/// participation/session checks. Secrets are accepted only in headers and are never included in
/// route values or log messages.
/// </summary>
public sealed class ExamApiAccessMiddleware(RequestDelegate next)
{
    private const string DeviceIdHeader = "X-Exam-Device-Id";
    private const string DeviceCredentialHeader = "X-Exam-Device-Credential";
    private const string AuthenticatedDeviceItem = "Exam.AuthenticatedDeviceId";
    private const string AuthenticatedOperatorItem = "Exam.AuthenticatedOperatorId";

    public async Task InvokeAsync(
        HttpContext context,
        ExamApiSecurityOptions security,
        IDeviceCredentialValidator credentials,
        ActiveNodeGuard activeNode,
        ServerNodeEnvelope server)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1", out var remainder))
        {
            await next(context);
            return;
        }

        var route = remainder.Value ?? string.Empty;
        var node = await activeNode.CheckAsync(context.RequestAborted);
        if (!node.Allowed)
        {
            await RejectNodeAsync(context, node, server);
            return;
        }

        if (IsPublic(context.Request.Method, route))
        {
            await next(context);
            return;
        }

        if (IsDeviceRoute(context.Request.Method, route))
        {
            if (await AuthenticateDeviceAsync(context, credentials) is { } authenticatedDeviceId)
            {
                context.Items[AuthenticatedDeviceItem] = authenticatedDeviceId;
                await next(context);
                return;
            }

            await RejectAsync(context, server, StatusCodes.Status401Unauthorized,
                ErrorCodes.AuthorizationRevoked, "设备凭据无效或与请求设备不一致。");
            return;
        }

        if (!OperatorIdentityValidator.Authenticate(context.Request.Headers.Authorization.ToString(),
                security.OperatorToken, security.OperatorId))
        {
            await RejectAsync(context, server, StatusCodes.Status401Unauthorized,
                "UNAUTHORIZED", "缺少有效的考务操作令牌。");
            return;
        }

        if (!await HasMatchingAuditActorAsync(context, security.OperatorId))
        {
            await RejectAsync(context, server, StatusCodes.Status403Forbidden,
                ErrorCodes.Forbidden, "请求中的操作者身份与已认证考务身份不一致。");
            return;
        }

        context.Items[AuthenticatedOperatorItem] = security.OperatorId;
        await next(context);
    }

    private static bool IsPublic(string method, string route) =>
        (HttpMethods.IsPost(method) && route is
            "/activation/requests" or
            "/activation/status" or
            "/activation/cancel-own" or
            "/activation/reactivate" or
            "/participation/login") ||
        (HttpMethods.IsGet(method) && route == "/commands/time");

    private static bool IsDeviceRoute(string method, string route)
    {
        if (HttpMethods.IsGet(method) && (route is "/student/exams" or "/student/instructions"))
        {
            return true;
        }

        if (route.StartsWith("/commands/due", StringComparison.Ordinal) ||
            route == "/commands/acknowledgements" ||
            route.StartsWith("/submissions/", StringComparison.Ordinal) ||
            route.StartsWith("/resources/", StringComparison.Ordinal) ||
            (route.StartsWith("/versioning/exams/", StringComparison.Ordinal) &&
             (route.EndsWith("/paper-transfer", StringComparison.Ordinal) ||
              route.EndsWith("/paper-review", StringComparison.Ordinal))))
        {
            return !route.StartsWith("/submissions/collection/", StringComparison.Ordinal);
        }

        return HttpMethods.IsPost(method) && route is
            "/participation/prepare-entry" or
            "/participation/confirm" or
            "/participation/withdraw" or
            "/participation/authorize-write";
    }

    public static string RequireAuthenticatedDeviceId(HttpContext context) =>
        context.Items.TryGetValue(AuthenticatedDeviceItem, out var value) && value is string deviceId
            ? deviceId
            : throw new InvalidOperationException("请求未经过设备身份验证中间件。");

    public static string RequireAuthenticatedOperatorId(HttpContext context) =>
        context.Items.TryGetValue(AuthenticatedOperatorItem, out var value) && value is string operatorId
            ? operatorId
            : throw new InvalidOperationException("请求未经过考务身份验证中间件。");

    private static async Task<bool> HasMatchingAuditActorAsync(HttpContext context, string operatorId)
    {
        if (!context.Request.HasJsonContentType())
        {
            return true;
        }

        context.Request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(
                context.Request.Body,
                cancellationToken: context.RequestAborted);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            return OperatorIdentityValidator.HasMatchingActor(document.RootElement, operatorId);
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            context.Request.Body.Position = 0;
        }
    }

    private static async Task<string?> AuthenticateDeviceAsync(
        HttpContext context,
        IDeviceCredentialValidator credentials)
    {
        var deviceId = context.Request.Headers[DeviceIdHeader].ToString();
        var credential = context.Request.Headers[DeviceCredentialHeader].ToString();
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(credential) ||
            !await credentials.ValidateActiveDeviceAsync(deviceId, credential, context.RequestAborted))
        {
            return null;
        }

        var queryDeviceId = context.Request.Query["deviceId"].ToString();
        if (!string.IsNullOrEmpty(queryDeviceId) &&
            !string.Equals(queryDeviceId, deviceId, StringComparison.Ordinal))
        {
            return null;
        }

        if (context.Request.HasJsonContentType())
        {
            context.Request.EnableBuffering();
            try
            {
                using var document = await JsonDocument.ParseAsync(
                    context.Request.Body,
                    cancellationToken: context.RequestAborted);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    (document.RootElement.TryGetProperty("deviceId", out var bodyDevice) &&
                     (bodyDevice.ValueKind != JsonValueKind.String ||
                      !string.Equals(bodyDevice.GetString(), deviceId, StringComparison.Ordinal))))
                {
                    return null;
                }
            }
            catch (JsonException)
            {
                return null;
            }
            finally
            {
                context.Request.Body.Position = 0;
            }
        }

        return deviceId;
    }

    private static Task RejectAsync(
        HttpContext context,
        ServerNodeEnvelope server,
        int statusCode,
        string code,
        string message,
        bool retryable = false)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsJsonAsync(new ApiEnvelope<object>(
            server.ServerId,
            server.NodeId,
            server.ServerEpoch,
            context.TraceIdentifier,
            null,
            new ApiError(code, message, Retryable: retryable)));
    }

    private static Task RejectNodeAsync(HttpContext context, ActiveNodeCheck node, ServerNodeEnvelope server) =>
        RejectAsync(context, server, StatusCodes.Status503ServiceUnavailable,
            node.ErrorCode, node.Reason ?? "当前节点暂时不能处理请求。", retryable: true);
}
