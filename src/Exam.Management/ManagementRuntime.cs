using Exam.Application.ServiceManagement;
using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;

namespace Exam.Management;

/// <summary>Dependencies are loaded per operation, so a broken business configuration permits status and stop.</summary>
public sealed class ManagementRuntime(
    string serverId, string nodeId, IServiceConfigurationStore configurations,
    IManagedBusinessHost process, Func<IManagedBusinessHost> readyHost,
    IStartupReadinessProbe preflight, Func<IBackupCoordinator> backups,
    IManagementOperationStore journal, IClock clock, int managementPort = 0)
{
    public string ServerId { get; } = serverId;
    public string NodeId { get; } = nodeId;
    public ManagementOperationRunner Operations { get; } = new(serverId, nodeId, journal, clock);

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
            await backups().RestoreAsync(request with { ActorId = actor }, token);
            if (process.Status != BusinessServiceStatus.Stopped) throw new IOException("隔离暂存期间业务进程状态发生变化。");
            // Existing coordinator stages files only. Do not run probes against the original database and call that recovery.
            return operation with { Phase = ManagementOperationPhase.Staged,
                Restore = new(request.BackupId, true, false, false) };
        }, cancellationToken);
}
