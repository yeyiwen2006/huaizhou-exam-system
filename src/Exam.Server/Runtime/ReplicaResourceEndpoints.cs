using Exam.Contracts.Common;
using Exam.Infrastructure.HighAvailability;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

namespace Exam.Server.Runtime;

public static class ReplicaResourceEndpoints
{
    public static void MapReplicaResources(this WebApplication app)
    {
        app.MapGet("/replication/v1/authority", async (HttpContext context, IConfiguration configuration) =>
        {
            if (!Authenticate(context, configuration)) { context.Response.StatusCode = 401; return; }
            try { await context.Response.WriteAsJsonAsync(await ReadStateAsync(context, configuration, false), JsonDefaults.Create(), context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception) { context.Response.StatusCode = 503; }
        });
        app.MapGet("/replication/v1/resources/{kind}", async (string kind, HttpContext context, IConfiguration configuration) =>
        {
            if (kind is not ("source" or "snapshot")) { context.Response.StatusCode = 404; return; }
            if (!Authenticate(context, configuration)) { context.Response.StatusCode = 401; return; }
            string? archive = null;
            try
            {
                var state = await ReadStateAsync(context, configuration, true);
                VerifySource(context, state);
                if (!await context.RequestServices.GetRequiredService<NodeAuthorityGuard>().CheckAsync(context.RequestAborted))
                    throw new IOException("旧节点禁止提供权威重建资源。");
                if (kind == "source")
                {
                    await context.Response.WriteAsJsonAsync(state, JsonDefaults.Create(), context.RequestAborted);
                    return;
                }
                if (context.Request.Headers["X-Exam-Resources-Digest"] != state.ResourcesDigest)
                    throw new IOException("重建计划中的资源已改变，请查询原请求后重新计划。");
                var directory = Exam.Infrastructure.ServiceManagement.FileRecoveryDeploymentSelection.ResolveResources(
                    k => configuration[k], state.ServerId, state.NodeId, state.Epoch)
                    ?? throw new IOException("本机资源目录未配置。");
                context.Response.ContentType = "application/zip";
                context.Response.Headers["X-Exam-Resources-Digest"] = state.ResourcesDigest;
                await context.Response.StartAsync(context.RequestAborted);
                archive = await ReplicaResourceTransfer.CaptureAsync(directory, state.ResourcesDigest, context.RequestAborted);
                VerifySource(context, await ReadStateAsync(context, configuration, false));
                await using var input = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);
                await input.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception) { if (!context.Response.HasStarted) context.Response.StatusCode = 503; else context.Abort(); }
            finally { if (archive is not null && File.Exists(archive)) File.Delete(archive); }
        });
        app.MapMethods("/replication/v1/resources", ["GET", "POST"], async (HttpContext context, IConfiguration configuration) =>
        {
            if (!Authenticate(context, configuration)) { context.Response.StatusCode = 401; return; }
            try
            {
                var state = await ReadStateAsync(context, configuration, context.Request.Method == "GET");
                VerifyReceiver(context, state);
                if (context.Request.Method == "GET")
                {
                    await context.Response.WriteAsJsonAsync(state, JsonDefaults.Create(), context.RequestAborted);
                    return;
                }
                if (context.Request.ContentType != "application/zip" || context.Request.ContentLength > ReplicaResourceTransfer.MaximumBytes)
                { context.Response.StatusCode = 413; return; }
                if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                    limit.MaxRequestBodySize = ReplicaResourceTransfer.MaximumBytes;
                var identity = configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>() ?? new();
                var directory = Exam.Infrastructure.ServiceManagement.FileRecoveryDeploymentSelection.ResolveResources(
                    k => configuration[k], identity.ServerId, identity.NodeId, identity.ServerEpoch)
                    ?? throw new IOException("本机复制资源目录未配置。");
                using var guard = new FileConfirmationStore(configuration["Confirmation:Directory"] ??
                    throw new IOException("本机确认目录未配置。")).Lock();
                await ReplicaResourceTransfer.ReceiveAsync(context.Request.Body, directory,
                    context.Request.Headers["X-Exam-Resources-Digest"].ToString(),
                    async token => VerifyReceiver(context, await ReadStateAsync(context, configuration, false)),
                    context.RequestAborted);
                context.Response.StatusCode = 204;
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception) { context.Response.StatusCode = 503; }
        });
    }

    internal static bool Authenticate(HttpContext context, IConfiguration configuration)
    {
        var identity = configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>() ?? new();
        return context.Request.IsHttps && Exam.Application.Security.OperatorIdentityValidator.Authenticate(
            context.Request.Headers.Authorization.ToString(), configuration["Confirmation:PeerToken"], "replication-peer") &&
            context.Request.Headers["X-Exam-Server-Id"] == identity.ServerId &&
            context.Request.Headers["X-Exam-Node-Id"] == identity.NodeId;
    }

    private static void VerifySource(HttpContext context, ReplicaResourceState state)
    {
        if (state.InRecovery || state.ActiveNodeId != state.NodeId ||
            context.Request.Headers["X-Exam-Primary-Node-Id"] != state.ActiveNodeId ||
            context.Request.Headers["X-Exam-Epoch"] != state.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            context.Request.Headers["X-Exam-System-Identifier"] != state.SystemIdentifier)
            throw new IOException("资源源节点不是本次重建的活动主节点。");
    }

    private static void VerifyReceiver(HttpContext context, ReplicaResourceState state)
    {
        if (!state.InRecovery || state.ActiveNodeId == state.NodeId ||
            context.Request.Headers["X-Exam-Primary-Node-Id"] != state.ActiveNodeId ||
            context.Request.Headers["X-Exam-Epoch"] != state.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
            context.Request.Headers["X-Exam-System-Identifier"] != state.SystemIdentifier)
            throw new IOException("当前节点不是本次复制的有效备用接收方。");
    }

    private static async Task<ReplicaResourceState> ReadStateAsync(HttpContext context, IConfiguration configuration, bool resources)
    {
        var identity = configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>() ?? new();
        var source = context.RequestServices.GetRequiredService<NpgsqlDataSource>();
        await using var command = source.CreateCommand("""
            SELECT s.active_node_id,s.server_epoch,c.system_identifier::text,pg_is_in_recovery()
            FROM exam.server_state s CROSS JOIN pg_control_system() c WHERE s.server_id=$1
            """);
        command.Parameters.AddWithValue(identity.ServerId);
        await using var row = await command.ExecuteReaderAsync(context.RequestAborted);
        if (!await row.ReadAsync(context.RequestAborted)) throw new IOException("权威节点身份尚未建立。");
        var digest = resources ? context.RequestServices.GetRequiredService<PostgresStandbyConfirmation>().ReadResourcesDigest() : "";
        return new(identity.ServerId, identity.NodeId, row.GetString(0), row.GetInt64(1), row.GetString(2), row.GetBoolean(3), digest);
    }
}
