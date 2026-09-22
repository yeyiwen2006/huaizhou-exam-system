using Exam.Application.Activation;
using Exam.Contracts.Activation;
using Exam.Contracts.Student;
using Exam.Infrastructure.Postgres.Student;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Mvc;

namespace Exam.Server.Api;

public static class StudentApiEndpointExtensions
{
    public static RouteGroupBuilder MapStudentApi(this RouteGroupBuilder api)
    {
        api.MapPost("/activation/cancel-own", async (
            CancelOwnActivationRequest request,
            ActivationService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(
                context, configuration, request.RequestId,
                () => service.CancelOwnAsync(request, cancellationToken)))
            .WithTags("Student Activation");

        api.MapGet("/student/exams", async (
            string sessionId,
            [FromServices] PostgresStudentDirectory directory,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(
                context, configuration, context.TraceIdentifier,
                () => directory.ListExamsAsync(
                    sessionId,
                    ExamApiAccessMiddleware.RequireAuthenticatedDeviceId(context),
                    cancellationToken)))
            .WithTags("Student Exams");

        api.MapGet("/student/instructions", async (
            string preparationId,
            string sessionId,
            [FromServices] PostgresStudentExamMetadataService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(
                context, configuration, context.TraceIdentifier,
                () => service.GetInstructionsAsync(
                    preparationId,
                    sessionId,
                    ExamApiAccessMiddleware.RequireAuthenticatedDeviceId(context),
                    cancellationToken)))
            .WithTags("Student Exams");

        api.MapPut("/student/exams/{examId}/metadata", async (
            string examId,
            UpdateStudentExamMetadataRequest request,
            [FromServices] PostgresStudentExamMetadataService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(
                context, configuration, request.RequestId,
                () => service.UpdateAsync(
                    examId,
                    request,
                    ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context),
                    cancellationToken)))
            .WithTags("Student Exam Configuration");

        return api;
    }
}
