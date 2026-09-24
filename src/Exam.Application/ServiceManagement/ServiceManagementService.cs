using Exam.Contracts.Common;
using Exam.Contracts.ServiceManagement;
using Exam.Domain.Common;

namespace Exam.Application.ServiceManagement;

public interface IManagedBusinessHost
{
    BusinessServiceStatus Status { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public interface IStartupReadinessProbe
{
    Task<IReadOnlyList<StartupCheckResult>> RunAsync(
        ServiceConfiguration configuration,
        CancellationToken cancellationToken);
}

public interface IServiceConfigurationStore
{
    Task<ServiceConfiguration> LoadAsync(CancellationToken cancellationToken = default);

    Task<ServiceConfiguration> SaveAsync(
        ServiceConfiguration requested,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}

public interface IBackupCoordinator
{
    Task<BackupResult> CreateAsync(BackupRequest request, CancellationToken cancellationToken);

    Task RestoreAsync(RestoreRequest request, CancellationToken cancellationToken);
}

public sealed class ServiceManagementService
{
    private readonly IManagedBusinessHost _host;
    private readonly IStartupReadinessProbe _readiness;
    private readonly IBackupCoordinator _backups;
    private readonly IClock _clock;
    private readonly IServiceConfigurationStore _configurationStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (BackupRequest Request, BackupResult Result)> _completedBackups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (RestoreRequest Request, RestoreResult Result)> _completedRestores = new(StringComparer.Ordinal);
    private ServiceConfiguration _configuration;
    private IReadOnlyList<StartupCheckResult> _checks = [];
    private string? _lastError;
    private BusinessServiceStatus _status;

    public ServiceManagementService(
        ServiceConfiguration configuration,
        IManagedBusinessHost host,
        IStartupReadinessProbe readiness,
        IBackupCoordinator backups,
        IClock clock,
        IServiceConfigurationStore configurationStore)
    {
        Validate(configuration);
        _configuration = configuration;
        _host = host;
        _readiness = readiness;
        _backups = backups;
        _clock = clock;
        _configurationStore = configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));
        _status = host.Status;
    }

    public static async Task<ServiceManagementService> LoadAsync(
        IServiceConfigurationStore configurationStore,
        IManagedBusinessHost host,
        IStartupReadinessProbe readiness,
        IBackupCoordinator backups,
        IClock clock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configurationStore);
        var configuration = await configurationStore.LoadAsync(cancellationToken);
        return new ServiceManagementService(
            configuration, host, readiness, backups, clock, configurationStore);
    }

    public ServiceStatusSnapshot GetStatus()
    {
        var actual = _host.Status;
        if (actual == BusinessServiceStatus.Faulted && _status != BusinessServiceStatus.Faulted)
        {
            _status = BusinessServiceStatus.Faulted;
            _lastError = "无法核对业务宿主状态，须检查本机服务控制器。";
        }
        else if (_status == BusinessServiceStatus.Running && actual != BusinessServiceStatus.Running)
        {
            _status = BusinessServiceStatus.Faulted;
            _lastError = "业务宿主已停止或退出，须检查实际进程状态。";
        }
        else if ((_status is BusinessServiceStatus.Stopped or BusinessServiceStatus.Recovering) &&
                 actual != BusinessServiceStatus.Stopped)
        {
            _status = BusinessServiceStatus.Faulted;
            _lastError = "业务宿主意外运行，须先隔离并核对实际进程状态。";
        }

        return new(_status, _configuration, _checks, _clock.UtcNow, _lastError);
    }

    public async Task<ServiceConfiguration> ConfigureAsync(
        ServiceConfiguration requested,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if ((_status is BusinessServiceStatus.Running or BusinessServiceStatus.Starting or BusinessServiceStatus.Stopping) ||
                _host.Status != BusinessServiceStatus.Stopped)
            {
                throw new DomainRuleException(ErrorCodes.VersionConflict, "业务服务运行时不能修改本机服务配置。");
            }

            if (_configuration.Version != expectedVersion)
            {
                throw new DomainRuleException(ErrorCodes.VersionConflict, "服务配置版本已变化，请刷新后重试。");
            }

            Validate(requested);
            var next = await _configurationStore.SaveAsync(requested, expectedVersion, cancellationToken);
            _configuration = next;
            _checks = [];
            _lastError = null;
            return _configuration;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServiceStatusSnapshot> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = GetStatus();
            if (current.Status == BusinessServiceStatus.Running)
            {
                return current;
            }

            if (_host.Status == BusinessServiceStatus.Running)
            {
                throw new DomainRuleException(
                    ErrorCodes.VersionConflict,
                    "业务宿主已在管理流程之外运行，须先停止并核对实际状态。");
            }

            _status = BusinessServiceStatus.Recovering;
            try
            {
                var persisted = await _configurationStore.LoadAsync(cancellationToken);
                Validate(persisted);
                if (persisted.ServerId != _configuration.ServerId || persisted.NodeId != _configuration.NodeId ||
                    persisted.Version < _configuration.Version)
                    throw new InvalidOperationException("持久配置的身份或版本发生不允许的变化。");
                _configuration = persisted;
                _checks = await ReadChecksAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _status = BusinessServiceStatus.Faulted;
                _lastError = "启动检查已取消，未发出启动命令。";
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _status = BusinessServiceStatus.Faulted;
                _lastError = "启动检查未完成，须检查受保护的本机诊断记录。";
                throw new DomainRuleException(
                    ErrorCodes.ServiceRecovering,
                    "启动检查未完成，业务写入保持关闭。", retryable: true);
            }
            if (_checks.Count == 0)
            {
                _lastError = "启动检查没有返回任何结果，业务写入保持关闭。";
                throw new DomainRuleException(ErrorCodes.ServiceRecovering, _lastError, retryable: true);
            }
            var blocking = _checks.Where(check => !check.Passed && check.BlocksWrites).ToArray();
            if (blocking.Length > 0)
            {
                _lastError = string.Join("；", blocking.Select(check => $"{check.Name}: {check.Message}"));
                throw new DomainRuleException(ErrorCodes.ServiceRecovering, _lastError, retryable: true);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_host.Status != BusinessServiceStatus.Stopped)
                    throw new InvalidOperationException("启动检查期间宿主状态已变化，不能发出启动命令。");
                _status = BusinessServiceStatus.Starting;
                await _host.StartAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (_host.Status != BusinessServiceStatus.Running)
                {
                    throw new InvalidOperationException("业务宿主未达到运行状态。");
                }
                _status = BusinessServiceStatus.Running;
                _lastError = null;
                var started = GetStatus();
                if (started.Status != BusinessServiceStatus.Running)
                {
                    throw new InvalidOperationException("业务宿主状态在启动完成后发生变化。");
                }

                return started;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _status = BusinessServiceStatus.Faulted;
                _lastError = "启动已取消，须核对实际业务宿主状态。";
                throw;
            }
            catch (Exception)
            {
                _status = BusinessServiceStatus.Faulted;
                _lastError = "业务服务启动失败，须检查受保护的本机诊断记录。";
                throw new DomainRuleException(ErrorCodes.PersistenceFailed, "业务服务启动失败，管理入口仍保持可用。", retryable: true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ServiceStatusSnapshot> StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var current = GetStatus();
            if (current.Status == BusinessServiceStatus.Stopped)
            {
                return current;
            }

            try
            {
                _status = BusinessServiceStatus.Stopping;
                await _host.StopAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (_host.Status != BusinessServiceStatus.Stopped)
                {
                    throw new InvalidOperationException("业务宿主未达到停止状态。");
                }
                _status = BusinessServiceStatus.Stopped;
                _lastError = null;
                var stopped = GetStatus();
                if (stopped.Status != BusinessServiceStatus.Stopped)
                {
                    throw new InvalidOperationException("业务宿主状态在停止完成后发生变化。");
                }

                return stopped;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _status = BusinessServiceStatus.Faulted;
                _lastError = "停止已取消，须核对实际业务宿主状态。";
                throw;
            }
            catch (Exception)
            {
                _status = BusinessServiceStatus.Faulted;
                _lastError = "业务服务停止失败，须检查实际进程及受保护的本机诊断记录。";
                throw new DomainRuleException(ErrorCodes.PersistenceFailed, "业务服务停止失败，须检查实际进程状态。", retryable: true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BackupResult> CreateBackupAsync(
        BackupRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureRequestId(request.RequestId);
            if (_completedRestores.ContainsKey(request.RequestId))
            {
                throw new DomainRuleException(ErrorCodes.InvalidInput, "requestId 已用于恢复操作。");
            }

            if (_completedBackups.TryGetValue(request.RequestId, out var previous))
            {
                if (previous.Request != request)
                    throw new DomainRuleException(ErrorCodes.InvalidInput, "requestId 已绑定不同的操作内容或操作者。");
                return previous.Result;
            }

            var result = await _backups.CreateAsync(request, cancellationToken);
            if (string.IsNullOrWhiteSpace(result.BackupId) || string.IsNullOrWhiteSpace(result.DestinationDirectory) ||
                !result.IncludesDatabase || !result.IncludesProtectedConfiguration || !result.IncludesVersionedResources)
            {
                throw new DomainRuleException(ErrorCodes.PersistenceFailed, "备份不完整，不能标记为可恢复备份。");
            }

            _completedBackups.Add(request.RequestId, (request, result));
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RestoreResult> RestoreAsync(
        RestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureRequestId(request.RequestId);
            if (_completedBackups.ContainsKey(request.RequestId))
            {
                throw new DomainRuleException(ErrorCodes.InvalidInput, "requestId 已用于备份操作。");
            }

            if (_completedRestores.TryGetValue(request.RequestId, out var previous))
            {
                if (previous.Request != request)
                    throw new DomainRuleException(ErrorCodes.InvalidInput, "requestId 已绑定不同的操作内容或操作者。");
                return previous.Result;
            }

            if (GetStatus().Status != BusinessServiceStatus.Stopped || _host.Status != BusinessServiceStatus.Stopped)
            {
                throw new DomainRuleException(ErrorCodes.VersionConflict, "恢复前必须停止业务服务。");
            }

            _status = BusinessServiceStatus.Recovering;
            _checks = [];
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _backups.RestoreAsync(request, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                RequireStoppedAfterRestore();
                _checks = await ReadChecksAsync(cancellationToken);
                RequireStoppedAfterRestore();
                if (_checks.Count == 0 || _checks.Any(check => !check.Passed && check.BlocksWrites))
                    throw new DomainRuleException(ErrorCodes.ServiceRecovering,
                        "恢复数据未通过启动检查，业务写入保持关闭。", retryable: true);
                var result = new RestoreResult(request.BackupId, _clock.UtcNow, _checks);
                _completedRestores.Add(request.RequestId, (request, result));
                _status = BusinessServiceStatus.Stopped;
                _lastError = null;
                return result;
            }
            catch (OperationCanceledException)
            {
                _status = BusinessServiceStatus.Faulted;
                _lastError = "恢复已取消，须核对暂存数据和实际宿主状态，不得自动启动。";
                throw;
            }
            catch (DomainRuleException error)
            {
                _status = BusinessServiceStatus.Faulted;
                _lastError = error.Message;
                throw;
            }
            catch (Exception)
            {
                _status = BusinessServiceStatus.Faulted;
                _lastError = "恢复未完成，须核对暂存数据和实际宿主状态，不得自动启动。";
                throw new DomainRuleException(ErrorCodes.PersistenceFailed, _lastError, retryable: true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void RequireStoppedAfterRestore()
    {
        if (_host.Status != BusinessServiceStatus.Stopped)
            throw new DomainRuleException(ErrorCodes.ServiceRecovering,
                "恢复期间宿主状态发生变化，不能确认恢复成功。", retryable: true);
    }

    private async Task<IReadOnlyList<StartupCheckResult>> ReadChecksAsync(CancellationToken token)
    {
        var checks = await _readiness.RunAsync(_configuration, token);
        token.ThrowIfCancellationRequested();
        if (checks is null || checks.Any(c => c is null || string.IsNullOrWhiteSpace(c.Name) || c.Message is null) ||
            checks.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != checks.Count)
            throw new InvalidOperationException("启动探针返回了无效或重复的检查项目。");
        return Array.AsReadOnly(checks.ToArray());
    }

    private static void Validate(ServiceConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.Version < 1 || string.IsNullOrWhiteSpace(configuration.BindAddress) ||
            string.IsNullOrWhiteSpace(configuration.ServerId) || string.IsNullOrWhiteSpace(configuration.NodeId))
        {
            throw new ArgumentException("服务器、节点、绑定地址和配置版本必须有效。", nameof(configuration));
        }

        if (configuration.HttpsPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration), "HTTPS 端口必须在 1 至 65535 之间。");
        }

        if (string.IsNullOrWhiteSpace(configuration.DataDirectory))
        {
            throw new ArgumentException("数据目录不能为空。", nameof(configuration));
        }
    }

    private static void EnsureRequestId(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            throw new DomainRuleException(ErrorCodes.InvalidInput, "requestId 不能为空。");
        }
    }
}
