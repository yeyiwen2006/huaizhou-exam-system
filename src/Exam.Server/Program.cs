using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Server.Api;
using Exam.Server.Realtime;
using Exam.Server.Runtime;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<ServerIdentityOptions>(builder.Configuration.GetSection(ServerIdentityOptions.SectionName));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSignalR();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    var defaults = JsonDefaults.Create();
    options.SerializerOptions.PropertyNamingPolicy = defaults.PropertyNamingPolicy;
    options.SerializerOptions.PropertyNameCaseInsensitive = defaults.PropertyNameCaseInsensitive;
    foreach (var converter in defaults.Converters)
    {
        options.SerializerOptions.Converters.Add(converter);
    }
});

var identity = builder.Configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>()
    ?? new ServerIdentityOptions();
var databaseConfigured = !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Exam"));
if (databaseConfigured)
{
    builder.Services.AddPostgresExamRuntime(
        builder.Configuration,
        identity,
        allowUninitializedFailoverState: builder.Environment.IsDevelopment());
}
else if (builder.Environment.IsProduction())
{
    throw new InvalidOperationException(
        "生产环境缺少 ConnectionStrings:Exam；为避免把考试数据写入临时内存，服务已拒绝启动。");
}

var app = builder.Build();

if (databaseConfigured && builder.Configuration.GetValue("Database:ApplyMigrations", false))
{
    await app.Services.GetRequiredService<Exam.Infrastructure.Postgres.PostgresMigrationRunner>()
        .MigrateAsync(app.Lifetime.ApplicationStopping);
}

app.UseMiddleware<ApiExceptionMiddleware>();
app.UseHttpsRedirection();
if (databaseConfigured)
{
    app.UseMiddleware<ExamApiAccessMiddleware>();
}

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

    if (!databaseConfigured)
    {
        return Results.Json(
            new { status = "recovering", reason = "PostgreSQL 尚未配置，业务接口未启用。" },
            statusCode: 503);
    }

    var readiness = await app.Services.GetRequiredService<PostgresReadinessProbe>()
        .CheckAsync(cancellationToken);
    return readiness.Ready
        ? Results.Ok(new
        {
            status = "ready",
            configuredIdentity.ServerId,
            configuredIdentity.NodeId,
            configuredIdentity.ServerEpoch
        })
        : Results.Json(new { status = "recovering", reason = readiness.Reason }, statusCode: 503);
});
app.MapGet("/api/system/time", (IConfiguration configuration, TimeProvider timeProvider, HttpContext context) =>
{
    var identity = configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>()
        ?? new ServerIdentityOptions();
    var payload = new ServerTimeResponse(timeProvider.GetUtcNow());
    return Results.Ok(new ApiEnvelope<ServerTimeResponse>(
        identity.ServerId,
        identity.NodeId,
        identity.ServerEpoch,
        context.TraceIdentifier,
        payload,
        null));
});
if (databaseConfigured)
{
    app.MapExamApi();
    app.MapHub<ExamEventHub>("/hubs/exam-events");
    if (builder.Configuration.GetValue("Backup:Enabled", false))
    {
        app.MapPost("/api/v1/service-management/backups", async (
            BackupRequest request,
            IBackupCoordinator backups,
            CancellationToken cancellationToken) =>
            Results.Ok(await backups.CreateAsync(request, cancellationToken)));
        app.MapPost("/api/v1/service-management/restores", async (
            RestoreRequest request,
            IBackupCoordinator backups,
            CancellationToken cancellationToken) =>
        {
            await backups.RestoreAsync(request, cancellationToken);
            return Results.Ok(new
            {
                request.BackupId,
                status = "staged",
                message = "备份已校验并恢复到隔离暂存目录，未覆盖当前数据库。"
            });
        });
    }
}

app.Run();

public partial class Program;
