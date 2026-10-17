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
            var ready = mode is nameof(ProtectionMode.SynchronousDualNode) or
                nameof(ProtectionMode.EmergencySingleNode) or nameof(ProtectionMode.Recovering);
            return new("managed-protection", ready, ready
                ? "当前活动节点与服务身份一致；业务可靠落盘后可用，实际复制保护单独显示。"
                : "当前节点或服务代次不具备业务写入权。", true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        { return new("managed-protection", false, "无法核对当前活动节点，业务就绪检查未通过。", true); }
    }
}
