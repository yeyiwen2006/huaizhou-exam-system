using Exam.Contracts.Exams;
using Exam.Infrastructure.Postgres.Exams;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Mvc;

namespace Exam.Server.Api;

/// <summary>考务端考试目录：列出、新建、读取、修改设置与学生端显示开关。仅考务身份可用。</summary>
public static class ExamCatalogEndpoints
{
    public static RouteGroupBuilder MapExamCatalogApi(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/exams").WithTags("Exam Catalog");

        group.MapGet("", async ([FromServices] PostgresExamCatalog catalog, HttpContext context,
                IConfiguration configuration, CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(context, configuration, context.TraceIdentifier,
                () => catalog.ListAsync(cancellationToken, ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context))));

        group.MapPost("", async (CreateExamRequest request, [FromServices] PostgresExamCatalog catalog,
                HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(context, configuration, request.RequestId,
                () => catalog.CreateAsync(request, ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context), cancellationToken),
                StatusCodes.Status201Created));

        group.MapGet("/{examId}", async (string examId, [FromServices] PostgresExamCatalog catalog,
                HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(context, configuration, context.TraceIdentifier,
                () => catalog.GetAsync(examId, cancellationToken, ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context))));

        group.MapPut("/{examId}/settings", async (string examId, UpdateExamSettingsRequest request,
                [FromServices] PostgresExamCatalog catalog, HttpContext context, IConfiguration configuration,
                CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(context, configuration, request.RequestId,
                () => catalog.UpdateSettingsAsync(examId, request,
                    ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context), cancellationToken)));

        group.MapPut("/{examId}/visibility", async (string examId, SetExamVisibilityRequest request,
                [FromServices] PostgresExamCatalog catalog, HttpContext context, IConfiguration configuration,
                CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(context, configuration, request.RequestId,
                () => catalog.SetVisibilityAsync(examId, request,
                    ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context), cancellationToken)));

        group.MapPut("/{examId}/hiding", async (string examId, SetExamHidingRequest request,
                [FromServices] PostgresExamCatalog catalog, HttpContext context, IConfiguration configuration,
                CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(context, configuration, request.RequestId,
                () => catalog.SetHidingAsync(examId, request,
                    ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context), cancellationToken)));

        return api;
    }
}
