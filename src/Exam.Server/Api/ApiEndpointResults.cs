using Exam.Application.Activation;
using Exam.Application.Participation;
using Exam.Contracts.Activation;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Server.Runtime;

namespace Exam.Server.Api;

internal static class ApiEndpointResults
{
    public static IResult Success<T>(
        HttpContext context,
        IConfiguration configuration,
        string correlationId,
        T value,
        int statusCode = StatusCodes.Status200OK) =>
        Results.Json(
            Envelope(context, configuration, correlationId, value, null),
            statusCode: statusCode);

    public static IResult Error(
        HttpContext context,
        IConfiguration configuration,
        string correlationId,
        string code,
        string message,
        int statusCode,
        bool retryable = false) =>
        Results.Json(
            Envelope<object>(
                context,
                configuration,
                correlationId,
                null,
                new ApiError(code, message, Retryable: retryable)),
            statusCode: statusCode);

    public static async Task<IResult> ExecuteRuleAsync<T>(
        HttpContext context,
        IConfiguration configuration,
        string correlationId,
        Func<ValueTask<T>> action,
        int successStatus = StatusCodes.Status200OK)
    {
        try
        {
            return Success(context, configuration, correlationId, await action(), successStatus);
        }
        catch (ActivationRuleException exception)
        {
            return Error(
                context,
                configuration,
                correlationId,
                exception.Code,
                exception.Message,
                StatusForActivation(exception.Code));
        }
        catch (ParticipationRuleException exception)
        {
            return Error(
                context,
                configuration,
                correlationId,
                exception.Code,
                exception.Message,
                StatusForParticipation(exception.Code));
        }
        catch (ArgumentException exception)
        {
            return Error(
                context,
                configuration,
                correlationId,
                ErrorCodes.InvalidInput,
                exception.Message,
                StatusCodes.Status400BadRequest);
        }
    }

    public static IResult FromCommand<T>(
        HttpContext context,
        IConfiguration configuration,
        string correlationId,
        CommandOperationResult<T> result)
    {
        if (result.IsSuccess && result.Value is not null)
        {
            return Success(context, configuration, correlationId, result.Value);
        }

        var status = result.ErrorCode switch
        {
            CommandErrorCode.InvalidRequest => StatusCodes.Status400BadRequest,
            CommandErrorCode.EventNotFound => StatusCodes.Status404NotFound,
            CommandErrorCode.AuthorizationRevoked => StatusCodes.Status403Forbidden,
            CommandErrorCode.PersistenceFailed => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status409Conflict
        };
        return Error(
            context,
            configuration,
            correlationId,
            CommandCode(result.ErrorCode),
            result.Message,
            status,
            result.ErrorCode == CommandErrorCode.PersistenceFailed);
    }

    public static IResult FromSubmission<T>(
        HttpContext context,
        IConfiguration configuration,
        string correlationId,
        SubmissionOperationResult<T> result)
    {
        if (result.IsSuccess && result.Value is not null)
        {
            return Success(context, configuration, correlationId, result.Value);
        }

        var status = result.ErrorCode switch
        {
            SubmissionErrorCode.InvalidRequest => StatusCodes.Status400BadRequest,
            SubmissionErrorCode.AuthorizationRevoked => StatusCodes.Status403Forbidden,
            SubmissionErrorCode.NotFound => StatusCodes.Status404NotFound,
            SubmissionErrorCode.ServiceRecovering or SubmissionErrorCode.PersistenceFailed =>
                StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status409Conflict
        };
        return Error(
            context,
            configuration,
            correlationId,
            SubmissionCode(result.ErrorCode),
            result.Message,
            status,
            result.ErrorCode is SubmissionErrorCode.ServiceRecovering or SubmissionErrorCode.PersistenceFailed);
    }

    public static IResult FromVersioning<T>(
        HttpContext context,
        IConfiguration configuration,
        string correlationId,
        VersioningResult<T> result,
        int successStatus = StatusCodes.Status200OK)
    {
        if (result.Succeeded && result.Value is not null)
        {
            return Success(context, configuration, correlationId, result.Value, successStatus);
        }

        var status = result.ErrorCode switch
        {
            VersioningErrorCode.ExamNotFound or VersioningErrorCode.ParticipantNotFound =>
                StatusCodes.Status404NotFound,
            VersioningErrorCode.InvalidSchedule or VersioningErrorCode.InvalidRequest or
                VersioningErrorCode.PublicationNotReady => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status409Conflict
        };
        return Error(
            context,
            configuration,
            correlationId,
            VersioningCode(result.ErrorCode),
            result.Error ?? "Versioning operation failed.",
            status);
    }

    public static IResult InvalidRequestId(
        HttpContext context,
        IConfiguration configuration) =>
        Error(
            context,
            configuration,
            context.TraceIdentifier,
            ErrorCodes.InvalidInput,
            "requestId 不能为空。",
            StatusCodes.Status400BadRequest);

    private static ApiEnvelope<T> Envelope<T>(
        HttpContext context,
        IConfiguration configuration,
        string correlationId,
        T? value,
        ApiError? error)
    {
        var identity = configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>()
            ?? new ServerIdentityOptions();
        return new ApiEnvelope<T>(
            identity.ServerId,
            identity.NodeId,
            identity.ServerEpoch,
            string.IsNullOrWhiteSpace(correlationId) ? context.TraceIdentifier : correlationId,
            value,
            error);
    }

    private static int StatusForActivation(string code) => code switch
    {
        ActivationErrorCodes.InvalidRequest => StatusCodes.Status400BadRequest,
        ActivationErrorCodes.InvalidCredential or ActivationErrorCodes.NotActivated => StatusCodes.Status403Forbidden,
        ActivationErrorCodes.ActivationRequestNotFound or ActivationErrorCodes.DeviceNotFound =>
            StatusCodes.Status404NotFound,
        _ => StatusCodes.Status409Conflict
    };

    private static int StatusForParticipation(string code) => code switch
    {
        Exam.Contracts.Operators.OperatorErrorCodes.InvalidCredentials => StatusCodes.Status401Unauthorized,
        Exam.Contracts.Operators.OperatorErrorCodes.PendingApproval or Exam.Contracts.Operators.OperatorErrorCodes.Disabled or
            Exam.Contracts.Operators.OperatorErrorCodes.NotAdministrator => StatusCodes.Status403Forbidden,
        ParticipationErrorCodes.InvalidRequest => StatusCodes.Status400BadRequest,
        ParticipationErrorCodes.NotActivated or ParticipationErrorCodes.AuthorizationRevoked or
            ParticipationErrorCodes.RosterMismatch =>
            StatusCodes.Status403Forbidden,
        ParticipationErrorCodes.SessionNotFound or ParticipationErrorCodes.ExamNotFound or
            ParticipationErrorCodes.PreparationNotFound => StatusCodes.Status404NotFound,
        _ => StatusCodes.Status409Conflict
    };

    private static string CommandCode(CommandErrorCode code) => code switch
    {
        CommandErrorCode.None => "NONE",
        CommandErrorCode.InvalidRequest => "INVALID_REQUEST",
        CommandErrorCode.EventNotFound => "EVENT_NOT_FOUND",
        CommandErrorCode.AuthorizationRevoked => "AUTHORIZATION_REVOKED",
        CommandErrorCode.VersionConflict => "VERSION_CONFLICT",
        CommandErrorCode.KeyNotReleased => "KEY_NOT_RELEASED",
        CommandErrorCode.StaleServerEpoch => "STALE_SERVER_EPOCH",
        CommandErrorCode.PersistenceFailed => "PERSISTENCE_FAILED",
        _ => "COMMAND_ERROR"
    };

    private static string SubmissionCode(SubmissionErrorCode code) => code switch
    {
        SubmissionErrorCode.None => "NONE",
        SubmissionErrorCode.InvalidRequest => "INVALID_REQUEST",
        SubmissionErrorCode.AuthorizationRevoked => "AUTHORIZATION_REVOKED",
        SubmissionErrorCode.VersionConflict => "VERSION_CONFLICT",
        SubmissionErrorCode.AlreadySubmitted => "ALREADY_SUBMITTED",
        SubmissionErrorCode.SubmissionIdConflict => "SUBMISSION_ID_CONFLICT",
        SubmissionErrorCode.NotFound => "NOT_FOUND",
        SubmissionErrorCode.ServiceRecovering => "SERVICE_RECOVERING",
        SubmissionErrorCode.PersistenceFailed => "PERSISTENCE_FAILED",
        _ => "SUBMISSION_ERROR"
    };

    private static string VersioningCode(VersioningErrorCode code) => code switch
    {
        VersioningErrorCode.None => "NONE",
        VersioningErrorCode.ExamAlreadyExists => "EXAM_ALREADY_EXISTS",
        VersioningErrorCode.ExamNotFound => "EXAM_NOT_FOUND",
        VersioningErrorCode.ParticipantNotFound => "PARTICIPANT_NOT_FOUND",
        VersioningErrorCode.VersionConflict => "VERSION_CONFLICT",
        VersioningErrorCode.InvalidSchedule => "INVALID_SCHEDULE",
        VersioningErrorCode.InvalidRequest => "INVALID_REQUEST",
        VersioningErrorCode.InvalidStateTransition => "INVALID_STATE_TRANSITION",
        VersioningErrorCode.ExamEnded => "EXAM_ENDED",
        VersioningErrorCode.PublicationNotReady => "PUBLICATION_NOT_READY",
        VersioningErrorCode.StructuralChangeRejected => "STRUCTURAL_CHANGE_REJECTED",
        VersioningErrorCode.StaleScheduleTask => "STALE_SCHEDULE_TASK",
        VersioningErrorCode.ScheduleTaskNotDue => "SCHEDULE_TASK_NOT_DUE",
        VersioningErrorCode.ResourceRollbackRejected => "RESOURCE_ROLLBACK_REJECTED",
        VersioningErrorCode.IdempotencyConflict => "IDEMPOTENCY_CONFLICT",
        VersioningErrorCode.MaterialsArchiveInvalid => "MATERIALS_ARCHIVE_INVALID",
        VersioningErrorCode.MaterialsFormatUnsupported => "MATERIALS_FORMAT_UNSUPPORTED",
        VersioningErrorCode.MaterialsBaselineMissing => "MATERIALS_BASELINE_MISSING",
        VersioningErrorCode.ChangedQuestionIdsMissing => "CHANGED_QUESTION_IDS_MISSING",
        VersioningErrorCode.ChangedQuestionIdsUnexpected => "CHANGED_QUESTION_IDS_UNEXPECTED",
        VersioningErrorCode.NoContentChange => "NO_CONTENT_CHANGE",
        _ => "VERSIONING_ERROR"
    };
}
