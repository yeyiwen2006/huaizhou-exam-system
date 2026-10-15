using Exam.Contracts.Activation;
using Exam.Contracts.Administration;
using Exam.Infrastructure.Postgres.Administration;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Mvc;

namespace Exam.Server.Api;

/// <summary>名册、试场、设备与实时监控的权威业务入口；设备上报与考务查询严格分离。</summary>
public static class AdministrationEndpoints
{
    public static RouteGroupBuilder MapAdministrationApi(this RouteGroupBuilder api)
    {
        api.MapGet("/exams/{examId}/roster", async (string examId, [FromServices] PostgresCandidateRosterService service,
                HttpContext context, IConfiguration configuration, CancellationToken ct) =>
            await Rule(context,configuration,context.TraceIdentifier,()=>service.GetAsync(examId,ct)))
            .WithMetadata(new ReadOnlyObservation());

        api.MapPost("/exams/{examId}/roster/import", async (string examId, ImportCandidateRosterRequest request, [FromServices] PostgresCandidateRosterService service,
                HttpContext context, IConfiguration configuration, CancellationToken ct) =>
            await Rule(context,configuration,request.RequestId,()=>service.ImportAsync(examId,request,Actor(context),ct)))
            .WithMetadata(new ConfirmationBeforeResult()).AddEndpointFilter<ConfirmationBeforeResult>();

        api.MapPut("/exams/{examId}/roster/{candidateId}", async (string examId, string candidateId, UpdateCandidateRosterRequest request, [FromServices] PostgresCandidateRosterService service,
                HttpContext context, IConfiguration configuration, CancellationToken ct) =>
            await Rule(context,configuration,request.RequestId,()=>service.UpdateAsync(examId,candidateId,request,Actor(context),ct)))
            .WithMetadata(new ConfirmationBeforeResult()).AddEndpointFilter<ConfirmationBeforeResult>();

        api.MapDelete("/exams/{examId}/roster/{candidateId}", async (string examId, string candidateId, [FromBody] DeleteCandidateRosterRequest request, [FromServices] PostgresCandidateRosterService service,
                HttpContext context, IConfiguration configuration, CancellationToken ct) =>
            await Rule(context,configuration,request.RequestId,()=>service.DeleteAsync(examId,candidateId,request,Actor(context),ct)))
            .WithMetadata(new ConfirmationBeforeResult()).AddEndpointFilter<ConfirmationBeforeResult>();

        api.MapGet("/rooms", async ([FromServices] PostgresRoomService service,
                HttpContext context, IConfiguration configuration, CancellationToken ct) =>
            await Rule(context,configuration,context.TraceIdentifier,()=>service.ListAsync(ct)))
            .WithMetadata(new ReadOnlyObservation());

        api.MapPost("/rooms", async (CreateRoomGroupRequest request, [FromServices] PostgresRoomService service,
                HttpContext context, IConfiguration configuration, CancellationToken ct) =>
            await Rule(context,configuration,request.RequestId,()=>service.CreateAsync(request,Actor(context),ct)))
            .WithMetadata(new ConfirmationBeforeResult()).AddEndpointFilter<ConfirmationBeforeResult>();

        api.MapPut("/rooms/{groupId}", async (string groupId, RenameRoomGroupRequest request, [FromServices] PostgresRoomService service,
                HttpContext context, IConfiguration configuration, CancellationToken ct) =>
            await Rule(context,configuration,request.RequestId,()=>service.RenameAsync(groupId,request,Actor(context),ct)))
            .WithMetadata(new ConfirmationBeforeResult()).AddEndpointFilter<ConfirmationBeforeResult>();

        api.MapDelete("/rooms/{groupId}", async (string groupId, [FromBody] DeleteRoomGroupRequest request, [FromServices] PostgresRoomService service,
                HttpContext context, IConfiguration configuration, CancellationToken ct) =>
            await Rule(context,configuration,request.RequestId,()=>service.DeleteAsync(groupId,request,Actor(context),ct)))
            .WithMetadata(new ConfirmationBeforeResult()).AddEndpointFilter<ConfirmationBeforeResult>();

        api.MapPost("/rooms/move-devices", async (MoveDevicesRequest request, [FromServices] PostgresRoomService service,
                HttpContext context, IConfiguration configuration, CancellationToken ct) =>
            await Rule(context,configuration,request.RequestId,()=>service.MoveAsync(request,Actor(context),ct)))
            .WithMetadata(new ConfirmationBeforeResult()).AddEndpointFilter<ConfirmationBeforeResult>();

        api.MapGet("/devices", async ([FromQuery] string? roomGroupId, [FromQuery] string? query, [FromQuery] ActivationStatus? status, [FromQuery] int? offset, [FromQuery] int? limit, [FromServices] PostgresDeviceDirectory service,
                HttpContext context, IConfiguration configuration, CancellationToken ct) =>
            await Rule(context,configuration,context.TraceIdentifier,()=>service.ListAsync(roomGroupId,query,status,offset??0,limit??500,ct)))
            .WithMetadata(new ReadOnlyObservation());

        api.MapGet("/devices/{clientNumber}/room-history", async (string clientNumber, [FromServices] PostgresRoomService service,
                HttpContext context, IConfiguration configuration, CancellationToken ct) =>
            await Rule(context,configuration,context.TraceIdentifier,()=>service.HistoryAsync(clientNumber,ct)))
            .WithMetadata(new ReadOnlyObservation());

        api.MapGet("/exams/{examId}/monitoring", async (string examId, [FromQuery] string? roomGroupId, [FromQuery] string? query, [FromServices] PostgresMonitoringService service,
                HttpContext context, IConfiguration configuration, CancellationToken ct) =>
            await Rule(context,configuration,context.TraceIdentifier,()=>service.GetAsync(examId,roomGroupId,query,ct)))
            .WithMetadata(new ReadOnlyObservation());

        api.MapGet("/exams/{examId}/uncollected", async (string examId, [FromServices] PostgresMonitoringService service,
                HttpContext context, IConfiguration configuration, CancellationToken ct) =>
            await Rule(context,configuration,context.TraceIdentifier,()=>ReadMissingAsync(service,examId,ct)))
            .WithMetadata(new ReadOnlyObservation());

        api.MapPost("/student/presence/open", async (OpenDevicePresenceRequest request, [FromServices] PostgresDevicePresenceService service,
                HttpContext context, IConfiguration configuration, CancellationToken ct) =>
            await Rule(context,configuration,request.RequestId,()=>service.OpenAsync(Device(context),request,ct)))
            .WithMetadata(new TransientPresenceUpdate());

        api.MapPost("/student/presence", async (UpdateDevicePresenceRequest request, [FromServices] PostgresDevicePresenceService service,
                HttpContext context, IConfiguration configuration, CancellationToken ct) =>
            await Rule(context,configuration,context.TraceIdentifier,()=>service.UpdateAsync(Device(context),request,ct)))
            .WithMetadata(new TransientPresenceUpdate());

        api.MapPost("/exams/{examId}/publications",async (string examId,PublishExamBundleApiRequest request,
                [FromServices] PostgresBundlePublicationService service,[FromServices] Exam.Server.Runtime.GradingRecalculationTrigger recalculation,
                HttpContext context,IConfiguration configuration,CancellationToken ct) =>
        {
            var result=await service.PublishAsync(examId,request,Actor(context),ct);
            if(result.Succeeded) await recalculation.TryTriggerAsync(examId,ct);
            return ApiEndpointResults.FromVersioning(context,configuration,request.RequestId,result);
        }).WithMetadata(new ConfirmationBeforeResult()).AddEndpointFilter<ConfirmationBeforeResult>();
        api.MapPublicationUploadApi();
        return api;
    }
    private static string Actor(HttpContext context)=>ExamApiAccessMiddleware.RequireAuthenticatedOperatorId(context);
    private static string Device(HttpContext context)=>ExamApiAccessMiddleware.RequireAuthenticatedDeviceId(context);
    private static Task<IResult> Rule<T>(HttpContext context,IConfiguration configuration,string correlationId,Func<Task<T>> action)=>
        ApiEndpointResults.ExecuteRuleAsync(context,configuration,correlationId,async()=>await action());
    private static async Task<IReadOnlyList<MonitoringParticipant>> ReadMissingAsync(PostgresMonitoringService service,string examId,CancellationToken ct)=>
        (await service.GetAsync(examId,ct:ct)).Uncollected;
}
