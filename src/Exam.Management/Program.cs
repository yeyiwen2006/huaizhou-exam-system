using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Exam.Application.ServiceManagement;
using Exam.Contracts.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Domain.Common;
using Exam.Infrastructure.ServiceManagement;

namespace Exam.Management;

public static class ManagementEntryPoint
{
    public static async Task Main(string[] args)
    {
        var builder = CreateHostBuilder(args);
        var configuration = builder.Configuration;
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
        AddManagementRuntime(builder);
        await using var app = builder.Build();
        app.MapManagementApi();
        await app.RunAsync();

        string Required(string key) => string.IsNullOrWhiteSpace(configuration[key])
            ? throw new InvalidOperationException($"缺少受保护部署配置项 {key}。") : configuration[key]!;
    }

    public static void AddManagementRuntime(WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var serverId = Required("Management:ServerId");
        var nodeId = Required("Management:NodeId");
        var port = configuration.GetValue<int>("Management:HttpsPort");
        if (port is < 1 or > 65535) throw new InvalidOperationException("管理 HTTPS 端口无效。");
        builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler
            { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(5) });
        builder.Services.AddSingleton(provider => CreateRuntime(provider.GetRequiredService<HttpClient>()));

        ManagementRuntime CreateRuntime(HttpClient http)
        {
            var process = new DeferredBusinessHost(() => new WindowsServiceBusinessHost(Required("Management:BusinessServiceName")));
            var store = new DeferredConfigurationStore(() => new FileServiceConfigurationStore(Required("Management:ServiceConfigurationPath")));
            var journal = new DeferredOperationStore(() => new FileManagementOperationStore(Required("Management:OperationDirectory")));
            var epoch = long.TryParse(configuration["ExamServer:ServerEpoch"], out var configuredEpoch) ? configuredEpoch : 0;
            string? SelectedConnection() => FileRecoveryDeploymentSelection.ResolveConnection(key => configuration[key], serverId, nodeId, epoch);
            IManagedBusinessHost ReadyHost() => new ReadyCheckedBusinessHost(process, http,
                new Uri(Required("Management:BusinessReadyEndpoint")), serverId, nodeId, epoch);
            FileRecoveryDeploymentSwitch Deployment() => new(new FileRecoveryDeploymentSelection(Required("Recovery:SelectionPath")), epoch,
                async (_, token) => (await new PostgresRecoveryStateComparison(Required("ConnectionStrings:Exam"),
                    Required("ConnectionStrings:ExamRecovery"), epoch).RunAsync(await store.LoadAsync(token), token)).Passed);
            PostgresBackupCoordinator CreateBackups() => new(new PostgresBackupOptions(
                Required("Backup:PostgresBinDirectory"), SelectedConnection() ?? "", Required("Backup:AllowedBackupRoot"),
                Required("Backup:RestoreStagingRoot"), Required("Backup:VersionedResourcesDirectory"), Required("Backup:ProtectedConfigurationDirectory"),
                TimeSpan.FromMinutes(configuration.GetValue("Backup:ToolTimeoutMinutes", 60))), new ExternalProcessRunner());
            var backups = new DeferredBackupCoordinator(CreateBackups);
            var preflight = new CompositeStartupReadinessProbe([
                new LocalStorageStartupReadinessProbe(long.TryParse(configuration["Management:MinimumAvailableBytes"], out var minimumBytes) ? minimumBytes : 1024L * 1024 * 1024),
                new NodeCertificateStartupReadinessProbe(configuration["PackageSecurity:CertificateThumbprint"] ?? ""),
                new DeferredReadinessProbe(() => new ManagementDatabaseStartupProbe(SelectedConnection(), epoch)),
                new DeferredReadinessProbe(() => new ConfiguredRecoveryReadinessProbe(SelectedConnection(), configuration["PackageSecurity:CertificateThumbprint"] ?? "")),
                new BackupIntegrityStartupReadinessProbe(() => new PostgresBackupCoordinator(new PostgresBackupOptions(
                    Required("Backup:PostgresBinDirectory"), "", Required("Backup:AllowedBackupRoot"), "", "", "",
                    TimeSpan.FromMinutes(configuration.GetValue("Backup:ToolTimeoutMinutes", 60))), new ExternalProcessRunner()),
                    configuration["Backup:StartupVerificationDirectory"], configuration["Backup:StartupVerificationBackupId"])
            ]);
            return new ManagementRuntime(serverId, nodeId, store, process,
                ReadyHost,
                preflight, () => backups, journal, new SystemClock(), port,
                token => new ReadyCheckedBusinessHost(process, http, new Uri(Required("Management:BusinessReadyEndpoint")), serverId, nodeId, epoch)
                    .ObserveReadyAsync(token),
                prepareRecoveryWorkingCopy: string.IsNullOrWhiteSpace(configuration["Recovery:WorkingRoot"]) ? null :
                    async (request, token) =>
                    {
                        await CreateBackups().PrepareRecoveryWorkingCopyAsync(request, Required("Recovery:WorkingRoot"), token);
                    },
                verifyRecoveryTarget: string.IsNullOrWhiteSpace(configuration.GetConnectionString("ExamRecovery")) ? null :
                    async (request, serviceConfiguration, token) =>
                    {
                        var working = await CreateBackups().FindPreparedRecoveryWorkingCopyAsync(request, Required("Recovery:WorkingRoot"), token)
                            ?? throw new IOException("恢复工作副本尚未准备。");
                        var checks = (await new PostgresWorkingCopyReadinessProbe(Required("ConnectionStrings:Exam"),
                            Required("ConnectionStrings:ExamRecovery"), configuration["PackageSecurity:CertificateThumbprint"] ?? "", epoch)
                            .RunAsync(serviceConfiguration, working, token)).ToList();
                        if (!string.IsNullOrWhiteSpace(configuration["Recovery:SelectionPath"]))
                        {
                            checks.Add(await new PostgresRecoveryStateComparison(Required("ConnectionStrings:Exam"),
                                Required("ConnectionStrings:ExamRecovery"), epoch).RunAsync(serviceConfiguration, token));
                            checks.AddRange((await preflight.RunAsync(serviceConfiguration, token))
                                .Select(check => check with { Name = "activation-" + check.Name }));
                        }
                        return checks;
                    },
                recoveryWorkingDirectory: string.IsNullOrWhiteSpace(configuration["Recovery:WorkingRoot"]) ? null :
                    () => Path.GetFullPath(Path.Combine(Required("Recovery:WorkingRoot"), "slot")),
                activateRecovery: string.IsNullOrWhiteSpace(configuration["Recovery:SelectionPath"]) ? null :
                    (operation, token) => new RecoveryActivationRunner(process, ReadyHost, Deployment(), journal).ActivateAsync(operation, token),
                observeRecoveryActivation: async (operation, token) =>
                {
                    // Query observes existing evidence only; it never copies, selects, rolls back or starts a service.
                    if (operation.RequestJson is null || operation.Restore is not { RecoveryVerified: true, BusinessReady: false } ||
                        operation.Checks?.Any(c => c.Name == "recovery-confirmed-state" && c.Passed && c.BlocksWrites) != true)
                        return null;
                    var selected = Deployment();
                    if (process.Status == BusinessServiceStatus.Stopped &&
                        operation.RecoveryActivation is RecoveryActivationPhase.RollbackPending or RecoveryActivationPhase.RolledBack &&
                        selected.IsRolledBack(operation))
                        return operation with { Phase = ManagementOperationPhase.Staged, RecoveryActivation = RecoveryActivationPhase.RolledBack, Error = null };
                    if (process.Status != BusinessServiceStatus.Running || !selected.IsSelected(operation)) return null;
                    var request = System.Text.Json.JsonSerializer.Deserialize<RestoreRequest>(operation.RequestJson, JsonDefaults.Create());
                    if (request is null || request.RequestId != operation.RequestId || request.ActorId != operation.ActorId ||
                        request.BackupId != operation.Restore.BackupId) return null;
                    var working = await CreateBackups().FindPreparedRecoveryWorkingCopyAsync(request, Required("Recovery:WorkingRoot"), token);
                    if (working is null || working.WorkingDirectory != operation.RecoveryWorkingDirectory) return null;
                    var checks = await new PostgresWorkingCopyReadinessProbe(Required("ConnectionStrings:Exam"),
                        Required("ConnectionStrings:ExamRecovery"), configuration["PackageSecurity:CertificateThumbprint"] ?? "", epoch)
                        .RunAsync(await store.LoadAsync(token), working, token);
                    if (!checks.Any(c => c.Name == "recovery-target" && c.Passed && c.BlocksWrites) ||
                        checks.Any(c => !c.Passed && c.BlocksWrites) ||
                        !await new ReadyCheckedBusinessHost(process, http, new Uri(Required("Management:BusinessReadyEndpoint")), serverId, nodeId, epoch)
                            .ObserveReadyAsync(token) || process.Status != BusinessServiceStatus.Running || !selected.IsSelected(operation)) return null;
                    return operation with { Phase = ManagementOperationPhase.Completed, RecoveryActivation = RecoveryActivationPhase.BusinessReady,
                        Restore = operation.Restore with { BusinessReady = true }, Error = null };
                });
        }

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
