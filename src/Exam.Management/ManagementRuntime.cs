using Exam.Application.ServiceManagement;
using Exam.Application.HighAvailability;
using Exam.Contracts.HighAvailability;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;
using System.Text.Json;

namespace Exam.Management;

/// <summary>Dependencies are loaded per operation, so a broken business configuration permits status and stop.</summary>
public sealed class ManagementRuntime(
    string serverId, string nodeId, IServiceConfigurationStore configurations,
    IManagedBusinessHost process, Func<ServiceConfiguration, IManagedBusinessHost> readyHost,
    IStartupReadinessProbe preflight, Func<IBackupCoordinator> backups,
    IManagementOperationStore journal, IClock clock, int managementPort = 0,
    Func<ServiceConfiguration, CancellationToken, Task<bool>>? observeBusinessReady = null,
    Func<RestoreRequest, CancellationToken, Task>? prepareRecoveryWorkingCopy = null,
    Func<RestoreRequest, ServiceConfiguration, CancellationToken, Task<IReadOnlyList<StartupCheckResult>>>? verifyRecoveryTarget = null,
    Func<string>? recoveryWorkingDirectory = null,
    Func<ManagementOperation, CancellationToken, Task<ManagementOperation>>? activateRecovery = null,
    Func<ManagementOperation, CancellationToken, Task<ManagementOperation?>>? observeRecoveryActivation = null,
    Func<int, CancellationToken, Task<IReadOnlyList<ManagementAuditEntry>>>? readAudit = null,
    Func<int, CancellationToken, Task<IReadOnlyList<ProtectedRuntimeLogEntry>>>? readRuntimeLogs = null,
    Func<DurableFailoverCoordinator>? failover = null,
    bool checkReadinessOnStatus = false,
    bool requireCurrentRecoveryState = true,
    bool supportsAdvancedOperations = true,
    Func<string, CancellationToken, Task<BackupInfo>>? readBackupInfo = null,
    string? unsupportedOperationsMessage = null)
{
    public string ServerId { get; } = serverId;
    public string NodeId { get; } = nodeId;
    public bool SupportsAdvancedOperations { get; } = supportsAdvancedOperations;
    public Action<ManagementOperation, Exception>? RecordFailure { get; init; }
    public IStartupReadinessProbe? StatusInspection { get; init; }
    public Func<CancellationToken, Task<BackupMaintenanceStatus?>>? ReadAutomaticBackupStatus { get; init; }
    public async Task<BackupInfo> GetBackupInfoAsync(string sourceDirectory, CancellationToken token)
    {
        if (readBackupInfo is null) throw new InvalidOperationException("备份信息读取未配置。");
        try { return await readBackupInfo(sourceDirectory, token); }
        catch (UnauthorizedAccessException)
        { throw new DomainRuleException(ErrorCodes.Forbidden, "备份来源不在服务器允许的目录内，或服务账户没有读取权限。"); }
        catch (InvalidDataException)
        { throw new DomainRuleException(ErrorCodes.InvalidInput, "该来源目录缺少有效的备份清单，请选择一次完整备份的目录。"); }
        catch (DirectoryNotFoundException)
        { throw new DomainRuleException(ErrorCodes.NotFound, "服务器上的备份来源目录不存在。"); }
    }
    private DurableFailoverCoordinator Failover => failover?.Invoke() ?? throw new InvalidOperationException("正式主备管理未配置。");
    public Task<HighAvailabilityStatus> GetHighAvailabilityAsync(CancellationToken token)
    {
        RequireAdvancedOperations();
        return Failover.GetStatusAsync(token);
    }
    public Task<HighAvailabilityStatus> GetLocalHighAvailabilityAsync(CancellationToken token)
    {
        RequireAdvancedOperations();
        return Failover.GetLocalStatusAsync(token);
    }
    public Task<ManagementOperation> ExecuteHighAvailabilityAsync(HighAvailabilityRequest request, string actor,
        ManagementOperationKind kind, CancellationToken token)
    {
        RequireTarget(request.ServerId, request.NodeId);
        if (request.ActorId != actor || !DurableFailoverCoordinator.IsFailover(kind))
            throw new DomainRuleException(ErrorCodes.Forbidden, "主备操作者必须来自当前已认证的考务会话。");
        return Operations.ExecuteAsync(request.RequestId, actor, kind, request,
            (operation, cancellation) =>
            {
                RequireAdvancedOperations();
                return Failover.ExecuteAsync(operation, request, cancellation);
            }, token);
    }

    private void RequireAdvancedOperations()
    {
        if (!SupportsAdvancedOperations)
            throw new DomainRuleException("PLATFORM_NOT_SUPPORTED",
                unsupportedOperationsMessage ?? "Linux 当前支持业务服务启停、配置、状态、日志和备份；主备切换及隔离恢复需要 Windows 管理宿主。");
    }
    public async Task<IReadOnlyList<ManagementAuditEntry>> ReadAuditAsync(int limit, CancellationToken token)
    {
        if (readAudit is null) throw new InvalidOperationException("受保护操作日志未配置。");
        var entries = await readAudit(limit, token);
        var changed = false;
        foreach (var entry in entries.Where(entry => entry.Phase == ManagementOperationPhase.Canceled &&
            entry.Kind is ManagementOperationKind.Takeover or ManagementOperationKind.Rebuild))
            if (await journal.FindAsync(entry.RequestId, token) is { } original)
                changed |= (await Operations.FindAsync(original.RequestId, original.ActorId, token)).Phase != entry.Phase;
        return changed ? await readAudit(limit, token) : entries;
    }
    public Task<IReadOnlyList<ProtectedRuntimeLogEntry>> ReadRuntimeLogsAsync(int limit, CancellationToken token) =>
        readRuntimeLogs is null ? throw new InvalidOperationException("受保护运行日志未配置。") : readRuntimeLogs(limit, token);
    private readonly object _operationsGate = new();
    private ManagementOperationRunner? _operations;
    public ManagementOperationRunner Operations
    {
        get { lock (_operationsGate) return _operations ??= new(ServerId, NodeId, journal, clock, ReconcileAsync, RecordFailure,
            (operation, token) => Failover.CancelAsync(operation, token),
            (operation, token) => Failover.CompleteCommittedChangeAsync(operation, token)); }
    }

    private async Task<ManagementOperation?> ReconcileAsync(ManagementOperation operation, CancellationToken token)
    {
        if (DurableFailoverCoordinator.IsFailover(operation.Kind)) return await Failover.ReconcileAsync(operation, token);
        if (operation.Kind == ManagementOperationKind.Restore && operation.Phase == ManagementOperationPhase.NeedsVerification &&
            operation.Checks?.Any(check => check.Name == "recovery-confirmed-state" && !check.Passed && check.BlocksWrites) == true)
            return null; // Read-only queries cannot replace a missing confirmation with mere staging evidence.
        if (operation.Kind == ManagementOperationKind.Restore && operation.RecoveryActivation is not null)
            return observeRecoveryActivation is null ? null : await observeRecoveryActivation(operation, token);
        if (operation.Kind == ManagementOperationKind.Stop && process.Status == BusinessServiceStatus.Stopped)
            return operation with { Phase = ManagementOperationPhase.Completed, Error = null, Status = await GetStatusAsync(token) };
        if (operation.Kind == ManagementOperationKind.Configure && operation.RequestJson is not null && process.Status == BusinessServiceStatus.Stopped)
        {
            var original = JsonSerializer.Deserialize<ManagementConfigurationRequest>(operation.RequestJson, JsonDefaults.Create());
            if (original is null || original.RequestId != operation.RequestId || original.ServerId != ServerId || original.NodeId != NodeId ||
                original.ExpectedVersion is < 1 or long.MaxValue) return null;
            var configuration = await GetConfigurationAsync(token);
            if (configuration == original.Configuration with { Version = original.ExpectedVersion + 1 })
                return operation with { Phase = ManagementOperationPhase.Completed, Error = null, Configuration = configuration };
        }
        if (operation.Kind == ManagementOperationKind.Start && process.Status == BusinessServiceStatus.Running && observeBusinessReady is not null)
        {
            var configuration = await GetConfigurationAsync(token);
            var checks = await preflight.RunAsync(configuration, token);
            if (checks.Count > 0 && checks.All(c => c.Passed || !c.BlocksWrites) && await observeBusinessReady(configuration, token) &&
                process.Status == BusinessServiceStatus.Running)
                return operation with { Phase = ManagementOperationPhase.Completed, Error = null,
                    Status = new(process.Status, BusinessReadiness.Ready, configuration, checks) };
        }
        if (operation.Kind is ManagementOperationKind.Backup or ManagementOperationKind.Restore &&
            operation.RequestJson is not null && process.Status == BusinessServiceStatus.Stopped && backups() is IBackupOperationEvidence evidence)
        {
            if (operation.Kind == ManagementOperationKind.Backup)
            {
                var original = JsonSerializer.Deserialize<BackupRequest>(operation.RequestJson, JsonDefaults.Create());
                if (original is null || original.RequestId != operation.RequestId || original.ActorId != operation.ActorId) return null;
                var result = await evidence.FindCompletedBackupAsync(original, token);
                if (result is not null && result.IncludesDatabase && result.IncludesProtectedConfiguration && result.IncludesVersionedResources &&
                    process.Status == BusinessServiceStatus.Stopped)
                    return operation with { Phase = ManagementOperationPhase.Completed, Error = null, Backup = result };
            }
            else
            {
                var original = JsonSerializer.Deserialize<RestoreRequest>(operation.RequestJson, JsonDefaults.Create());
                if (original is null || original.RequestId != operation.RequestId || original.ActorId != operation.ActorId) return null;
                if (await evidence.FindStagedRestoreAsync(original, token) && process.Status == BusinessServiceStatus.Stopped)
                    return operation with { Phase = ManagementOperationPhase.Staged, Error = null, Restore = new(original.BackupId, true, false, false) };
            }
        }
        // Recovery readiness cannot be inferred from the original database or a completed staging marker.
        return null;
    }

    public async Task<ManagementStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        ServiceConfiguration? configuration = null;
        IReadOnlyList<StartupCheckResult> checks = [];
        try { configuration = await GetConfigurationAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { checks = [new("service-configuration", false, "业务配置缺失、损坏或与管理节点不一致。", true)]; }
        var readiness = BusinessReadiness.Unknown;
        if (checkReadinessOnStatus && configuration is not null && process.Status == BusinessServiceStatus.Running && observeBusinessReady is not null)
        {
            try
            {
                readiness = await observeBusinessReady(configuration, cancellationToken) ? BusinessReadiness.Ready : BusinessReadiness.NotReady;
                checks = [new("business-readiness", readiness == BusinessReadiness.Ready,
                    readiness == BusinessReadiness.Ready ? "业务就绪接口已确认服务器程序正常运行。" : "业务就绪接口报告服务器程序尚未就绪。", true)];
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { readiness = BusinessReadiness.Unknown; }
        }
        return new(process.Status, readiness, configuration, checks)
        {
            AutomaticBackup = ReadAutomaticBackupStatus is null ? null : await ReadAutomaticBackupStatus(cancellationToken)
        };
    }

    public async Task<ServiceConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        var configuration = await configurations.LoadAsync(cancellationToken);
        RequireTarget(configuration.ServerId, configuration.NodeId);
        if (configuration.HttpsPort == managementPort)
            throw new DomainRuleException(ErrorCodes.InvalidInput, "业务端口不能与独立管理 HTTPS 端口相同。");
        return configuration;
    }

    public async Task<ManagementOperation> FindOperationAsync(string requestId, string actor, CancellationToken cancellationToken)
    {
        var operation = await Operations.FindAsync(requestId, actor, cancellationToken);
        return operation.Phase is ManagementOperationPhase.NeedsVerification or ManagementOperationPhase.Staged
            ? operation with { Status = await GetStatusAsync(cancellationToken) } : operation;
    }

    public void RequireTarget(string requestedServer, string requestedNode)
    {
        if (requestedServer != ServerId || requestedNode != NodeId)
            throw new DomainRuleException(ErrorCodes.InvalidInput, "请求目标与此服务器管理节点不一致。");
    }

    public Task<ManagementOperation> ExecuteServiceAsync(ManagementServiceRequest request, string actor,
        ManagementOperationKind kind, CancellationToken cancellationToken)
    {
        RequireTarget(request.ServerId, request.NodeId);
        return Operations.ExecuteAsync(request.RequestId, actor, kind, request, async (operation, token) =>
        {
            if (kind == ManagementOperationKind.Stop)
            {
                await process.StopAsync(token);
                if (process.Status != BusinessServiceStatus.Stopped) throw new IOException("业务进程未停止。");
                return operation with { Phase = ManagementOperationPhase.Completed, Status = await GetStatusAsync(token) };
            }
            var configuration = await GetConfigurationAsync(token);
            if (kind == ManagementOperationKind.Checks)
            {
                var checks = await (StatusInspection ?? preflight).RunAsync(configuration, token);
                if (checks.Count == 0) throw new IOException("状态检测未返回结果。");
                return operation with { Phase = ManagementOperationPhase.Completed, Checks = checks };
            }
            if (kind != ManagementOperationKind.Start) throw new ArgumentException("操作类型无效。");
            if (process.Status == BusinessServiceStatus.Running)
            {
                // A fresh readiness wrapper deliberately reports Recovering until it has observed
                // HTTPS readiness. Repeated starts must observe the existing process, not try SCM again.
                try
                {
                    if (await ReconcileAsync(operation, token) is { } running) return running;
                }
                catch (Exception)
                {
                    throw new DomainRuleException(ErrorCodes.ServiceRecovering,
                        "现有业务进程未能完成就绪核对，未重复执行启动；请检查当前状态。", retryable: true);
                }
                throw new DomainRuleException(ErrorCodes.ServiceRecovering,
                    "现有业务进程尚未通过就绪核对，未重复执行启动；请检查当前状态。", retryable: true);
            }
            var service = new ServiceManagementService(configuration, readyHost(configuration), preflight, backups(), clock, configurations);
            var result = await service.StartAsync(token);
            return operation with { Phase = ManagementOperationPhase.Completed,
                Status = new(process.Status, BusinessReadiness.Ready, result.Configuration, result.Checks) };
        }, cancellationToken);
    }

    public Task<ManagementOperation> ConfigureAsync(ManagementConfigurationRequest request, string actor, CancellationToken cancellationToken)
    {
        RequireTarget(request.ServerId, request.NodeId);
        if (request.Configuration is null || request.ExpectedVersion < 1 || request.Configuration.HttpsPort == managementPort)
            throw new DomainRuleException(ErrorCodes.InvalidInput, "目标配置、预期版本或独立端口无效。");
        RequireTarget(request.Configuration.ServerId, request.Configuration.NodeId);
        return Operations.ExecuteAsync(request.RequestId, actor, ManagementOperationKind.Configure, request, async (operation, token) =>
        {
            var service = new ServiceManagementService(await GetConfigurationAsync(token), process, preflight, backups(), clock, configurations);
            var saved = await service.ConfigureAsync(request.Configuration, request.ExpectedVersion, token);
            return operation with { Phase = ManagementOperationPhase.Completed, Configuration = saved };
        }, cancellationToken);
    }

    public Task<ManagementOperation> BackupAsync(BackupRequest request, string actor, CancellationToken cancellationToken) =>
        Operations.ExecuteAsync(request.RequestId, actor, ManagementOperationKind.Backup, request with { ActorId = actor }, async (operation, token) =>
        {
            if (process.Status != BusinessServiceStatus.Stopped)
                throw new DomainRuleException(ErrorCodes.VersionConflict, "服务器管理备份前须停止业务服务，避免与业务入口的备份操作并发。");
            var result = await backups().CreateAsync(request with { ActorId = actor }, token);
            if (!result.IncludesDatabase || !result.IncludesProtectedConfiguration || !result.IncludesVersionedResources)
                throw new IOException("备份不完整。");
            return operation with { Phase = ManagementOperationPhase.Completed, Backup = result };
        }, cancellationToken);

    public Task<ManagementOperation> RestoreAsync(RestoreRequest request, string actor, CancellationToken cancellationToken) =>
        Operations.ExecuteAsync(request.RequestId, actor, ManagementOperationKind.Restore, request with { ActorId = actor }, async (operation, token) =>
        {
            RequireAdvancedOperations();
            if (process.Status != BusinessServiceStatus.Stopped)
                throw new DomainRuleException(ErrorCodes.VersionConflict, "隔离恢复前必须停止业务服务。");
            var targetDirectory = recoveryWorkingDirectory?.Invoke();
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (operation.RecoveryWorkingDirectory is not null &&
                !string.Equals(operation.RecoveryWorkingDirectory, targetDirectory, pathComparison))
                throw new IOException("恢复部署目标已改变，原请求不能切换到另一工作槽。");
            if (operation.Restore?.IsolationStaged != true)
                await backups().RestoreAsync(request with { ActorId = actor }, token);
            if (process.Status != BusinessServiceStatus.Stopped) throw new IOException("隔离暂存期间业务进程状态发生变化。");
            var staged = operation with { Phase = ManagementOperationPhase.Staged,
                Restore = new(request.BackupId, true, false, false), RecoveryWorkingDirectory = targetDirectory,
                RecoveryActivation = null, Error = null };
            await journal.SaveAsync(staged, token); // Persist staging before creating an independently mutable target.
            if (prepareRecoveryWorkingCopy is null) return staged;
            await prepareRecoveryWorkingCopy(request with { ActorId = actor }, token);
            if (process.Status != BusinessServiceStatus.Stopped) throw new IOException("工作副本准备期间业务进程状态发生变化。");
            // Preparing a copy proves neither restored-domain correctness nor business readiness.
            var checks = new List<StartupCheckResult> { new("recovery-working-copy", true,
                "独立恢复工作副本已准备，尚未开放业务。", true) };
            staged = staged with { Checks = checks.AsReadOnly(), UpdatedAt = clock.UtcNow };
            await journal.SaveAsync(staged, token); // Queries can observe the prepared copy while target verification is running.
            if (verifyRecoveryTarget is not null)
            {
                var verified = await verifyRecoveryTarget(request with { ActorId = actor }, await GetConfigurationAsync(token), token);
                if (verified is null || verified.Count == 0 || verified.Any(c => c is null || string.IsNullOrWhiteSpace(c.Name)) ||
                    verified.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != verified.Count)
                    throw new IOException("恢复目标探针结果无效。");
                checks.AddRange(verified);
                if (activateRecovery is not null && requireCurrentRecoveryState && !checks.Any(c => c.Name == "recovery-confirmed-state"))
                    checks.Add(new("recovery-confirmed-state", false,
                        "恢复激活缺少最新已确认状态证明，禁止开放业务。", true));
                if (checks.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != checks.Count)
                    throw new IOException("恢复检查名称重复。");
                if (process.Status != BusinessServiceStatus.Stopped) throw new IOException("恢复核对期间业务进程状态发生变化。");
                staged = staged with { Restore = new(request.BackupId, true,
                    checks.Any(c => c.Name == "recovery-target" && c.Passed && c.BlocksWrites) &&
                    checks.All(c => c.Passed || !c.BlocksWrites), false) };
            }
            staged = staged with { Checks = checks.AsReadOnly() };
            if (checks.Any(check => check.Name == "recovery-confirmed-state" && !check.Passed && check.BlocksWrites))
                staged = staged with { Phase = ManagementOperationPhase.NeedsVerification,
                    Error = new(ErrorCodes.ServiceRecovering,
                        "原库与恢复副本的最新已确认状态未能核实；须经已同步备节点的持久确认记录核对，不得开放业务。") };
            await journal.SaveAsync(staged, token);
            return staged.Restore is { RecoveryVerified: true } && activateRecovery is not null
                ? await activateRecovery(staged, token) : staged;
        }, cancellationToken, resumeStagedRestore: prepareRecoveryWorkingCopy is not null);
}
