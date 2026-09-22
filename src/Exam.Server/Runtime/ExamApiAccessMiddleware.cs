using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Application.Activation;

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
        ActiveNodeGuard activeNode)
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
            await RejectNodeAsync(context, node);
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

            await RejectAsync(context, StatusCodes.Status401Unauthorized, "设备凭据无效或与请求设备不一致。");
            return;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        var supplied = authorization.StartsWith(prefix, StringComparison.Ordinal)
            ? authorization[prefix.Length..]
            : string.Empty;
        if (!FixedTimeEquals(supplied, security.OperatorToken))
        {
            await RejectAsync(context, StatusCodes.Status401Unauthorized, "缺少有效的考务操作令牌。");
            return;
        }

        if (!await HasMatchingAuditActorAsync(context, security.OperatorId))
        {
            await RejectAsync(context, StatusCodes.Status403Forbidden, "请求中的操作者身份与已认证考务身份不一致。");
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

            foreach (var propertyName in new[] { "operator", "operatorId", "actorId" })
            {
                if (document.RootElement.TryGetProperty(propertyName, out var actor) &&
                    (actor.ValueKind != JsonValueKind.String ||
                     !string.Equals(actor.GetString(), operatorId, StringComparison.Ordinal)))
                {
                    return false;
                }
            }

            return true;
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
                if (document.RootElement.TryGetProperty("deviceId", out var bodyDevice) &&
                    bodyDevice.ValueKind == JsonValueKind.String &&
                    !string.Equals(bodyDevice.GetString(), deviceId, StringComparison.Ordinal))
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

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        var left = Encoding.UTF8.GetBytes(supplied);
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static Task RejectAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                code = statusCode == StatusCodes.Status403Forbidden ? "FORBIDDEN" : "UNAUTHORIZED",
                message
            },
            correlationId = context.TraceIdentifier
        });
    }

    private static Task RejectNodeAsync(HttpContext context, ActiveNodeCheck node)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsJsonAsync(new
        {
            error = new { code = node.ErrorCode, message = node.Reason },
            correlationId = context.TraceIdentifier
        });
    }
}
