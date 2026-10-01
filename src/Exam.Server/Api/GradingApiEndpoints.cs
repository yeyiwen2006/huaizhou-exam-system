using Exam.Application.Scoring;
using Microsoft.AspNetCore.Mvc;

namespace Exam.Server.Api;

/// <summary>
/// D05 计分状态读取：当前已发布规则版本、完整答卷范围与计分完成情况。
/// 与报表门配合使用——报表门只说「计分尚未完成」，本接口说明还缺哪些答卷、原因是什么。
/// </summary>
public static class GradingApiEndpoints
{
    public static RouteGroupBuilder MapGradingApi(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/grading").WithTags("D05 grading status");

        group.MapGet("/{examId}/status", async (string examId,
            [FromServices] GradingStatusService grading,
            HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
        {
            // 状态随规则发布与补收答卷变化，且是范围快照：禁止缓存。
            context.Response.Headers.CacheControl = "no-store";
            return ApiEndpointResults.Success(context, configuration, context.TraceIdentifier,
                await grading.ReadAsync(examId, cancellationToken));
        });

        return group;
    }
}
