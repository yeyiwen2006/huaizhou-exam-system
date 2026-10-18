using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Exam.Contracts.Archives;
using Exam.Contracts.Common;
using Exam.Infrastructure.Archives;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

namespace Exam.Server.Api;

/// <summary>Single-exam copies travel through the operator connection, without using server-local directories.</summary>
public static class ExamArchiveEndpoints
{
    public static RouteGroupBuilder MapExamArchiveApi(this RouteGroupBuilder api)
    {
        api.MapGet("/exams/{examId}/archive", ExportAsync)
            .WithMetadata(new ReadOnlyObservation());
        api.MapPost("/exam-archives/preview", PreviewAsync).DisableAntiforgery()
            .WithMetadata(new ReadOnlyPostQuery());
        api.MapPost("/exam-archives/import", ImportAsync).DisableAntiforgery();
        return api;
    }

    private static async Task<IResult> ExportAsync(string examId, HttpContext context,
        IConfiguration configuration, CancellationToken ct)
    {
        _ = ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context);
        MemoryStream? output = null;
        try
        {
            var service = context.RequestServices.GetRequiredService<PostgresExamArchiveService>();
            var result = await ApiEndpointResults.ExecuteRuleAsync(context, configuration, context.TraceIdentifier,
                async () => await service.ExportAsync(examId, ct));
            // The regular envelope supplies consistent 404/409 responses before any archive bytes are sent.
            if (result is not IValueHttpResult { Value: ApiEnvelope<ExamArchiveDocument> { Result: { } document, Error: null } })
                return result;
            output = new MemoryStream();
            await document.WriteAsync(output, ct);
            if (output.Length > ExamArchiveFormat.MaximumArchiveBytes)
                throw new InvalidDataException("考试归档超过当前格式支持的大小。");
            output.Position = 0;
            var identity = configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>()
                ?? throw new InvalidOperationException("缺少服务器身份配置。");
            context.Response.Headers["X-Exam-Server-Id"] = identity.ServerId;
            context.Response.Headers["X-Exam-Node-Id"] = identity.NodeId;
            context.Response.Headers["X-Exam-Server-Epoch"] = identity.ServerEpoch.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Stream(output, "application/zip", ExamArchiveFormat.FileName, enableRangeProcessing: false);
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or CryptographicException)
        {
            output?.Dispose();
            return ApiEndpointResults.Error(context, configuration, context.TraceIdentifier,
                "EXAM_ARCHIVE_UNAVAILABLE", "考试归档未能生成，请核对本场题包、源材料和服务器解密配置；当前归档上限为 512 MiB。",
                StatusCodes.Status409Conflict);
        }
        catch { output?.Dispose(); throw; }
    }

    private static async Task<IResult> PreviewAsync(HttpContext context, IConfiguration configuration, CancellationToken ct)
    {
        _ = ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context);
        try
        {
            var (document, _) = await ReadUploadAsync(context, import: false, ct);
            var service = context.RequestServices.GetRequiredService<PostgresExamArchiveService>();
            context.Response.Headers.CacheControl = "no-store";
            return await ApiEndpointResults.ExecuteRuleAsync(context, configuration, context.TraceIdentifier,
                async () => await service.PreviewAsync(document, ct));
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or BadHttpRequestException)
        { return InvalidArchive(context, configuration); }
    }

    private static async Task<IResult> ImportAsync(HttpContext context, IConfiguration configuration, CancellationToken ct)
    {
        var actor = ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context);
        try
        {
            var (document, request) = await ReadUploadAsync(context, import: true, ct);
            var service = context.RequestServices.GetRequiredService<PostgresExamArchiveService>();
            context.Response.Headers.CacheControl = "no-store";
            return await ApiEndpointResults.ExecuteRuleAsync(context, configuration, request!.RequestId,
                async () => await service.ImportAsync(document, request, actor, ct));
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or BadHttpRequestException)
        { return InvalidArchive(context, configuration); }
        catch (CryptographicException)
        {
            return ApiEndpointResults.Error(context, configuration, context.TraceIdentifier,
                "EXAM_ARCHIVE_ENCRYPTION_UNAVAILABLE", "目标服务器无法加密考试资源，请核对题包证书配置后重试。",
                StatusCodes.Status503ServiceUnavailable);
        }
        catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.SerializationFailure or
            PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.DeadlockDetected)
        {
            return ApiEndpointResults.Error(context, configuration, context.TraceIdentifier,
                "VERSION_CONFLICT", "目标考试已被其他操作更新，本次导入未写入。请重新预览后选择导入方式。",
                StatusCodes.Status409Conflict);
        }
    }

    private static async Task<(ExamArchiveDocument Document, ExamArchiveImportRequest? Request)> ReadUploadAsync(
        HttpContext context, bool import, CancellationToken ct)
    {
        const long maximum = ExamArchiveFormat.MaximumArchiveBytes + 1024 * 1024;
        if (!context.Request.HasFormContentType || context.Request.ContentLength > maximum)
            throw new InvalidDataException("请选择完整的考试归档。");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = maximum;
        context.Features.Set<IFormFeature>(new FormFeature(context.Request, new FormOptions
        {
            BufferBody = false, ValueCountLimit = 6, ValueLengthLimit = 256,
            MultipartBodyLengthLimit = maximum, MultipartBoundaryLengthLimit = 70,
            MultipartHeadersLengthLimit = 16384, MultipartHeadersCountLimit = 16
        }));
        var form = await context.Request.ReadFormAsync(ct);
        if (form.Files.Count != 1 || form.Files[0].Name != "file" ||
            form.Files[0].Length is <= 0 or > ExamArchiveFormat.MaximumArchiveBytes)
            throw new InvalidDataException("考试归档文件无效。");
        ExamArchiveImportRequest? request = null;
        if (!import && form.Count != 0) throw new InvalidDataException("归档预览不接收导入选项。");
        if (import)
        {
            string[] allowed = ["requestId", "mode", "expectedTargetExamId", "expectedTargetVersion"];
            if (form.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal)) || form.Any(item => item.Value.Count != 1))
                throw new InvalidDataException("考试导入选项无效。");
            var requestId = form["requestId"].ToString();
            var mode = form["mode"].ToString();
            var target = form["expectedTargetExamId"].ToString();
            var versionText = form["expectedTargetVersion"].ToString();
            if (string.IsNullOrWhiteSpace(requestId) || requestId.Length > 128 ||
                mode is not ("copy" or "overwrite" or "skip") || target.Length > 128)
                throw new InvalidDataException("考试导入选项无效。");
            long? version = null;
            if (versionText.Length > 0)
            {
                if (!long.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
                    throw new InvalidDataException("目标考试版本无效。");
                version = parsed;
            }
            if (mode == "overwrite" && (string.IsNullOrWhiteSpace(target) || version is null) ||
                (target.Length == 0) != (version is null))
                throw new InvalidDataException("请选择要覆盖的目标考试。");
            request = new(requestId, mode, target.Length == 0 ? null : target, version);
        }
        await using var input = form.Files[0].OpenReadStream();
        return (await ExamArchiveDocument.ReadAsync(input, ct), request);
    }

    private static IResult InvalidArchive(HttpContext context, IConfiguration configuration) =>
        ApiEndpointResults.Error(context, configuration, context.TraceIdentifier, ErrorCodes.InvalidInput,
            "考试归档或导入选项无效。请选择本系统导出的完整归档目录，当前归档上限为 512 MiB。",
            StatusCodes.Status400BadRequest);
}
