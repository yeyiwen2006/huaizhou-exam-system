using System.Net;
using System.Security.Cryptography.X509Certificates;
using Exam.Application.ServiceManagement;
using Exam.Application.HighAvailability;
using Exam.Contracts.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Domain.Common;
using Exam.Infrastructure.ServiceManagement;
using Exam.Infrastructure.HighAvailability;
using Exam.Infrastructure.Security;

namespace Exam.Management;

public static class ManagementEntryPoint
{
    public static async Task Main(string[] args)
    {
        var builder = CreateHostBuilder(args);
        var configuration = builder.Configuration;
        var port = configuration.GetValue<int>("Management:HttpsPort");
        if (port is < 1 or > 65535) throw new InvalidOperationException("管理 HTTPS 端口无效。");
        var bindAddress = ManagementDeployment.ParseBindAddress(configuration["Management:BindAddress"]);
        if (new[] { "urls", "http_ports", "https_ports" }.Any(key => !string.IsNullOrWhiteSpace(configuration[key])) ||
            configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
            throw new InvalidOperationException("管理宿主拒绝额外监听配置，使用 Management:BindAddress 和 Management:HttpsPort。");
        using var certificate = ManagementDeployment.LoadCertificate(new ServerCertificateSource(configuration["Management:CertificateThumbprint"],
            configuration["Management:CertificatePath"], configuration["Management:CertificatePassword"]));
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 65536;
            options.Listen(bindAddress, port, endpoint => endpoint.UseHttps(certificate));
        });
        AddManagementRuntime(builder);
        await using var app = builder.Build();
        app.MapManagementApi();
        await app.RunAsync();
    }

    public static void AddManagementRuntime(WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var serverId = Required("Management:ServerId");
        var nodeId = Required("Management:NodeId");
        // The independent management host remains running across role changes. Resolve the same
        // protected selection as business startup on each operation, without restarting management.
        string? SelectedConfiguration(string key)
        {
            var selected = FileFailoverDeploymentSelection.ReadConfigurationOverrides(k => configuration[k], serverId, nodeId);
            return selected.TryGetValue(key, out var value) ? value : configuration[key];
        }
        string SelectedRequired(string key) => SelectedConfiguration(key) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"缺少受保护部署配置项 {key}。");
        long CurrentEpoch() => long.TryParse(SelectedConfiguration("ExamServer:ServerEpoch"), out var epoch) ? epoch : 0;
        var port = configuration.GetValue<int>("Management:HttpsPort");
        if (port is < 1 or > 65535) throw new InvalidOperationException("管理 HTTPS 端口无效。");
        builder.Services.AddSingleton(_ => new HttpClient(new HttpClientHandler
            { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) });
        builder.Services.AddSingleton<DurableFailoverCoordinator>(provider =>
        {
            var nodes = configuration.GetSection("HighAvailability:Nodes").GetChildren().ToDictionary(section => section.Key,
                section => new FailoverNodeDeployment(section.Key, Required(section.Path + ":ConnectionString"),
                    Required(section.Path + ":ResourcesDirectory"), Required(section.Path + ":CertificateThumbprint"),
                    Required(section.Path + ":ConfirmationDirectory"), Required(section.Path + ":BusinessHttpsUrl"))
                    { ReplicationConnectionString = section["ReplicationConnectionString"] }, StringComparer.Ordinal);
            var options = new HighAvailabilityManagementOptions(serverId, nodeId, nodes,
                FileFailoverDeploymentSelection.ResolvePath(k => configuration[k], nodeId) ?? throw new IOException("主备部署选择路径未配置。"),
                configuration["HighAvailability:RebuildRoot"] ?? "", configuration["Backup:PostgresBinDirectory"] ?? "",
                configuration["HighAvailability:PostgresDataDirectory"] ?? "", configuration["HighAvailability:PostgresServiceName"] ?? "",
                Required("Management:BusinessServiceName"), configuration["Confirmation:PeerToken"] ?? "", configuration["HighAvailability:PostgresConfigurationPath"] ?? "",
                configuration["HighAvailability:WalArchiveDirectory"] ?? "", configuration["HighAvailability:WalArchiveScriptsDirectory"] ?? "")
            {
                WalArchiveTimeoutSeconds = configuration.GetValue("HighAvailability:WalArchiveTimeoutSeconds", 60),
                PostgresHbaPath = configuration["HighAvailability:PostgresHbaPath"] ?? ""
            };
            var journal = new FileManagementOperationStore(Required("Management:OperationDirectory"));
            return new(serverId, nodeId, new PostgresHighAvailabilityManagementPlatform(options,
                provider.GetRequiredService<HttpClient>(), localMaterials: new RunningBusinessMaterialProbe(
                    provider.GetRequiredService<HttpClient>(), new Uri(nodes[nodeId].BusinessHttpsUrl),
                    serverId, nodeId, CurrentEpoch, () => configuration["Confirmation:PeerToken"] ?? ""),
                recordProbeFailure: (observedNode, error) =>
                {
                    var kind = error is TimeoutException or Npgsql.NpgsqlException { InnerException: TimeoutException } ? "Timeout" :
                        error is Npgsql.PostgresException { SqlState: "28P01" or "28000" } ? "AuthenticationFailed" : "Unavailable";
                    new ProtectedRuntimeLogWriter(Path.Combine(Required("Management:OperationDirectory"), "management-runtime.jsonl"), "management")
                        .Write("Exam.Management.Database." + (observedNode == nodeId ? "Local." : "Peer.") + kind, "Error", 7002);
                }, readSelection: () => FileFailoverDeploymentSelection.ReadConfigured(k => configuration[k], serverId, nodeId)),
                journal, new SystemClock());
        });
        builder.Services.AddSingleton(provider => CreateRuntime(provider.GetRequiredService<HttpClient>(),
            () => provider.GetRequiredService<DurableFailoverCoordinator>()));
        // 考务账号登录：以数据库为准，数据库不可用时按本机加密缓存核对。
        builder.Services.AddSingleton(_ => new ManagementOperatorSessions(
            () => FileRecoveryDeploymentSelection.ResolveConnection(SelectedConfiguration, serverId, nodeId, CurrentEpoch()),
            configuration["Management:OperatorCachePath"] ?? Path.Combine(Required("Management:OperationDirectory"), "operators.cache")));
        builder.Services.AddHostedService(_ => new AutomaticBackupWorker(configuration, SelectedConfiguration));

        ManagementRuntime CreateRuntime(HttpClient http, Func<DurableFailoverCoordinator> failover)
        {
            var managementLog = Path.Combine(Required("Management:OperationDirectory"), "management-runtime.jsonl");
            async Task<IReadOnlyList<ProtectedRuntimeLogEntry>> RuntimeLogs(int limit, CancellationToken token)
            {
                var entries = new List<ProtectedRuntimeLogEntry>();
                foreach (var (path, source) in new[] { (Required("Management:ProtectedBusinessLogPath"), "business"), (managementLog, "management") })
                    try { entries.AddRange(await ProtectedRuntimeLogReader.ReadAsync(path, source, limit, token)); }
                    catch (FileNotFoundException) { }
                if (entries.Count == 0) throw new FileNotFoundException("受保护日志尚不可用。");
                return entries.OrderByDescending(entry => entry.Timestamp).Take(limit).ToArray();
            }
            var process = new DeferredBusinessHost(() => OperatingSystem.IsLinux()
                ? new SystemdBusinessHost(Required("Management:BusinessServiceName"))
                : new WindowsServiceBusinessHost(Required("Management:BusinessServiceName")));
            var store = new DeferredConfigurationStore(() => new FileServiceConfigurationStore(Required("Management:ServiceConfigurationPath")));
            var journal = new DeferredOperationStore(() => new FileManagementOperationStore(Required("Management:OperationDirectory")));
            string? SelectedConnection() => FileRecoveryDeploymentSelection.ResolveConnection(SelectedConfiguration, serverId, nodeId, CurrentEpoch());
            ReadyCheckedBusinessHost ReadyHost(ServiceConfiguration expected) => new(process, http,
                new Uri(Required("Management:BusinessReadyEndpoint")), serverId, nodeId, CurrentEpoch(), expectedConfiguration: expected);
            FileRecoveryDeploymentSwitch Deployment() => new(new FileRecoveryDeploymentSelection(Required("Recovery:SelectionPath")), CurrentEpoch(),
                async (_, token) => (await new PostgresRecoveryStateComparison(SelectedRequired("ConnectionStrings:Exam"),
                    Required("ConnectionStrings:ExamRecovery"), CurrentEpoch()).RunAsync(await store.LoadAsync(token), token)).Passed,
                ArchiveReplay() is not null);
            PostgresBackupCoordinator CreateBackups() => new(new PostgresBackupOptions(
                Required("Backup:PostgresBinDirectory"), SelectedConnection() ?? "", Required("Backup:AllowedBackupRoot"),
                Required("Backup:RestoreStagingRoot"),
                FileRecoveryDeploymentSelection.ResolveResources(SelectedConfiguration, serverId, nodeId, CurrentEpoch())
                    ?? throw new InvalidOperationException("备份资源未配置。"), Required("Backup:ProtectedConfigurationDirectory"),
                TimeSpan.FromMinutes(configuration.GetValue("Backup:ToolTimeoutMinutes", 60))), new ExternalProcessRunner());
            PostgresArchiveRecovery? ArchiveReplay()
            {
                var options = PostgresArchiveRecoveryOptions.ReadConfigured(key => configuration[key]);
                if (options is null) return null;
                if (string.IsNullOrWhiteSpace(configuration["Recovery:PostgresServiceName"]))
                    throw new InvalidOperationException("归档重放必须使用已部署的受控隔离数据库服务。");
                return new(options);
            }
            var backups = new DeferredBackupCoordinator(CreateBackups);
            IStartupReadinessProbe Preflight(string? target = null) => new CompositeStartupReadinessProbe([
                new LocalStorageStartupReadinessProbe(long.TryParse(configuration["Management:MinimumAvailableBytes"], out var minimumBytes) ? minimumBytes : 1024L * 1024 * 1024),
                new DeferredReadinessProbe(() => new NodeCertificateStartupReadinessProbe(NodeCertificate())),
                new DeferredReadinessProbe(() => new ManagementDatabaseStartupProbe(target ?? SelectedConnection(), CurrentEpoch())),
                new DeferredReadinessProbe(() => new ConfiguredRecoveryReadinessProbe(target ?? SelectedConnection(), NodeCertificate())),
                new BackupIntegrityStartupReadinessProbe(() => new PostgresBackupCoordinator(new PostgresBackupOptions(
                    Required("Backup:PostgresBinDirectory"), "", Required("Backup:AllowedBackupRoot"), "", "", "",
                    TimeSpan.FromMinutes(configuration.GetValue("Backup:ToolTimeoutMinutes", 60))), new ExternalProcessRunner()),
                    configuration["Backup:StartupVerificationDirectory"], configuration["Backup:StartupVerificationBackupId"])
            ]);
            ServerCertificateSource NodeCertificate() => new(SelectedConfiguration("PackageSecurity:CertificateThumbprint"),
                SelectedConfiguration("PackageSecurity:CertificatePath"), SelectedConfiguration("PackageSecurity:CertificatePassword"));
            var preflight = Preflight();
            async Task<StartupCheckResult> ConfirmedState(ServiceConfiguration expected, RecoveryWorkingCopy working, CancellationToken token)
            {
                var original = await new PostgresRecoveryStateComparison(SelectedRequired("ConnectionStrings:Exam"),
                    Required("ConnectionStrings:ExamRecovery"), CurrentEpoch()).ObserveAsync(expected, token);
                if (original.Status != RecoveryComparisonStatus.OriginalUnavailable ||
                    string.IsNullOrWhiteSpace(SelectedConfiguration("Confirmation:Directory"))) return original.Check;
                try
                {
                    return await new PostgresConfirmedRecoveryEvidence(new FileConfirmationStore(SelectedRequired("Confirmation:Directory")),
                        new HttpConfirmationPeer(http, new Uri(SelectedRequired("Confirmation:StandbyHttpsUrl")), serverId,
                            SelectedRequired("Confirmation:StandbyNodeId"), () => configuration["Confirmation:PeerToken"]),
                        SelectedRequired("ConnectionStrings:ExamStandby"), Required("ConnectionStrings:ExamRecovery"), CurrentEpoch(),
                        configuration.GetConnectionString("ExamRecoveryTimeline") ?? Required("ConnectionStrings:ExamRecovery"),
                        configuration["Backup:PostgresBinDirectory"], SelectedConfiguration("ConnectionStrings:ExamStandbyTimeline"))
                        .RunAsync(expected, Path.Combine(working.WorkingDirectory, "resources"), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { return original.Check; }
            }
            return new ManagementRuntime(serverId, nodeId, store, process,
                ReadyHost,
                preflight, () => backups, journal, new SystemClock(), port,
                (expected, token) => ReadyHost(expected).ObserveReadyAsync(token),
                prepareRecoveryWorkingCopy: string.IsNullOrWhiteSpace(configuration["Recovery:WorkingRoot"]) ? null :
                    async (request, token) =>
                    {
                        var archiveReplay = ArchiveReplay();
                        var coordinator = CreateBackups();
                        if (string.IsNullOrWhiteSpace(configuration["Recovery:PostgresServiceName"]))
                            await coordinator.PrepareRecoveryWorkingCopyAsync(request, Required("Recovery:WorkingRoot"), token);
                        else
                            await new ControlledRecoveryDatabase(Required("Recovery:PostgresServiceName"),
                                Required("Recovery:OriginalPostgresServiceName"), Required("Management:BusinessServiceName"),
                                Required("Management:ServiceName"), Required("Backup:PostgresBinDirectory"), Required("Recovery:WorkingRoot"),
                                Required("Recovery:PostgresConfigurationPath"), SelectedRequired("ConnectionStrings:Exam"), Required("ConnectionStrings:ExamRecovery"),
                                archiveRecovery: archiveReplay)
                                .PrepareAndStartAsync(request,
                                    ct => coordinator.PrepareRecoveryWorkingCopyAsync(request, Required("Recovery:WorkingRoot"), ct),
                                    ct => coordinator.FindPreparedRecoveryWorkingCopyAsync(request, Required("Recovery:WorkingRoot"), ct), token);
                    },
                verifyRecoveryTarget: string.IsNullOrWhiteSpace(configuration.GetConnectionString("ExamRecovery")) ? null :
                    async (request, serviceConfiguration, token) =>
                    {
                        var working = await CreateBackups().FindPreparedRecoveryWorkingCopyAsync(request, Required("Recovery:WorkingRoot"), token)
                            ?? throw new IOException("恢复工作副本尚未准备。");
                        var checks = (await new PostgresWorkingCopyReadinessProbe(SelectedRequired("ConnectionStrings:Exam"),
                            Required("ConnectionStrings:ExamRecovery"), SelectedConfiguration("PackageSecurity:CertificateThumbprint") ?? "", CurrentEpoch())
                            .RunAsync(serviceConfiguration, working, token)).ToList();
                        if (ArchiveReplay() is { } replay)
                            checks.Add(await replay.VerifyReplayedAsync(working, request, Required("ConnectionStrings:ExamRecovery"), token));
                        if (!string.IsNullOrWhiteSpace(configuration["Recovery:SelectionPath"]))
                        {
                            checks.Add(await ConfirmedState(serviceConfiguration, working, token));
                            checks.AddRange((await Preflight(Required("ConnectionStrings:ExamRecovery")).RunAsync(serviceConfiguration, token))
                                .Select(check => check with { Name = "activation-" + check.Name }));
                        }
                        return checks;
                    },
                recoveryWorkingDirectory: string.IsNullOrWhiteSpace(configuration["Recovery:WorkingRoot"]) ? null :
                    () => Path.GetFullPath(Path.Combine(Required("Recovery:WorkingRoot"), "slot")),
                activateRecovery: string.IsNullOrWhiteSpace(configuration["Recovery:SelectionPath"]) ? null :
                    async (operation, token) =>
                    {
                        var expected = await store.LoadAsync(token);
                        return await new RecoveryActivationRunner(process, () => ReadyHost(expected), Deployment(), journal).ActivateAsync(operation, token);
                    },
                observeRecoveryActivation: async (operation, token) =>
                {
                    // Query observes existing evidence only; it never copies, selects, rolls back or starts a service.
                    if (operation.RequestJson is null || operation.Restore is not { RecoveryVerified: true, BusinessReady: false } ||
                        operation.Checks?.Any(c => c.Name == "recovery-confirmed-state" && c.Passed && c.BlocksWrites) != true ||
                        operation.Checks?.Any(c => c.Name == "recovery-target" && c.Passed && c.BlocksWrites) != true ||
                        operation.Checks?.All(c => c.Passed || !c.BlocksWrites) != true)
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
                    var replay = ArchiveReplay();
                    if (replay is null &&
                        (File.Exists(Path.Combine(working.WorkingDirectory, "wal-recovery-plan.json")) ||
                         File.Exists(Path.Combine(working.WorkingDirectory, "wal-recovery.json")))) return null;
                    var expected = await store.LoadAsync(token);
                    var checks = await new PostgresWorkingCopyReadinessProbe(SelectedRequired("ConnectionStrings:Exam"),
                        Required("ConnectionStrings:ExamRecovery"), SelectedConfiguration("PackageSecurity:CertificateThumbprint") ?? "", CurrentEpoch())
                        .RunAsync(expected, working, token);
                    if (!checks.Any(c => c.Name == "recovery-target" && c.Passed && c.BlocksWrites) ||
                        checks.Any(c => !c.Passed && c.BlocksWrites) ||
                        replay is not null && !(await replay.VerifyReplayedAsync(working, request, Required("ConnectionStrings:ExamRecovery"), token)).Passed ||
                        !await ReadyHost(expected).ObserveReadyAsync(token) ||
                        process.Status != BusinessServiceStatus.Running || !selected.IsSelected(operation)) return null;
                    return operation with { Phase = ManagementOperationPhase.Completed, RecoveryActivation = RecoveryActivationPhase.BusinessReady,
                        Restore = operation.Restore with { BusinessReady = true }, Error = null };
                },
                readAudit: (limit, token) => new FileManagementOperationStore(Required("Management:OperationDirectory"))
                    .ReadAuditAsync(limit, token),
                readRuntimeLogs: RuntimeLogs, failover: failover,
                supportsAdvancedOperations: OperatingSystem.IsWindows(), checkReadinessOnStatus: true,
                readBackupInfo: (directory, token) => new PostgresBackupCoordinator(new PostgresBackupOptions(
                    "", "", Required("Backup:AllowedBackupRoot"), "", "", "", TimeSpan.FromSeconds(5)),
                    new ExternalProcessRunner()).ReadBackupInfoAsync(directory, token))
            {
                RecordFailure = (operation, error) => new ProtectedRuntimeLogWriter(managementLog, "management").Write(
                    error.Message.Contains("pg_hba.conf", StringComparison.OrdinalIgnoreCase)
                        ? "Exam.Management.Rebuild.PgHbaDenied" : "Exam.Management.Operation.Failed", "Error", 7001),
                ReadAutomaticBackupStatus = token => AutomaticBackupStatusFile.ReadAsync(
                    configuration.GetValue<bool>("Backup:Automatic:Enabled"), SelectedConfiguration("Backup:Automatic:StateDirectory"), token)
            };
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
    public static IPAddress ParseBindAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase))
            return IPAddress.Loopback;
        if (IPAddress.TryParse(address, out var value)) return value;
        throw new InvalidOperationException("管理监听地址必须是 IP 地址或 localhost；跨设备部署请填写服务器网卡地址或 0.0.0.0。");
    }

    public static X509Certificate2 LoadCertificate(string thumbprint) => LoadCertificate(new ServerCertificateSource(thumbprint));

    public static X509Certificate2 LoadCertificate(ServerCertificateSource source)
    {
        var certificate = source.Load();
        if (DateTime.UtcNow >= certificate.NotBefore.ToUniversalTime() && DateTime.UtcNow <= certificate.NotAfter.ToUniversalTime())
            return certificate;
        certificate.Dispose();
        throw new InvalidOperationException("管理 HTTPS 证书不在有效期内。");
    }
}
