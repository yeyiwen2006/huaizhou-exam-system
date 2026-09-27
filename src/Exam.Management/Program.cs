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
        var builder = CreateHostBuilder(args);
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
            new ConfiguredRecoveryReadinessProbe(configuration.GetConnectionString("Exam"), configuration["PackageSecurity:CertificateThumbprint"] ?? "")
        ]);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
        builder.Services.AddSingleton(new ManagementRuntime(serverId, nodeId, store, process,
            () => new ReadyCheckedBusinessHost(process, http, new Uri(Required("Management:BusinessReadyEndpoint")), serverId, nodeId, epoch),
            preflight, () => backups, journal, new SystemClock(), port,
            token => new ReadyCheckedBusinessHost(process, http, new Uri(Required("Management:BusinessReadyEndpoint")), serverId, nodeId, epoch)
                .ObserveReadyAsync(token)));
        var app = builder.Build();
        app.MapManagementApi();
        await app.RunAsync();

        string Required(string key) => string.IsNullOrWhiteSpace(configuration[key])
            ? throw new InvalidOperationException($"缺少受保护部署配置项 {key}。") : configuration[key]!;
    }

    public static WebApplicationBuilder CreateHostBuilder(string[] args)
    {
        // SCM starts in system32; deployment configuration belongs beside the executable.
        var serviceMode = Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { Args = args, ApplicationName = typeof(ManagementEntryPoint).Assembly.GetName().Name,
            ContentRootPath = serviceMode ? AppContext.BaseDirectory : null });
        if (serviceMode)
        {
            var name = builder.Configuration["Management:ServiceName"];
            if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.IndexOfAny(['/', '\\']) >= 0)
                throw new InvalidOperationException("缺少有效的独立管理 Windows 服务名称。");
            builder.Services.AddWindowsService(options => options.ServiceName = name);
        }
        // Windows lifetime can add an event-log provider; management requests never log credentials or tool output.
        builder.Logging.ClearProviders();
        return builder;
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
