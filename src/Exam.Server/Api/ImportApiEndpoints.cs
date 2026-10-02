using Exam.Contracts.Importing;
using Exam.Infrastructure.Packaging;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Mvc;

namespace Exam.Server.Api;

/// <summary>
/// C05 导入流水线的服务端入口：**预检**与**发布**。考务端界面与 CLI 共用同一入口，
/// 两者看到的阶段与问题一致，不各写一套。
///
/// 操作者取自已认证身份，请求体不携带 operator，因此不存在「请求自称的身份与认证身份不一致」的情况。
/// 预检不组包、不入库、不发布；发布在预检同一准备结果上继续，四道发布门由流水线在服务端判定。
/// </summary>
public static class ImportApiEndpoints
{
    public static RouteGroupBuilder MapImportApi(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/imports").WithTags("C05 import pipeline");

        group.MapPost("/preview", async ([FromBody] ImportPreviewRequest body,
            [FromServices] ImportPipeline pipeline,
            HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            var request = new ImportPipelineRequest(
                body.PackageRoot, body.ExamId, body.ExpectedPaperVersion,
                body.CompilationOutputDirectory, body.CompilationOutputDirectory,
                [], ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context), body.Reason);
            // 预检只编译与抽取，不组包，因此不需要加密收件人。
            return ApiEndpointResults.Success(context, configuration, context.TraceIdentifier,
                await pipeline.InspectAsync(request, cancellationToken));
        });

        group.MapPost("/publish", async ([FromBody] ImportPublishRequest body,
            [FromServices] ImportPipeline pipeline,
            HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            var request = new ImportPipelineRequest(
                body.PackageRoot, body.ExamId, body.ExpectedPaperVersion,
                body.CompilationOutputDirectory, body.PackageOutputDirectory,
                body.Recipients, ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context),
                body.Reason, body.RequestId ?? string.Empty);
            return ApiEndpointResults.Success(context, configuration, context.TraceIdentifier,
                await pipeline.RunAsync(request, cancellationToken));
        });

        return group;
    }
}

/// <summary>
/// 预检输入。材料以**服务器本机可读的目录**给出；编译产物写到 CompilationOutputDirectory，
/// 不写回材料目录。发布需要的包输出目录与加密收件人在发布请求里给出。
/// </summary>
public sealed record ImportPreviewRequest(
    string PackageRoot,
    string ExamId,
    long ExpectedPaperVersion,
    string CompilationOutputDirectory,
    string Reason);

/// <summary>发布输入。Recipients 为学生包的加密接收节点（节点标识与其公钥证书 DER）。</summary>
public sealed record ImportPublishRequest(
    string PackageRoot,
    string ExamId,
    long ExpectedPaperVersion,
    string CompilationOutputDirectory,
    string PackageOutputDirectory,
    IReadOnlyList<PackageRecipient> Recipients,
    string Reason,
    string? RequestId = null);
