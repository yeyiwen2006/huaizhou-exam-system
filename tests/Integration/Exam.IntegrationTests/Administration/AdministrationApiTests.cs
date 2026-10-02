using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Exam.Application.Activation;
using Exam.Application.Security;
using Exam.Contracts.Administration;
using Exam.Contracts.Commands;
using Exam.Contracts.Common;
using Exam.Infrastructure.Postgres.Administration;
using Exam.Infrastructure.Postgres.Resources;
using Exam.Infrastructure.Postgres.Versioning;
using Exam.Infrastructure.Security;
using Exam.IntegrationTests.Postgres;
using Exam.Server.Api;
using Exam.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Exam.IntegrationTests.Administration;

[Collection(PostgresCollection.Name)]
[Trait("Category","Postgres")]
public sealed class AdministrationApiTests(PostgresDatabaseFixture database)
{
    private static readonly System.Text.Json.JsonSerializerOptions Json=JsonDefaults.Create();
    [Fact]
    public async Task DeviceAndAnonymousClients_CannotReadOperatorRosterMonitoringOrRoomData()
    {
        var exam=CreateExam();await using var app=await CreateApp();using var client=app.GetTestClient();
        var paths=new[]{"/rooms","/devices",$"/exams/{exam}/roster",$"/exams/{exam}/monitoring",$"/exams/{exam}/uncollected","/devices/20261002001/room-history"};
        foreach(var path in paths)Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/v1"+path)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Exam-Device-Id","synthetic-device");client.DefaultRequestHeaders.Add("X-Exam-Device-Credential","synthetic-device-token");
        foreach(var path in paths)Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/v1"+path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsync($"/api/v1/exams/{exam}/publications/upload",new MultipartFormDataContent())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.GetAsync("/api/v1/publications/recipients")).StatusCode);
    }
    [Fact]
    public async Task OrdinaryOperator_UsesVersionedRosterApi_WithoutSpoofingAuditActor()
    {
        var exam=CreateExam();await using var app=await CreateApp();using var client=app.GetTestClient();
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer","synthetic-operator-token");
        var empty=await client.GetFromJsonAsync<ApiEnvelope<CandidateRosterSnapshot>>($"/api/v1/exams/{exam}/roster",Json);
        Assert.NotNull(empty!.Result);Assert.Empty(empty.Result.Entries);Assert.Equal(0,empty.Result.Version);
        var request=new ImportCandidateRosterRequest(Guid.NewGuid().ToString("N"),0,[new(1,"API合成考生","001","000001")]);
        var saved=await client.PostAsJsonAsync($"/api/v1/exams/{exam}/roster/import",request,Json);
        Assert.Equal(HttpStatusCode.OK,saved.StatusCode);var envelope=await saved.Content.ReadFromJsonAsync<ApiEnvelope<RosterImportResult>>(Json);
        Assert.True(envelope!.Result!.Applied);Assert.Equal("000001",Assert.Single(envelope.Result.Snapshot.Entries).AdmissionNo);
        var replay=await client.PostAsJsonAsync($"/api/v1/exams/{exam}/roster/import",request,Json);Assert.Equal(HttpStatusCode.OK,replay.StatusCode);
        var stale=await client.PostAsJsonAsync($"/api/v1/exams/{exam}/roster/import",request with{RequestId=Guid.NewGuid().ToString("N")},Json);Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
        using var spoof=new StringContent("{\"requestId\":\"synthetic-spoof\",\"name\":\"无权伪造操作者\",\"operator\":\"another-person\"}",Encoding.UTF8,"application/json");
        Assert.Equal(HttpStatusCode.Forbidden,(await client.PostAsync("/api/v1/rooms",spoof)).StatusCode);
        await using var audit=database.DataSource.CreateCommand("SELECT actor_id FROM exam.administration_requests WHERE request_id=$1;");audit.Parameters.AddWithValue(request.RequestId);
        Assert.Equal("synthetic-operator",await audit.ExecuteScalarAsync());
        var missing=await client.GetFromJsonAsync<ApiEnvelope<IReadOnlyList<MonitoringParticipant>>>($"/api/v1/exams/{exam}/uncollected",Json);
        Assert.Empty(missing!.Result!);
    }
    [Fact]
    public async Task PresenceRequiresDeviceCredential_AndOperatorTokenCannotImpersonateADevice()
    {
        await using var app=await CreateApp();using var client=app.GetTestClient();
        var body=new OpenDevicePresenceRequest(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync("/api/v1/student/presence/open",body,Json)).StatusCode);
        client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer","synthetic-operator-token");
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync("/api/v1/student/presence/open",body,Json)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Exam-Device-Id","synthetic-device");client.DefaultRequestHeaders.Add("X-Exam-Device-Credential","wrong-synthetic-token");
        Assert.Equal(HttpStatusCode.Unauthorized,(await client.PostAsJsonAsync("/api/v1/student/presence/open",body,Json)).StatusCode);
    }
    private string CreateExam()
    {
        var exam="admin-api-"+Guid.NewGuid().ToString("N");var now=DateTimeOffset.UtcNow;
        Assert.True(new PostgresExamVersioningService(database.DataSource).CreateExam(new(exam,new(now.AddYears(80),now.AddYears(80).AddHours(2)),now,Guid.NewGuid().ToString("N"))).Succeeded);
        return exam;
    }
    private async Task<WebApplication> CreateApp()
    {
        var builder=WebApplication.CreateBuilder();builder.WebHost.UseTestServer();
        var identity=new ServerNodeEnvelope("synthetic-administration-api","synthetic-node",1);
        builder.Configuration["ExamServer:ServerId"]=identity.ServerId;builder.Configuration["ExamServer:NodeId"]=identity.NodeId;builder.Configuration["ExamServer:ServerEpoch"]="1";
        builder.Services.ConfigureHttpJsonOptions(o=>{foreach(var converter in Json.Converters)o.SerializerOptions.Converters.Add(converter);});
        builder.Services.AddSingleton(database.DataSource);builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton(new ActiveNodeGuard(database.DataSource,new(){ServerId=identity.ServerId,NodeId=identity.NodeId,ServerEpoch=1},true));
        builder.Services.AddSingleton<IOperatorAuthenticator,TestOperatorAuthenticator>();builder.Services.AddSingleton<IDeviceCredentialValidator,TestDeviceValidator>();
        builder.Services.AddSingleton<PostgresCandidateRosterService>();builder.Services.AddSingleton<PostgresRoomService>();builder.Services.AddSingleton<PostgresDeviceDirectory>();
        builder.Services.AddSingleton<PostgresDevicePresenceService>();builder.Services.AddSingleton<PostgresMonitoringService>();builder.Services.AddSingleton<PostgresExamVersioningService>();
        builder.Services.AddSingleton<PublicationUploadGate>();
        builder.Services.AddSingleton(ManagedNodeEncryptionPolicy.None);builder.Services.AddSingleton<PostgresVersionedResourceStore>();builder.Services.AddSingleton<IPackageKeyUnwrapper,RejectingUnwrapper>();builder.Services.AddSingleton<PostgresBundlePublicationService>();
        var app=builder.Build();app.UseMiddleware<ApiExceptionMiddleware>();app.UseMiddleware<ExamApiAccessMiddleware>();app.MapGroup("/api/v1").MapAdministrationApi();await app.StartAsync();return app;
    }
    private sealed class TestOperatorAuthenticator:IOperatorAuthenticator
    {public ValueTask<AuthenticatedOperator?> AuthenticateAsync(string token,CancellationToken ct=default)=>ValueTask.FromResult<AuthenticatedOperator?>(token=="synthetic-operator-token"?new("synthetic-operator",false):null);}
    private sealed class TestDeviceValidator:IDeviceCredentialValidator
    {public ValueTask<bool> IsActiveDeviceAsync(string id,CancellationToken ct=default)=>ValueTask.FromResult(id=="synthetic-device");
     public ValueTask<bool> ValidateActiveDeviceAsync(string id,string credential,CancellationToken ct=default)=>ValueTask.FromResult(id=="synthetic-device"&&credential=="synthetic-device-token");}
    private sealed class RejectingUnwrapper:IPackageKeyUnwrapper
    {public byte[] Unwrap(EncryptedPackageEnvelope envelope)=>throw new CryptographicException("Synthetic API fixture has no private key.");}
}
