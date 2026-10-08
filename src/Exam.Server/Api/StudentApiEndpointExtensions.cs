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
        api.MapPost("/student/login", async (
            Exam.Contracts.Participation.LoginStudentRequest request,
            Exam.Application.Participation.ParticipationService participation,
            ActivationService connections,
            HttpContext context, IConfiguration configuration, CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(context, configuration, request.RequestId,
                () => participation.ConnectAndLoginAsync(request, connections, cancellationToken)))
            .WithTags("Student Connection");

        api.MapGet("/student/server-topology", (HttpContext context, IConfiguration configuration) =>
        {
            _ = ExamApiAccessMiddleware.RequireAuthenticatedDeviceId(context);
            context.Response.Headers.CacheControl = "no-store";
            var topology = StudentFailoverConfiguration.Read(configuration);
            return topology is null ? ApiEndpointResults.Error(context, configuration, context.TraceIdentifier,
                "TOPOLOGY_NOT_CONFIGURED", "可信主备信息尚未配置，请联系考务人员。", StatusCodes.Status503ServiceUnavailable, true)
                : ApiEndpointResults.Success(context, configuration, context.TraceIdentifier, topology);
        }).WithTags("Student Connection");

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

        return api;
    }
}
