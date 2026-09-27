using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Exam.Application.ServiceManagement;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Management;

public static class ManagementEntryPoint
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // No framework request/exception logging of credentials, local paths or tool output.
        builder.Logging.ClearProviders();
        var configuration = builder.Configuration;
        var serverId = Required("Management:ServerId");
        var nodeId = Required("Management:NodeId");
        var port = configuration.GetValue<int>("Management:HttpsPort");
        if (port is < 1 or > 65535) throw new InvalidOperationException("管理 HTTPS 端口无效。");
        if (configuration["Management:BindAddress"] is { } address && address != "127.0.0.1" && address != "::1" && address != "localhost")
            throw new InvalidOperationException("本机管理只允许回环监听。");
        if (!string.IsNullOrEmpty(configuration["Urls"]) || configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new InvalidOperationException("管理宿主拒绝额外监听配置，使用 Management:HttpsPort。 ");
        using var certificate = ManagementDeployment.LoadCertificate(Required("Management:CertificateThumbprint"));
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 65536;
            options.Listen(IPAddress.Loopback, port, endpoint => endpoint.UseHttps(certificate));
        });
        var process = new DeferredBusinessHost(() => new WindowsServiceBusinessHost(Required("Management:BusinessServiceName")));
        var store = new DeferredConfigurationStore(() => new FileServiceConfigurationStore(Required("Management:ServiceConfigurationPath")));
        var journal = new DeferredOperationStore(() => new FileManagementOperationStore(Required("Management:OperationDirectory")));
        var backups = new DeferredBackupCoordinator(() => new PostgresBackupCoordinator(new PostgresBackupOptions(
            Required("Backup:PostgresBinDirectory"), Required("ConnectionStrings:Exam"), Required("Backup:AllowedBackupRoot"),
            Required("Backup:RestoreStagingRoot"), Required("Backup:VersionedResourcesDirectory"), Required("Backup:ProtectedConfigurationDirectory"),
            TimeSpan.FromMinutes(configuration.GetValue("Backup:ToolTimeoutMinutes", 60))), new ExternalProcessRunner()));
        var epoch = long.TryParse(configuration["ExamServer:ServerEpoch"], out var configuredEpoch) ? configuredEpoch : 0;
        var preflight = new CompositeStartupReadinessProbe([
            new LocalStorageStartupReadinessProbe(long.TryParse(configuration["Management:MinimumAvailableBytes"], out var minimumBytes) ? minimumBytes : 1024L * 1024 * 1024),
            new NodeCertificateStartupReadinessProbe(configuration["PackageSecurity:CertificateThumbprint"] ?? ""),
            new ManagementDatabaseStartupProbe(configuration.GetConnectionString("Exam"), epoch),
            new RecoveryBoundaryProbe()
        ]);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
        builder.Services.AddSingleton(new ManagementRuntime(serverId, nodeId, store, process,
            () => new ReadyCheckedBusinessHost(process, http, new Uri(Required("Management:BusinessReadyEndpoint")), serverId, nodeId, epoch),
            preflight, () => backups, journal, new SystemClock(), port));
        var app = builder.Build();
        app.MapManagementApi();
        await app.RunAsync();

        string Required(string key) => string.IsNullOrWhiteSpace(configuration[key])
            ? throw new InvalidOperationException($"缺少受保护部署配置项 {key}。") : configuration[key]!;
    }
}

public static class ManagementDeployment
{
    public static X509Certificate2 LoadCertificate(string thumbprint)
    {
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            var certificates = store.Certificates;
            X509Certificate2? selected = null;
            try
            {
                var matches = certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
                if (matches.Count != 1) throw new InvalidOperationException("管理 HTTPS 证书不存在或不唯一。");
                selected = matches[0];
                if (!selected.HasPrivateKey || DateTime.UtcNow < selected.NotBefore.ToUniversalTime() || DateTime.UtcNow > selected.NotAfter.ToUniversalTime())
                    throw new InvalidOperationException("管理 HTTPS 证书或私钥不可用。");
                return selected;
            }
            finally { foreach (var item in certificates) if (!ReferenceEquals(item, selected)) item.Dispose(); }
        }
        catch (Exception error) when (error is CryptographicException or PlatformNotSupportedException or UnauthorizedAccessException)
        { throw new InvalidOperationException("无法加载管理 HTTPS 证书及私钥。"); }
    }
}

// Published resources, original receipts, reliable events and timer recovery need a complete verification chain.
// A local directory/database connectivity check must not be advertised as that chain.
internal sealed class RecoveryBoundaryProbe : IStartupReadinessProbe
{
    public Task<IReadOnlyList<StartupCheckResult>> RunAsync(ServiceConfiguration configuration, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StartupCheckResult>>([new("recovery-verification", false,
            "题包、原回执、可靠事件与定时恢复核对链尚未接齐，业务启动保持关闭。", true)]);
}
