using Exam.Contracts.Common;
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

var app = builder.Build();

app.UseMiddleware<ApiExceptionMiddleware>();
app.UseHttpsRedirection();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", (IConfiguration configuration) =>
{
    var identity = configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>()
        ?? new ServerIdentityOptions();
    var valid = identity.IsValid();
    return valid
        ? Results.Ok(new { status = "ready", identity.ServerId, identity.NodeId, identity.ServerEpoch })
        : Results.Json(new { status = "recovering", reason = "服务身份配置不完整。" }, statusCode: 503);
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
app.MapHub<ExamEventHub>("/hubs/exam-events");

app.Run();

public partial class Program;
