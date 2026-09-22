using Exam.Application.Activation;
using Exam.Contracts.Activation;
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

        return api;
    }
}
