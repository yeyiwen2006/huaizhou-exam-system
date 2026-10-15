using Exam.Application.HighAvailability;
using Exam.Contracts.Common;
using Exam.Contracts.HighAvailability;
using Exam.Domain.Common;
using Exam.Infrastructure.HighAvailability;
using Exam.Infrastructure.Postgres.Resources;
using Microsoft.AspNetCore.SignalR;
using Npgsql;

namespace Exam.Server.Runtime;

/// <summary>A display-only query; it does not acknowledge a write or release exam resources.</summary>
public sealed class ReadOnlyObservation;

/// <summary>Ephemeral device presence, not an answer, receipt, authorization or exam setting.</summary>
public sealed class TransientPresenceUpdate;

/// <summary>Holds business success bytes until the independent durable confirmation completes.</summary>
public sealed class BusinessConfirmationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IBusinessConfirmation confirmation)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1") ||
            context.GetEndpoint()?.Metadata.GetMetadata<ConfirmationBeforeResult>() is not null ||
            (HttpMethods.IsGet(context.Request.Method) &&
             context.GetEndpoint()?.Metadata.GetMetadata<ReadOnlyObservation>() is not null) ||
            (HttpMethods.IsPost(context.Request.Method) &&
             context.GetEndpoint()?.Metadata.GetMetadata<TransientPresenceUpdate>() is not null))
        { await next(context); return; }
        var transport = context.Response.Body;
        await using var buffered = new MemoryStream();
        context.Response.Body = buffered;
        try
        {
            await next(context);
            if (context.Response.StatusCode is >= 200 and < 300)
                await confirmation.ConfirmAsync(context.RequestAborted);
            context.Response.Body = transport;
            buffered.Position = 0; await buffered.CopyToAsync(transport, context.RequestAborted);
        }
        catch { context.Response.Clear(); throw; }
        finally { context.Response.Body = transport; }
    }
}

// Binary packages already occupy memory in their resource result. Confirm after the handler
// has read the package, but before its IResult writes headers/bytes, avoiding a second package copy.
public sealed class ConfirmationBeforeResult : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var result = await next(context);
        if (result is IResult response && result is not IStatusCodeHttpResult { StatusCode: >= 400 })
            return new ConfirmedResult(response);
        return result;
    }
    private sealed class ConfirmedResult(IResult result) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            // Non-PostgreSQL interface fixtures have no confirmation service; the production
            // entry point always registers one and rejects missing protected configuration.
            if (context.RequestServices.GetService<IBusinessConfirmation>() is { } confirmation)
            {
                try { await confirmation.ConfirmAsync(context.RequestAborted); }
                catch { context.Response.Clear(); throw; }
            }
            await result.ExecuteAsync(context);
        }
    }
}

public sealed class ConfirmationHubFilter(IBusinessConfirmation confirmation) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var result = await next(context);
        // No events were released and no acknowledgement was accepted. An empty catch-up
        // is just a connection check; it must not recheck every paper on the standby.
        if (context.Hub is Exam.Server.Realtime.ExamEventHub && context.HubMethodName == "CatchUp" &&
            result is IReadOnlyList<Exam.Contracts.Commands.CommandDelivery> { Count: 0 }) return result;
        try { await confirmation.ConfirmAsync(context.Context.ConnectionAborted); }
        catch (DomainRuleException) { throw new HubException("SERVICE_RECOVERING: 持久确认尚未完成，保留原请求重试。"); }
        return result;
    }
}

public static class BusinessConfirmationRegistration
{
    public static void AddBusinessConfirmation(this IServiceCollection services, IConfiguration configuration,
        ServerIdentityOptions identity, bool isolatedTestRuntime)
    {
        services.AddSingleton(_ => new HttpClient(new HttpClientHandler
        { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton<IBusinessConfirmation>(provider => isolatedTestRuntime
            ? new IsolatedTestConfirmation()
            : new ConfiguredConfirmation(() => new PostgresBusinessConfirmation(provider.GetRequiredService<NpgsqlDataSource>(),
                new FileConfirmationStore(Required("Confirmation:Directory")), identity.ServerId, identity.NodeId,
                Required("Confirmation:StandbyNodeId"), identity.ServerEpoch, Resources(),
                new HttpConfirmationPeer(provider.GetRequiredService<HttpClient>(), new Uri(Required("Confirmation:StandbyHttpsUrl")),
                    identity.ServerId, Required("Confirmation:StandbyNodeId"), () => configuration["Confirmation:PeerToken"]), TimelineConnection(), configuration["Backup:PostgresBinDirectory"]),
                provider.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping));
        services.AddSingleton(provider => new PostgresStandbyConfirmation(provider.GetRequiredService<NpgsqlDataSource>(),
            new FileConfirmationStore(Required("Confirmation:Directory")), identity.ServerId, identity.NodeId,
            Required("Confirmation:PrimaryNodeId"), identity.ServerEpoch, Resources(),
            provider.GetRequiredService<IPackageKeyUnwrapper>(), TimelineConnection(), configuration["Backup:PostgresBinDirectory"]));
        services.AddSignalR(options => options.AddFilter<ConfirmationHubFilter>());
        string Resources() => Exam.Infrastructure.ServiceManagement.FileRecoveryDeploymentSelection.ResolveResources(
            key => configuration[key], identity.ServerId, identity.NodeId, identity.ServerEpoch)
            ?? throw new InvalidOperationException("持久确认缺少受保护资源配置。");
        string? TimelineConnection() => Exam.Infrastructure.ServiceManagement.FileRecoveryDeploymentSelection.ResolveTimelineConnection(
            key => configuration[key], identity.ServerId, identity.NodeId, identity.ServerEpoch);
        string Required(string key) => string.IsNullOrWhiteSpace(configuration[key])
            ? throw new InvalidOperationException("持久确认缺少受保护配置。") : configuration[key]!;
    }

    public static void MapStandbyConfirmation(this WebApplication app)
    {
        app.MapGet("/replication/v1/readiness", async (HttpContext context, IConfiguration configuration) =>
        {
            var identity = configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>() ?? new();
            if (!context.Request.IsHttps || !Exam.Application.Security.OperatorIdentityValidator.Authenticate(
                context.Request.Headers.Authorization.ToString(), configuration["Confirmation:PeerToken"], "replication-peer") ||
                context.Request.Headers["X-Exam-Server-Id"] != identity.ServerId || context.Request.Headers["X-Exam-Node-Id"] != identity.NodeId)
            { context.Response.StatusCode = 401; return; }
            try { await context.Response.WriteAsJsonAsync(await HighAvailabilityDeployment.ReadMaterialsAsync(context, configuration),
                JsonDefaults.Create(), context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception) { context.Response.StatusCode = 503; }
        });
        app.MapMethods("/replication/v1/confirmation", ["GET", "POST"], async (HttpContext context, IConfiguration configuration) =>
        {
            var identity = configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>() ?? new();
            if (!context.Request.IsHttps || !Exam.Application.Security.OperatorIdentityValidator.Authenticate(
                context.Request.Headers.Authorization.ToString(), configuration["Confirmation:PeerToken"], "replication-peer") ||
                context.Request.Headers["X-Exam-Server-Id"] != identity.ServerId ||
                context.Request.Headers["X-Exam-Node-Id"] != identity.NodeId)
            { context.Response.StatusCode = 401; return; }
            try
            {
                var witness = context.RequestServices.GetRequiredService<PostgresStandbyConfirmation>();
                if (context.Request.Method == "GET")
                { await context.Response.WriteAsJsonAsync(witness.Read(), JsonDefaults.Create(), context.RequestAborted); return; }
                if (!context.Request.HasJsonContentType() || context.Request.ContentLength > 16384)
                    throw new IOException("确认请求格式无效。");
                // Bound streamed request bodies as well as Content-Length.
                using var buffer = new MemoryStream(); var bytes = new byte[4096]; int count;
                while ((count = await context.Request.Body.ReadAsync(bytes, context.RequestAborted)) > 0)
                { if (buffer.Length + count > 16384) throw new IOException("确认请求超限。"); buffer.Write(bytes, 0, count); }
                var record = System.Text.Json.JsonSerializer.Deserialize<DurableConfirmation>(buffer.ToArray(), JsonDefaults.Create())
                    ?? throw new IOException("确认请求缺失。");
                await context.Response.WriteAsJsonAsync(await witness.ConfirmAsync(record, context.RequestAborted),
                    JsonDefaults.Create(), context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception) { context.Response.StatusCode = 503; }
        });
    }

    private sealed class ConfiguredConfirmation(Func<PostgresBusinessConfirmation> create, CancellationToken stoppingToken) : IBusinessConfirmation
    {
        private IBusinessConfirmation? _inner;
        private readonly object _gate = new();
        public Task ConfirmAsync(CancellationToken token)
        {
            try { lock (_gate) return (_inner ??= new BatchedBusinessConfirmation(create(), stoppingToken)).ConfirmAsync(token); }
            catch (Exception) { throw new DomainRuleException(ErrorCodes.ServiceRecovering, "持久确认配置不可用。", retryable: true); }
        }
    }
    // Only isolated test hosts explicitly select this registration. The business executable always uses full confirmation.
    private sealed class IsolatedTestConfirmation : IBusinessConfirmation
    { public Task ConfirmAsync(CancellationToken token) => Task.CompletedTask; }
}
