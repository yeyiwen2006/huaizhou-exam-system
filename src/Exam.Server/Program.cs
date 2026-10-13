using Exam.Contracts.Common;
using Exam.Server.Api;
using Exam.Server.Realtime;
using Exam.Server.Runtime;
using Exam.Server.Diagnostics;

// SCM starts with system32 as its working directory; service configuration lives beside this executable.
var serviceMode = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{ Args = args, ContentRootPath = serviceMode ? AppContext.BaseDirectory : null });
if (!string.IsNullOrWhiteSpace(builder.Configuration["Management:ProtectedBusinessLogPath"]))
    builder.Logging.AddProvider(new ProtectedRuntimeLogProvider(builder.Configuration["Management:ProtectedBusinessLogPath"]!));
if (serviceMode)
{
    var name = builder.Configuration["ExamServer:WindowsServiceName"];
    if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.IndexOfAny(['/', '\\']) >= 0)
        throw new InvalidOperationException("缺少有效的业务 Windows 服务名称。");
    builder.Services.AddWindowsService(options => options.ServiceName = name);
}
HighAvailabilityDeployment.Apply(builder);
var managedServiceConfiguration = await BusinessServiceConfiguration.LoadAndApplyAsync(builder);
if (managedServiceConfiguration is null)
    BusinessServiceConfiguration.ApplyDefaultHttpsListener(builder);
builder.Services.Configure<ServerIdentityOptions>(builder.Configuration.GetSection(ServerIdentityOptions.SectionName));
builder.Services.AddExamServerTransport();

var identity = builder.Configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>()
    ?? new ServerIdentityOptions();
var studentTopology = StudentFailoverConfiguration.Read(builder.Configuration);
if ((builder.Configuration.GetSection("HighAvailability:RequiredNodeIds").Get<string[]>()?.Length ?? 0) == 2 && studentTopology is null)
    throw new InvalidOperationException("双机部署缺少学生端主备信任配置，不能投入正式考试。");
var databaseConfigured = !string.IsNullOrWhiteSpace(Exam.Infrastructure.ServiceManagement.FileRecoveryDeploymentSelection.ResolveConnection(
    key => builder.Configuration[key], identity.ServerId, identity.NodeId, identity.ServerEpoch));
if (databaseConfigured)
{
    builder.Services.AddPostgresExamRuntime(
        builder.Configuration,
        identity,
        allowUninitializedFailoverState: false);
}
else
{
    throw new InvalidOperationException(
        "缺少 PostgreSQL 连接配置，业务服务已拒绝启动。请在服务管理中核对部署配置。");
}

var app = builder.Build();

if (databaseConfigured && builder.Configuration.GetValue("Database:ApplyMigrations", false))
{
    await app.Services.GetRequiredService<Exam.Infrastructure.Postgres.PostgresMigrationRunner>()
        .MigrateAsync(app.Lifetime.ApplicationStopping);
}

app.UseMiddleware<ApiExceptionMiddleware>();
app.UseHttpsRedirection();
app.UseMiddleware<ExamApiAccessMiddleware>();
app.UseMiddleware<BusinessConfirmationMiddleware>();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (
    IConfiguration configuration,
    CancellationToken cancellationToken) =>
{
    var configuredIdentity = configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>()
        ?? new ServerIdentityOptions();
    if (!configuredIdentity.IsValid())
    {
        return Results.Json(
            new { status = "recovering", reason = "服务身份配置不完整。" },
            statusCode: 503);
    }

    var readiness = await app.Services.GetRequiredService<PostgresReadinessProbe>()
        .CheckAsync(cancellationToken);
    var recovery = app.Services.GetRequiredService<BusinessRecoveryGuard>().Snapshot;
    if (!recovery.Ready)
        return Results.Json(new { status = "recovering", reason = "启动恢复核对尚未通过。" }, statusCode: 503);
    if (managedServiceConfiguration is not null &&
        !(await managedServiceConfiguration.CheckProtectionAsync(app.Services.GetRequiredService<Npgsql.NpgsqlDataSource>(), cancellationToken)).Passed)
        return Results.Json(new { status = "recovering", reason = "当前权威保护状态不满足本机服务配置。" }, statusCode: 503);
    if (readiness.Ready)
    {
        try { await app.Services.GetRequiredService<Exam.Application.HighAvailability.IBusinessConfirmation>().ConfirmAsync(cancellationToken); }
        catch (Exam.Domain.Common.DomainRuleException)
        { return Results.Json(new { status = "recovering", reason = "本机及备节点持久确认尚未完成。" }, statusCode: 503); }
    }
    return readiness.Ready
        ? Results.Ok(new
        {
            status = "ready",
            configuredIdentity.ServerId,
            configuredIdentity.NodeId,
            configuredIdentity.ServerEpoch,
            serviceConfiguration = managedServiceConfiguration?.Value
        })
        : Results.Json(new { status = "recovering", reason = readiness.Reason }, statusCode: 503);
});
app.MapExamApi();
app.MapStandbyConfirmation();
app.MapExamDiagnostics();
app.MapHub<ExamEventHub>("/hubs/exam-events");

app.Run();

public partial class Program;
