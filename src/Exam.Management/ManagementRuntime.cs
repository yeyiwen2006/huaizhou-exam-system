using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;
using System.Text.Json;

namespace Exam.Management;

/// <summary>Dependencies are loaded per operation, so a broken business configuration permits status and stop.</summary>
public sealed class ManagementRuntime(
    string serverId, string nodeId, IServiceConfigurationStore configurations,
    IManagedBusinessHost process, Func<IManagedBusinessHost> readyHost,
    IStartupReadinessProbe preflight, Func<IBackupCoordinator> backups,
    IManagementOperationStore journal, IClock clock, int managementPort = 0,
    Func<CancellationToken, Task<bool>>? observeBusinessReady = null,
    Func<RestoreRequest, CancellationToken, Task>? prepareRecoveryWorkingCopy = null,
    Func<RestoreRequest, ServiceConfiguration, CancellationToken, Task<IReadOnlyList<StartupCheckResult>>>? verifyRecoveryTarget = null,
    Func<string>? recoveryWorkingDirectory = null,
    Func<ManagementOperation, CancellationToken, Task<ManagementOperation>>? activateRecovery = null,
    Func<ManagementOperation, CancellationToken, Task<ManagementOperation?>>? observeRecoveryActivation = null)
{
    public string ServerId { get; } = serverId;
    public string NodeId { get; } = nodeId;
    private readonly object _operationsGate = new();
    private ManagementOperationRunner? _operations;
    public ManagementOperationRunner Operations
    {
        get { lock (_operationsGate) return _operations ??= new(ServerId, NodeId, journal, clock, ReconcileAsync); }
    }

    private async Task<ManagementOperation?> ReconcileAsync(ManagementOperation operation, CancellationToken token)
    {
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
            if (checks.Count > 0 && checks.All(c => c.Passed || !c.BlocksWrites) && await observeBusinessReady(token) &&
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
        return new(process.Status, BusinessReadiness.Unknown, configuration, checks);
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
            throw new DomainRuleException(ErrorCodes.InvalidInput, "请求目标不是本机管理节点。");
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
                var checks = await preflight.RunAsync(configuration, token);
                if (checks.Count == 0) throw new IOException("自检未返回结果。");
                return operation with { Phase = ManagementOperationPhase.Completed, Checks = checks };
            }
            if (kind != ManagementOperationKind.Start) throw new ArgumentException("操作类型无效。");
            var service = new ServiceManagementService(configuration, readyHost(), preflight, backups(), clock, configurations);
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
                throw new DomainRuleException(ErrorCodes.VersionConflict, "本机管理备份前须停止业务服务，避免与业务入口的备份操作并发。");
            var result = await backups().CreateAsync(request with { ActorId = actor }, token);
            if (!result.IncludesDatabase || !result.IncludesProtectedConfiguration || !result.IncludesVersionedResources)
                throw new IOException("备份不完整。");
            return operation with { Phase = ManagementOperationPhase.Completed, Backup = result };
        }, cancellationToken);

    public Task<ManagementOperation> RestoreAsync(RestoreRequest request, string actor, CancellationToken cancellationToken) =>
        Operations.ExecuteAsync(request.RequestId, actor, ManagementOperationKind.Restore, request with { ActorId = actor }, async (operation, token) =>
        {
            if (process.Status != BusinessServiceStatus.Stopped)
                throw new DomainRuleException(ErrorCodes.VersionConflict, "隔离恢复前必须停止业务服务。");
            var targetDirectory = recoveryWorkingDirectory?.Invoke();
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (operation.RecoveryWorkingDirectory is not null &&
                !string.Equals(operation.RecoveryWorkingDirectory, targetDirectory, pathComparison))
                throw new IOException("恢复部署目标已改变，原请求不能切换到另一工作槽。");
            if (operation.Phase != ManagementOperationPhase.Staged)
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
            if (verifyRecoveryTarget is not null)
            {
                var verified = await verifyRecoveryTarget(request with { ActorId = actor }, await GetConfigurationAsync(token), token);
                if (verified is null || verified.Count == 0 || verified.Any(c => c is null || string.IsNullOrWhiteSpace(c.Name)) ||
                    verified.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != verified.Count)
                    throw new IOException("恢复目标探针结果无效。");
                checks.AddRange(verified);
                if (checks.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != checks.Count)
                    throw new IOException("恢复检查名称重复。");
                if (process.Status != BusinessServiceStatus.Stopped) throw new IOException("恢复核对期间业务进程状态发生变化。");
                staged = staged with { Restore = new(request.BackupId, true,
                    verified.Any(c => c.Name == "recovery-target" && c.Passed && c.BlocksWrites) &&
                    verified.All(c => c.Passed || !c.BlocksWrites), false) };
            }
            staged = staged with { Checks = checks.AsReadOnly() };
            await journal.SaveAsync(staged, token);
            return staged.Restore is { RecoveryVerified: true } && activateRecovery is not null
                ? await activateRecovery(staged, token) : staged;
        }, cancellationToken, resumeStagedRestore: prepareRecoveryWorkingCopy is not null);
}
