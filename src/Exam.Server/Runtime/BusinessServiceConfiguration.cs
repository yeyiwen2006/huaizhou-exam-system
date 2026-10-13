using System.Net;
using Exam.Contracts.HighAvailability;
using Exam.Contracts.ServiceManagement;
using Exam.Infrastructure.ServiceManagement;
using Exam.Infrastructure.HighAvailability;
using Npgsql;

namespace Exam.Server.Runtime;

/// <summary>Opt-in binding of the managed, non-secret configuration to the actual business host.</summary>
public sealed class BusinessServiceConfiguration
{
    private readonly long _serverEpoch;
    private readonly string? _selectionPath;
    private readonly string? _confirmationRoot;
    public ServiceConfiguration Value { get; }

    private BusinessServiceConfiguration(ServiceConfiguration value, long serverEpoch, string? selectionPath, string? confirmationRoot)
    { Value = value; _serverEpoch = serverEpoch; _selectionPath = selectionPath; _confirmationRoot = confirmationRoot; }

    public static void ApplyDefaultHttpsListener(WebApplicationBuilder builder)
    {
        if (new[] { "urls", "http_ports", "https_ports" }.Any(key => !string.IsNullOrWhiteSpace(builder.Configuration[key])) ||
            builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any()) return;
        // Both remote clients and operators can use a bare domain with the standard HTTPS port.
        // Certificate material stays in the normal protected Kestrel certificate configuration.
        builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(443, endpoint => endpoint.UseHttps()));
    }

    public static async Task<BusinessServiceConfiguration?> LoadAndApplyAsync(WebApplicationBuilder builder,
        CancellationToken cancellationToken = default)
    {
        var path = builder.Configuration["Management:ServiceConfigurationPath"];
        if (path is null) return null; // Existing deployments remain explicitly outside managed configuration.
        var value = await new FileServiceConfigurationStore(path).LoadAsync(cancellationToken);
        var identity = builder.Configuration.GetSection(ServerIdentityOptions.SectionName).Get<ServerIdentityOptions>() ?? new();
        if (!identity.IsValid() || value.ServerId != identity.ServerId || value.NodeId != identity.NodeId)
            throw new InvalidOperationException("持久服务配置与受保护业务节点身份不一致。");
        if (new[] { "urls", "http_ports", "https_ports" }.Any(key => !string.IsNullOrWhiteSpace(builder.Configuration[key])) ||
            builder.Configuration.GetValue("preferHostingUrls", false) ||
            builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new InvalidOperationException("托管业务配置不能与额外 URL、端口或 Kestrel 端点配置同时使用。");
        var localhost = string.Equals(value.BindAddress, "localhost", StringComparison.OrdinalIgnoreCase);
        if (!localhost && !IPAddress.TryParse(value.BindAddress, out _))
            throw new InvalidOperationException("托管业务监听地址必须是 IP 地址或 localhost。");

        var minimumBytes = builder.Configuration.GetValue("Management:MinimumAvailableBytes", 1024L * 1024 * 1024);
        var storage = await new LocalStorageStartupReadinessProbe(minimumBytes).RunAsync(value, cancellationToken);
        if (storage.Count == 0 || storage.Any(check => !check.Passed && check.BlocksWrites))
            throw new InvalidOperationException("托管业务数据目录的路径、空间或持久写入检查失败，业务未启动。");

        builder.WebHost.ConfigureKestrel(options =>
        {
            // UseHttps uses the independently protected Kestrel default certificate; no key is stored in this file.
            if (localhost) options.ListenLocalhost(value.HttpsPort, endpoint => endpoint.UseHttps());
            else options.Listen(IPAddress.Parse(value.BindAddress), value.HttpsPort, endpoint => endpoint.UseHttps());
        });
        var configured = new BusinessServiceConfiguration(value, identity.ServerEpoch, builder.Configuration["HighAvailability:SelectionPath"],
            builder.Configuration[$"HighAvailability:Nodes:{identity.NodeId}:ConfirmationDirectory"]);
        builder.Services.AddSingleton(configured);
        return configured;
    }

    public async Task<StartupCheckResult> CheckProtectionAsync(NpgsqlDataSource source, CancellationToken cancellationToken)
    {
        if (!Value.HighAvailabilityEnabled)
            return new("managed-protection", true, "未附加双机启动要求；现有权威主备及写入权限仍然生效。", true);
        try
        {
            await using var command = source.CreateCommand("""
                SELECT protection_mode FROM exam.server_state
                WHERE server_id = $1 AND active_node_id = $2 AND server_epoch = $3;
                """);
            command.CommandTimeout = 5;
            command.Parameters.AddWithValue(Value.ServerId);
            command.Parameters.AddWithValue(Value.NodeId);
            command.Parameters.AddWithValue(_serverEpoch);
            var mode = await command.ExecuteScalarAsync(cancellationToken) as string;
            var ready = mode == nameof(ProtectionMode.SynchronousDualNode);
            if (!ready && mode == nameof(ProtectionMode.EmergencySingleNode))
            {
                var selection = FileFailoverDeploymentSelection.ReadConfigured(k =>
                    k == "HighAvailability:SelectionPath" ? _selectionPath :
                    k == $"HighAvailability:Nodes:{Value.NodeId}:ConfirmationDirectory" ? _confirmationRoot : null, Value.ServerId, Value.NodeId);
                if (selection is { Mode: ProtectionMode.EmergencySingleNode } && selection.ActiveNodeId == Value.NodeId && selection.Epoch == _serverEpoch)
                {
                    await using var proof = source.CreateCommand("""
                        SELECT EXISTS(SELECT 1 FROM exam.failover_audit a JOIN exam.server_state s USING(server_id)
                          WHERE a.server_id=$1 AND a.node_id=$2 AND a.server_epoch=$3
                            AND a.action IN ($4,$5) AND s.state_version >= $6)
                        """);
                    proof.Parameters.AddWithValue(Value.ServerId); proof.Parameters.AddWithValue(Value.NodeId);
                    proof.Parameters.AddWithValue(_serverEpoch); proof.Parameters.AddWithValue("F02:Takeover:" + selection.RequestId);
                    proof.Parameters.AddWithValue("F02:Emergency:" + selection.RequestId); proof.Parameters.AddWithValue(selection.StateVersion);
                    ready = (bool)(await proof.ExecuteScalarAsync(cancellationToken) ?? false);
                }
            }
            return new("managed-protection", ready, ready ? mode == nameof(ProtectionMode.EmergencySingleNode)
                ? "本次已授权单机应急部署与权威审计相符；业务使用单机持久确认。" : "当前权威状态满足双机保护启动要求。" :
                "当前权威节点、代次或保护模式不满足双机启动要求；不会自动改变主备状态。", true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        { return new("managed-protection", false, "无法核对当前双机保护状态，业务就绪检查未通过。", true); }
    }
}
