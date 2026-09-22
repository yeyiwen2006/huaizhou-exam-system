using Exam.Application.Activation;
using Exam.Application.Commands;
using Exam.Application.Participation;
using Exam.Application.Submissions;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Mvc;

namespace Exam.Server.Api;

public static class ExamApiEndpointExtensions
{
    public static IEndpointRouteBuilder MapExamApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1");
        api.MapActivationApi();
        api.MapParticipationApi();
        api.MapReliableCommandApi();
        api.MapSubmissionApi();
        api.MapVersioningApi();
        api.MapResourceApi();
        return endpoints;
    }

    public static RouteGroupBuilder MapResourceApi(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/resources").WithTags("A06 Encrypted Resources");
        group.MapGet("/{resourceId}", async (
            string resourceId,
            string participationId,
            long entryVersion,
            string deviceId,
            long authorizationVersion,
            IParticipationAccessReader accessReader,
            [FromServices] PostgresVersionedResourceStore resources,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var access = await accessReader.FindAsync(participationId, cancellationToken);
            if (access is null || access.EntryVersion != entryVersion ||
                !string.Equals(access.AuthorizedDeviceId, deviceId, StringComparison.Ordinal) ||
                access.AuthorizationVersion != authorizationVersion)
            {
                return ApiEndpointResults.Error(
                    context,
                    configuration,
                    context.TraceIdentifier,
                    ParticipationErrorCodes.AuthorizationRevoked,
                    "当前设备无权下载该参试记录的题包。",
                    StatusCodes.Status403Forbidden);
            }

            var resource = await resources.FindByIdAsync(resourceId, cancellationToken);
            if (resource is null ||
                !string.Equals(resource.ExamId, access.ExamId, StringComparison.Ordinal) ||
                !string.Equals(resource.ResourceKind, "paper-package", StringComparison.Ordinal) ||
                resource.Version != access.PaperVersion)
            {
                return ApiEndpointResults.Error(
                    context,
                    configuration,
                    context.TraceIdentifier,
                    "RESOURCE_NOT_FOUND",
                    "找不到与当前参试版本匹配的题包。",
                    StatusCodes.Status404NotFound);
            }

            return Results.File(
                resource.EncryptedContent,
                "application/vnd.huaizhou.exam-package",
                fileDownloadName: $"{resource.ResourceId}.epkg",
                enableRangeProcessing: false);
        });
        return group;
    }

    public static RouteGroupBuilder MapActivationApi(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/activation").WithTags("A02 Activation");

        group.MapPost("/requests", async (
            SubmitActivationRequest request,
            ActivationService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(
                context,
                configuration,
                request.RequestId,
                () => service.SubmitRequestAsync(request, cancellationToken),
                StatusCodes.Status201Created));

        group.MapPost("/status", async (
            GetActivationStatusRequest request,
            ActivationService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(
                context,
                configuration,
                context.TraceIdentifier,
                () => service.GetRequestStatusAsync(request, cancellationToken)));

        group.MapPost("/approve", async (
            ApproveActivationRequest request,
            ActivationService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(
                context,
                configuration,
                request.RequestId,
                () => service.ApproveAsync(request, cancellationToken)));

        group.MapPost("/revoke", async (
            RevokeActivationRequest request,
            ActivationService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(
                context,
                configuration,
                request.RequestId,
                () => service.RevokeAsync(request, cancellationToken)));

        group.MapPost("/reactivate", async (
            ReactivateDeviceRequest request,
            ActivationService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(
                context,
                configuration,
                request.RequestId,
                () => service.ReactivateAsync(request, cancellationToken)));

        group.MapGet("/devices/{deviceId}", async (
            string deviceId,
            ActivationService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var result = await service.FindDeviceAsync(deviceId, cancellationToken);
            return result is null
                ? ApiEndpointResults.Error(
                    context,
                    configuration,
                    context.TraceIdentifier,
                    ActivationErrorCodes.DeviceNotFound,
                    "找不到设备登记。",
                    StatusCodes.Status404NotFound)
                : ApiEndpointResults.Success(context, configuration, context.TraceIdentifier, result);
        });

        return group;
    }

    public static RouteGroupBuilder MapParticipationApi(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/participation").WithTags("A03 Participation");

        group.MapPost("/login", async (
            LoginStudentRequest request,
            ParticipationService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(
                context, configuration, request.RequestId,
                () => service.LoginAsync(request, cancellationToken)));

        group.MapPost("/prepare-entry", async (
            PrepareExamEntryRequest request,
            ParticipationService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(
                context, configuration, request.RequestId,
                () => service.PrepareEntryAsync(
                    request,
                    ExamApiAccessMiddleware.RequireAuthenticatedDeviceId(context),
                    cancellationToken)));

        group.MapPost("/confirm", async (
            ConfirmParticipationRequest request,
            ParticipationService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(
                context, configuration, request.RequestId,
                () => service.ConfirmParticipationAsync(
                    request,
                    ExamApiAccessMiddleware.RequireAuthenticatedDeviceId(context),
                    cancellationToken)));

        group.MapPost("/withdraw", async (
            WithdrawParticipationRequest request,
            ParticipationService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(
                context, configuration, request.RequestId,
                () => service.WithdrawAsync(
                    request,
                    ExamApiAccessMiddleware.RequireAuthenticatedDeviceId(context),
                    cancellationToken)));

        group.MapPost("/device-decisions", async (
            DeviceDecisionRequest request,
            ParticipationService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(
                context, configuration, request.RequestId,
                () => service.DecideDeviceAsync(request, cancellationToken)));

        group.MapPost("/authorize-write", async (
            AuthorizationCheckRequest request,
            ParticipationService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(
                context, configuration, context.TraceIdentifier,
                () => service.EnsureAuthorizedForWriteAsync(request, cancellationToken)));

        group.MapPost("/finalize", async (
            FinalizeParticipationRequest request,
            ParticipationService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(
                context, configuration, request.RequestId,
                () => service.FinalizeAsync(request, cancellationToken)));

        group.MapPost("/exams/advance-to-answering", async (
            AdvanceExamToAnsweringRequest request,
            ParticipationService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(
                context, configuration, request.RequestId,
                () => service.AdvanceExamToAnsweringAsync(request, cancellationToken)));

        group.MapGet("/{participationId}", async (
            string participationId,
            ParticipationService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var result = await service.FindParticipationAsync(participationId, cancellationToken);
            return result is null
                ? ApiEndpointResults.Error(
                    context,
                    configuration,
                    context.TraceIdentifier,
                    "PARTICIPATION_NOT_FOUND",
                    "找不到参试记录。",
                    StatusCodes.Status404NotFound)
                : ApiEndpointResults.Success(context, configuration, context.TraceIdentifier, result);
        });

        group.MapGet("/exams/{examId}/actual", async (
            string examId,
            ParticipationService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            await ApiEndpointResults.ExecuteRuleAsync(
                context, configuration, context.TraceIdentifier,
                () => service.ListActualParticipationsAsync(examId, cancellationToken)));

        return group;
    }

    public static RouteGroupBuilder MapReliableCommandApi(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/commands").WithTags("A04 Reliable Commands");

        group.MapGet("/time", (
            ReliableCommandService service,
            HttpContext context,
            IConfiguration configuration) =>
            ApiEndpointResults.Success(
                context,
                configuration,
                context.TraceIdentifier,
                service.GetServerTime()));

        group.MapPost("/", async (
            CreateCommandEventRequest request,
            ReliableCommandService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            ApiEndpointResults.FromCommand(
                context,
                configuration,
                request.RequestId,
                await service.CreateAsync(request, cancellationToken)));

        group.MapGet("/due", async (
            string participationId,
            long entryVersion,
            string deviceId,
            long authorizationVersion,
            string serverId,
            long serverEpoch,
            ReliableCommandService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            ApiEndpointResults.FromCommand(
                context,
                configuration,
                context.TraceIdentifier,
                await service.GetDueForDeviceAsync(
                    participationId,
                    entryVersion,
                    deviceId,
                    authorizationVersion,
                    serverId,
                    serverEpoch,
                    cancellationToken)));

        group.MapPost("/{eventId}/resend", async (
            string eventId,
            ResendCommandApiRequest request,
            ReliableCommandService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return ApiEndpointResults.InvalidRequestId(context, configuration);
            }

            return ApiEndpointResults.FromCommand(
                context,
                configuration,
                request.RequestId,
                await service.ResendAsync(
                    eventId,
                    request.ParticipationId,
                    request.EntryVersion,
                    request.DeviceId,
                    request.AuthorizationVersion,
                    cancellationToken));
        });

        group.MapPost("/acknowledgements", async (
            AcknowledgeCommandRequest request,
            ReliableCommandService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            ApiEndpointResults.FromCommand(
                context,
                configuration,
                request.RequestId,
                await service.AcknowledgeAsync(request, cancellationToken)));

        return group;
    }

    public static RouteGroupBuilder MapSubmissionApi(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/submissions").WithTags("A05 Submissions");

        group.MapPut("/progress", async (
            SyncProgressRequest request,
            SubmissionService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            ApiEndpointResults.FromSubmission(
                context,
                configuration,
                request.RequestId,
                await service.SyncProgressAsync(request, cancellationToken)));

        group.MapPost("/final", async (
            SubmitFinalAnswersRequest request,
            SubmissionService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            ApiEndpointResults.FromSubmission(
                context,
                configuration,
                request.RequestId,
                await service.SubmitAsync(request, cancellationToken)));

        group.MapGet("/receipts", async (
            string participationId,
            long entryVersion,
            string deviceId,
            long authorizationVersion,
            SubmissionService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            ApiEndpointResults.FromSubmission(
                context,
                configuration,
                context.TraceIdentifier,
                await service.FindReceiptForDeviceAsync(
                    participationId,
                    entryVersion,
                    deviceId,
                    authorizationVersion,
                    cancellationToken)));

        group.MapPost("/collection/evaluate", async (
            EvaluateCollectionApiRequest request,
            CollectionCompletionService service,
            HttpContext context,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return ApiEndpointResults.InvalidRequestId(context, configuration);
            }

            return await ApiEndpointResults.ExecuteRuleAsync(
                context,
                configuration,
                request.RequestId,
                () => service.EvaluateAsync(
                    request.ExamId,
                    request.RosterVersion,
                    request.ActualParticipationIds,
                    cancellationToken));
        });

        return group;
    }

    public static RouteGroupBuilder MapVersioningApi(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/versioning").WithTags("A06 Versioning");

        group.MapPost("/exams", (
            CreateVersionedExamApiRequest request,
            IExamVersioningService service,
            TimeProvider timeProvider,
            HttpContext context,
            IConfiguration configuration) =>
        {
            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return ApiEndpointResults.InvalidRequestId(context, configuration);
            }

            return ApiEndpointResults.FromVersioning(
                context,
                configuration,
                request.RequestId,
                service.CreateExam(new CreateVersionedExamRequest(
                    request.ExamId,
                    request.Schedule,
                    timeProvider.GetUtcNow(),
                    request.RequestId)),
                StatusCodes.Status201Created);
        });

        group.MapGet("/exams/{examId}", (
            string examId,
            IExamVersioningService service,
            HttpContext context,
            IConfiguration configuration) =>
            ApiEndpointResults.FromVersioning(
                context,
                configuration,
                context.TraceIdentifier,
                service.GetExam(examId)));

        group.MapPut("/exams/{examId}/schedule", (
            string examId,
            UpdateScheduleApiRequest request,
            IExamVersioningService service,
            TimeProvider timeProvider,
            HttpContext context,
            IConfiguration configuration) =>
        {
            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return ApiEndpointResults.InvalidRequestId(context, configuration);
            }

            return ApiEndpointResults.FromVersioning(
                context,
                configuration,
                request.RequestId,
                service.UpdateSchedule(new UpdateScheduleRequest(
                    examId,
                    request.ExpectedVersion,
                    request.Schedule,
                    timeProvider.GetUtcNow(),
                    request.RequestId)));
        });

        group.MapPost("/exams/{examId}/schedule/triggers", (
            string examId,
            ExecuteScheduleTriggerApiRequest request,
            IExamVersioningService service,
            TimeProvider timeProvider,
            HttpContext context,
            IConfiguration configuration) =>
        {
            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return ApiEndpointResults.InvalidRequestId(context, configuration);
            }

            return ApiEndpointResults.FromVersioning(
                context,
                configuration,
                request.RequestId,
                service.ExecuteScheduleTrigger(new ExecuteScheduleTriggerRequest(
                    examId,
                    request.ScheduleVersion,
                    request.Trigger,
                    timeProvider.GetUtcNow(),
                    request.RequestId)));
        });

        group.MapPost("/exams/{examId}/paper-publications", (
            string examId,
            PublishPaperApiRequest request,
            IExamVersioningService service,
            TimeProvider timeProvider,
            HttpContext context,
            IConfiguration configuration) =>
        {
            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return ApiEndpointResults.InvalidRequestId(context, configuration);
            }

            return ApiEndpointResults.FromVersioning(
                context,
                configuration,
                request.RequestId,
                service.PublishPaper(new PublishPaperRequest(
                    examId,
                    request.ExpectedVersion,
                    request.ArtifactId,
                    request.Structure,
                    request.ChangedQuestionIds,
                    request.Validation,
                    request.Operator,
                    request.Reason,
                    timeProvider.GetUtcNow(),
                    request.RequestId)));
        });

        group.MapPost("/exams/{examId}/grading-publications", (
            string examId,
            PublishGradingRulesApiRequest request,
            IExamVersioningService service,
            TimeProvider timeProvider,
            HttpContext context,
            IConfiguration configuration) =>
        {
            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return ApiEndpointResults.InvalidRequestId(context, configuration);
            }

            return ApiEndpointResults.FromVersioning(
                context,
                configuration,
                request.RequestId,
                service.PublishGradingRules(new PublishGradingRulesRequest(
                    examId,
                    request.ExpectedVersion,
                    request.RuleSetId,
                    request.Validation,
                    request.Operator,
                    request.Reason,
                    timeProvider.GetUtcNow(),
                    request.RequestId)));
        });

        group.MapPost("/exams/{examId}/participants", (
            string examId,
            RegisterVersionedParticipantApiRequest request,
            IExamVersioningService service,
            HttpContext context,
            IConfiguration configuration) =>
        {
            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return ApiEndpointResults.InvalidRequestId(context, configuration);
            }

            return ApiEndpointResults.FromVersioning(
                context,
                configuration,
                request.RequestId,
                service.RegisterParticipant(new RegisterVersionedParticipantRequest(
                    examId,
                    request.ParticipantId,
                    request.InitialState,
                    request.RequestId)));
        });

        group.MapPut("/exams/{examId}/participants/{participantId}/state", (
            string examId,
            string participantId,
            ChangeParticipantStateApiRequest request,
            IExamVersioningService service,
            HttpContext context,
            IConfiguration configuration) =>
        {
            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return ApiEndpointResults.InvalidRequestId(context, configuration);
            }

            return ApiEndpointResults.FromVersioning(
                context,
                configuration,
                request.RequestId,
                service.ChangeParticipantState(new ChangeParticipantStateRequest(
                    examId,
                    participantId,
                    request.State,
                    request.RequestId)));
        });

        group.MapPost("/exams/{examId}/participants/{participantId}/paper-transfer", (
            string examId,
            string participantId,
            ReportPaperTransferApiRequest request,
            IExamVersioningService service,
            HttpContext context,
            IConfiguration configuration) =>
        {
            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return ApiEndpointResults.InvalidRequestId(context, configuration);
            }

            return ApiEndpointResults.FromVersioning(
                context,
                configuration,
                request.RequestId,
                service.ReportPaperTransfer(new ReportPaperTransferRequest(
                    examId,
                    participantId,
                    request.PaperVersion,
                    request.InstalledPaperVersion,
                    request.Outcome,
                    request.Error,
                    request.RequestId,
                    request.DeviceId,
                    request.EntryVersion,
                    request.AuthorizationVersion)));
        });

        group.MapPost("/exams/{examId}/participants/{participantId}/paper-review", (
            string examId,
            string participantId,
            ConfirmPaperReviewApiRequest request,
            IExamVersioningService service,
            HttpContext context,
            IConfiguration configuration) =>
        {
            if (string.IsNullOrWhiteSpace(request.RequestId))
            {
                return ApiEndpointResults.InvalidRequestId(context, configuration);
            }

            return ApiEndpointResults.FromVersioning(
                context,
                configuration,
                request.RequestId,
                service.ConfirmPaperReview(new ConfirmPaperReviewRequest(
                    examId,
                    participantId,
                    request.PaperVersion,
                    request.RequestId,
                    request.DeviceId,
                    request.EntryVersion,
                    request.AuthorizationVersion)));
        });

        return group;
    }
}
