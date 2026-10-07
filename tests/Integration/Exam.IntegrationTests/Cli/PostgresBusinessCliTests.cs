using System.Collections.Concurrent;
using System.Text.Json;
using Exam.Application.Activation;
using Exam.Application.Commands;
using Exam.Contracts.Activation;
using Exam.Contracts.Administration;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Contracts.Versioning;
using Exam.Infrastructure.Postgres.Administration;
using Exam.Infrastructure.Postgres.Exams;
using Exam.IntegrationTests.Postgres;
using Exam.Server.Api;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Exam.IntegrationTests.Cli;

public sealed class WindowsBusinessCliFactAttribute : FactAttribute
{
    public WindowsBusinessCliFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows CLI persists protected mutation requests using current-user DPAPI.";
    }
}

[Collection(PostgresCollection.Name)]
[Trait("Category", "Postgres")]
public sealed class PostgresBusinessCliTests : IAsyncLifetime
{
    // The real startup probe inspects the whole database. Other module tests intentionally
    // leave incompatible recovery material; each CLI workflow therefore owns a fresh database.
    private readonly PostgresDatabaseFixture database = new();
    public Task InitializeAsync() => database.InitializeAsync();
    public Task DisposeAsync() => database.DisposeAsync();

    private static readonly JsonSerializerOptions Json = JsonDefaults.Create();
    private static string Id() => Guid.NewGuid().ToString("N");

    [WindowsBusinessCliFact]
    public async Task ActivationUsesRealPendingNumberAllocationAndReplaysAfterLostResponse()
    {
        await using var host = await Host.StartAsync(database);
        var activation = host.Services.GetRequiredService<ActivationService>();
        var pending = await activation.SubmitRequestAsync(new(Id(), Id(), "synthetic-device-credential"));
        Assert.Null(pending.ClientNumber);
        var room = await host.Services.GetRequiredService<PostgresRoomService>().CreateAsync(new(Id(), "合成试场-"+Id()), host.Actor);
        var list = await host.Cli("activation", "pending"); Assert.Equal(0,list.Exit);
        Assert.Contains(JsonSerializer.Deserialize<ActivationRequestResult[]>(list.Out,Json)!, x=>x.ActivationRequestId==pending.ActivationRequestId&&x.ClientNumber is null);
        var requestId=Id();
        var args=new[]{"activation","approve",pending.ActivationRequestId,room.Name,"--request-id",requestId};
        var denied=await host.CliWithToken("synthetic-invalid-token",args);Assert.NotEqual(0,denied.Exit);
        Assert.Null(await activation.FindDeviceAsync(pending.DeviceId));
        host.FailAfterCommitPath="/api/v1/activation/approve";
        var uncertain=await host.Cli(args);Assert.NotEqual(0,uncertain.Exit);Assert.Empty(uncertain.Out);
        var actual=await activation.FindDeviceAsync(pending.DeviceId);Assert.NotNull(actual);Assert.Equal(room.GroupId,actual.RoomGroupId);
        Assert.DoesNotContain(await activation.ListPendingRequestsAsync(),x=>x.ActivationRequestId==pending.ActivationRequestId);
        var replay=await host.Cli(args);Assert.Equal(0,replay.Exit);
        Assert.Equal(actual,JsonSerializer.Deserialize<ActivationResult>(replay.Out,Json));
        var approvals=host.Mutations.Where(x=>x.Path=="/api/v1/activation/approve").ToArray();Assert.Equal(2,approvals.Length);Assert.Equal(approvals[0].Body,approvals[1].Body);
        var changed=args.ToArray();changed[3]="未分组";var conflict=await host.Cli(changed);Assert.Equal(2,conflict.Exit);
        Assert.Equal(2,host.Mutations.Count(x=>x.Path=="/api/v1/activation/approve"));
        var input=CliProcess.WriteInput("{\"reason\":\"合成撤销\"}");
        try
        {
            var revoke=new[]{"activation","revoke",actual.ClientNumber,"--input",input,"--request-id",Id()};
            Assert.Equal(0,(await host.Cli(revoke)).Exit);Assert.Equal(0,(await host.Cli(revoke)).Exit);
            var revoked=await activation.FindDeviceAsync(pending.DeviceId);Assert.Equal(ActivationStatus.Revoked,revoked!.Status);Assert.Equal(actual.ClientNumber,revoked.ClientNumber);
            // A local encrypted result never substitutes for current server permission.
            Assert.NotEqual(0,(await host.CliWithToken("synthetic-invalid-token",revoke)).Exit);
        }
        finally{File.Delete(input);}
    }

    [WindowsBusinessCliFact]
    public async Task DecisionReplaysOriginalVersionAfterConflictDisappearsAndRejectsChangedIntent()
    {
        await using var host=await Host.StartAsync(database);
        var seed=await SeedAsync(host);
        var input=CliProcess.WriteInput(JsonSerializer.Serialize(new{device=seed.Other.ClientNumber,reason="合成保留新设备"}));
        try
        {
            var args=new[]{"decisions","keep",seed.Name,seed.Original.ClientNumber,"--input",input,"--request-id",Id()};
            host.FailAfterCommitPath="/api/v1/participation/device-decisions";
            Assert.NotEqual(0,(await host.Cli(args)).Exit);
            Assert.Equal(seed.Other.DeviceId,await Scalar("SELECT authorized_device_id FROM exam.participations WHERE participation_id=$1",seed.Participant));
            var monitor=host.Services.GetRequiredService<PostgresMonitoringService>();Assert.Empty(Assert.Single((await monitor.GetAsync(seed.Exam)).Participants).ConflictDevices);
            var replay=await host.Cli(args);Assert.Equal(0,replay.Exit);
            Assert.Equal(seed.Other.DeviceId,JsonSerializer.Deserialize<DeviceDecisionResult>(replay.Out,Json)!.AuthorizedDeviceId);
            var sent=host.Mutations.Where(x=>x.Path=="/api/v1/participation/device-decisions").ToArray();Assert.Equal(2,sent.Length);Assert.Equal(sent[0].Body,sent[1].Body);
            using var body=JsonDocument.Parse(sent[1].Body);Assert.Equal(host.Actor,body.RootElement.GetProperty("operatorId").GetString());Assert.Equal(1,body.RootElement.GetProperty("expectedVersion").GetInt64());
            File.WriteAllText(input,JsonSerializer.Serialize(new{device=seed.Original.ClientNumber,reason="修改原意图"}));
            Assert.Equal(2,(await host.Cli(args)).Exit);Assert.Equal(2,host.Mutations.Count(x=>x.Path=="/api/v1/participation/device-decisions"));
        }
        finally{File.Delete(input);}
    }

    [WindowsBusinessCliFact]
    public async Task CommandsAndMonitorUseRealDatabaseRoutesAndKeepOriginalEventOnRetry()
    {
        await using var host=await Host.StartAsync(database);var seed=await SeedAsync(host);
        var stream=await host.Cli("monitor","stream",seed.Name,"1","2");Assert.Equal(0,stream.Exit);
        var snapshots=stream.Out.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(x=>JsonSerializer.Deserialize<MonitoringSnapshot>(x,Json)!).ToArray();
        Assert.Equal(2,snapshots.Length);Assert.All(snapshots,x=>Assert.Equal(seed.Participant,Assert.Single(x.Participants).ParticipationId));
        var events=await host.Cli("monitor","events",seed.Name);Assert.Equal(0,events.Exit);Assert.Contains("设备冲突待裁决",events.Error);
        // Seed an ended lifecycle and a durable, uncollected participation; production resend rules still enforce validity.
        await Sql("UPDATE exam.version_state SET lifecycle='Ended',snapshot=jsonb_set(snapshot,'{lifecycleState}',to_jsonb('ended'::text)) WHERE exam_id=$1",seed.Exam);
        var service=host.Services.GetRequiredService<ReliableCommandService>();var eventId=Id();
        var created=await service.CreateAsync(new(Id(),eventId,seed.Exam,seed.Participant,1,CommandEventType.End,2,seed.Original.DeviceId,1,null,null));
        Assert.True(created.IsSuccess,created.Message);
        var args=new[]{"commands","resend",seed.Name,seed.Original.ClientNumber,"end","--request-id",Id()};
        host.FailAfterCommitPath=$"/api/v1/commands/{eventId}/resend";
        Assert.NotEqual(0,(await host.Cli(args)).Exit);
        var nextId=Id();Assert.True((await service.CreateAsync(new(Id(),nextId,seed.Exam,seed.Participant,1,CommandEventType.End,3,seed.Original.DeviceId,1,null,null))).IsSuccess);
        var replay=await host.Cli(args);Assert.Equal(0,replay.Exit);Assert.Equal(eventId,JsonSerializer.Deserialize<CommandDelivery>(replay.Out,Json)!.EventId);
        Assert.Equal(0,(await host.Cli("commands","reupload",seed.Name,seed.Original.ClientNumber,"--request-id",Id())).Exit);
        Assert.NotEqual(0,(await host.Cli("commands","resend",seed.Name,seed.Original.ClientNumber,"start")).Exit);
        // Once collected, a fresh reupload request is refused; the original final answer is untouched.
        await Sql("""
            INSERT INTO exam.final_submissions(submission_id,exam_id,candidate_id,participation_id,device_id,authorization_version,paper_version,frozen_answers,received_at,receipt_id,grading_version)
            VALUES($1,$2,$3,$4,$5,1,1,'{}'::jsonb,now(),$6,1)
            """,Id(),seed.Exam,seed.Candidate,seed.Participant,seed.Original.DeviceId,Id());
        Assert.Equal(2,(await host.Cli("commands","reupload",seed.Name,seed.Original.ClientNumber)).Exit);
    }

    [WindowsBusinessCliFact]
    public async Task TimeModificationBindsAuthenticatedActorAndPersistsAuditAcrossReplay()
    {
        await using var host = await Host.StartAsync(database);
        var seed = await SeedAsync(host);
        var requestId = Id();
        var now = DateTimeOffset.UtcNow;
        var body = new { requestId, expectedVersion = 1, schedule = new ScheduleWindow(now.AddHours(2), now.AddHours(3)),
            reason = "合成考场延时" };
        var input = CliProcess.WriteInput(JsonSerializer.Serialize(body, Json));
        try
        {
            var args = new[] { "exams", "schedule", seed.Name, "--input", input };
            var first = await host.Cli(args);
            Assert.True(first.Exit == 0, first.Error);
            var replay = await host.Cli(args);
            Assert.True(replay.Exit == 0, replay.Error);
            await using var command = database.DataSource.CreateCommand(
                "SELECT payload::text FROM exam.version_events WHERE exam_id=$1 AND operation_type='schedule.update';");
            command.Parameters.AddWithValue(seed.Exam);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            using var payload = JsonDocument.Parse(reader.GetString(0));
            Assert.Equal(host.Actor, payload.RootElement.GetProperty("operator").GetString());
            Assert.Equal(body.reason, payload.RootElement.GetProperty("reason").GetString());
            Assert.Equal(requestId, payload.RootElement.GetProperty("requestId").GetString());
            Assert.True(payload.RootElement.TryGetProperty("now", out _));
            Assert.False(await reader.ReadAsync());
            var restored = new Exam.Infrastructure.Postgres.Versioning.PostgresExamVersioningService(database.DataSource);
            Assert.Equal(2, restored.GetExam(seed.Exam).Value!.Versions.ScheduleVersion);
            File.WriteAllText(input, JsonSerializer.Serialize(new { requestId = Id(), expectedVersion = 1,
                body.schedule, reason = "并发旧版本" }, Json));
            Assert.Equal(4, (await host.Cli(args)).Exit);
            File.WriteAllText(input, JsonSerializer.Serialize(new { requestId = Id(), expectedVersion = 2,
                body.schedule, reason = "" }, Json));
            Assert.Equal(2, (await host.Cli(args)).Exit);
            Assert.Equal(2, restored.GetExam(seed.Exam).Value!.Versions.ScheduleVersion);
        }
        finally { File.Delete(input); }
    }

    private async Task<(string Exam,string Name,string Candidate,string Participant,ActivationResult Original,ActivationResult Other)> SeedAsync(Host host)
    {
        var now=DateTimeOffset.UtcNow;var catalog=host.Services.GetRequiredService<PostgresExamCatalog>();
        var detail=await catalog.CreateAsync(new(Id(),"合成CLI-"+Id(),new(now.AddHours(1),now.AddHours(2)),
            new([new("subject","合成科目","3",[new("group","合成题组","single",1,1,"3",false,new("3","0","0"),null)])])),host.Actor);
        var exam=detail.Summary.ExamId;var roster=await host.Services.GetRequiredService<PostgresCandidateRosterService>().ImportAsync(exam,new(Id(),0,[new(1,"合成考生","001","000001")]),host.Actor);
        var activation=host.Services.GetRequiredService<ActivationService>();
        async Task<ActivationResult> Device(){var request=await activation.SubmitRequestAsync(new(Id(),Id(),"synthetic-device-credential"));return await activation.ApproveAsync(new(Id(),request.ActivationRequestId,null));}
        var original=await Device();var other=await Device();var part=Id();var candidate=Assert.Single(roster.Snapshot.Entries).CandidateId;
        await Sql("INSERT INTO exam.participations(participation_id,exam_id,candidate_id,entry_version,business_state,authorized_device_id,authorization_version,version) VALUES($1,$2,$3,1,'waiting',$4,1,1)",part,exam,candidate,original.DeviceId);
        await Sql("INSERT INTO exam.participation_device_conflicts(participation_id,original_device_id,pending_device_id,detected_at) VALUES($1,$2,$3,now())",part,original.DeviceId,other.DeviceId);
        return(exam,detail.Summary.Name,candidate,part,original,other);
    }
    private async Task Sql(string sql,params object[] args){await using var c=database.DataSource.CreateCommand(sql);foreach(var a in args)c.Parameters.AddWithValue(a);await c.ExecuteNonQueryAsync();}
    private async Task<object?> Scalar(string sql,params object[] args){await using var c=database.DataSource.CreateCommand(sql);foreach(var a in args)c.Parameters.AddWithValue(a);return await c.ExecuteScalarAsync();}

    private sealed class Host(WebApplication app,string actor,string token):IAsyncDisposable
    {
        public string Actor=>actor;public IServiceProvider Services=>app.Services;
        public string? FailAfterCommitPath;
        public ConcurrentQueue<(string Path,string Body)> Mutations{get;}=new();
        public Task<(int Exit,string Out,string Error)> Cli(params string[] args)=>CliWithToken(token,args);
        public Task<(int Exit,string Out,string Error)> CliWithToken(string session,params string[] args)=>CliProcess.RunWithTokenAsync(session,[..args,"--server",app.Urls.Single()]);
        public static async Task<Host> StartAsync(PostgresDatabaseFixture database)
        {
            var actor="cli-"+Id()[..28];var token=await SyntheticOperators.LoginAsync(database.DataSource,actor);
            var builder=WebApplication.CreateBuilder();builder.Configuration.Sources.Clear();builder.Configuration.AddInMemoryCollection();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
            var identity=new ServerIdentityOptions{ServerId="cli-server-"+Id(),NodeId="node-a",ServerEpoch=1};
            builder.Configuration["ConnectionStrings:Exam"]=database.ConnectionString;
            builder.Configuration["ExamServer:ServerId"]=identity.ServerId;builder.Configuration["ExamServer:NodeId"]=identity.NodeId;builder.Configuration["ExamServer:ServerEpoch"]="1";
            builder.Configuration["Database:MaximumPoolSize"]="16";
            builder.Services.AddSingleton(TimeProvider.System);builder.Services.AddSignalR();
            builder.Services.ConfigureHttpJsonOptions(o=>{o.SerializerOptions.PropertyNamingPolicy=Json.PropertyNamingPolicy;foreach(var converter in Json.Converters)o.SerializerOptions.Converters.Add(converter);});
            var source=builder.Services.AddPostgresExamRuntime(builder.Configuration,identity,true);builder.Services.AddSingleton<NpgsqlDataSource>(_=>source);
            // Endpoint integration, not scheduler timing or dual-host durability: prevent background jobs mutating fixture state.
            foreach(var service in builder.Services.Where(s=>s.ServiceType==typeof(IHostedService)&&s.ImplementationType!=typeof(BusinessRecoveryWorker)&&s.ImplementationType?.Namespace?.StartsWith("Exam.",StringComparison.Ordinal)==true).ToArray())builder.Services.Remove(service);
            var app=builder.Build();var host=new Host(app,actor,token);
            app.UseMiddleware<ApiExceptionMiddleware>();app.UseMiddleware<ExamApiAccessMiddleware>();
            app.Use(async(context,next)=>
            {
                if(context.Request.Method=="POST")
                {context.Request.EnableBuffering();var body=await new StreamReader(context.Request.Body,leaveOpen:true).ReadToEndAsync();context.Request.Body.Position=0;host.Mutations.Enqueue((context.Request.Path,body));}
                if(context.Request.Path!=host.FailAfterCommitPath){await next(context);return;}
                host.FailAfterCommitPath=null;var output=context.Response.Body;await using var buffer=new MemoryStream();context.Response.Body=buffer;
                try{await next(context);Assert.InRange(context.Response.StatusCode,200,299);}
                finally{context.Response.Body=output;}
                context.Response.Clear();context.Response.StatusCode=503;await context.Response.WriteAsync("synthetic lost response after durable operation");
            });
            app.MapGroup("/api/v1").MapActivationApi();app.MapGroup("/api/v1").MapParticipationApi();app.MapGroup("/api/v1").MapReliableCommandApi();
            app.MapGroup("/api/v1").MapVersioningApi();app.MapGroup("/api/v1").MapOperatorAccountApi();app.MapGroup("/api/v1").MapExamCatalogApi();app.MapGroup("/api/v1").MapAdministrationApi();app.MapGroup("/api/v1").MapSubmissionApi();
            try
            {
                await app.StartAsync();
                using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var guard = app.Services.GetRequiredService<BusinessRecoveryGuard>();
                try { while (!guard.Snapshot.Ready) await Task.Delay(20, ready.Token); }
                catch (OperationCanceledException) when (ready.IsCancellationRequested)
                { throw new TimeoutException("Synthetic CLI startup checks: " + JsonSerializer.Serialize(guard.Snapshot, Json)); }
                return host;
            }
            catch{await app.DisposeAsync();throw;}
        }
        public ValueTask DisposeAsync()=>app.DisposeAsync();
    }
}
