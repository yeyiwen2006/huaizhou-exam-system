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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, BackupResult> _completedBackups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RestoreResult> _completedRestores = new(StringComparer.Ordinal);
    private ServiceConfiguration _configuration;
    private IReadOnlyList<StartupCheckResult> _checks = [];
    private string? _lastError;
    private BusinessServiceStatus _status;

    public ServiceManagementService(
        ServiceConfiguration configuration,
        IManagedBusinessHost host,
        IStartupReadinessProbe readiness,
        IBackupCoordinator backups,
        IClock clock)
    {
        Validate(configuration);
        _configuration = configuration;
        _host = host;
        _readiness = readiness;
        _backups = backups;
        _clock = clock;
        _status = host.Status;
    }

    public ServiceStatusSnapshot GetStatus() =>
        new(_status, _configuration, _checks, _clock.UtcNow, _lastError);

    public async Task<ServiceConfiguration> ConfigureAsync(
        ServiceConfiguration requested,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_status is not BusinessServiceStatus.Stopped and not BusinessServiceStatus.Faulted)
            {
                throw new DomainRuleException(ErrorCodes.VersionConflict, "业务服务运行时不能修改本机服务配置。");
            }

            if (_configuration.Version != expectedVersion)
            {
                throw new DomainRuleException(ErrorCodes.VersionConflict, "服务配置版本已变化，请刷新后重试。");
            }

            Validate(requested);
            _configuration = requested with { Version = expectedVersion + 1 };
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
            if (_status == BusinessServiceStatus.Running)
            {
                return GetStatus();
            }

            _status = BusinessServiceStatus.Recovering;
            _checks = Array.AsReadOnly((await _readiness.RunAsync(_configuration, cancellationToken)).ToArray());
            var blocking = _checks.Where(check => !check.Passed && check.BlocksWrites).ToArray();
            if (blocking.Length > 0)
            {
                _lastError = string.Join("；", blocking.Select(check => $"{check.Name}: {check.Message}"));
                throw new DomainRuleException(ErrorCodes.ServiceRecovering, _lastError, retryable: true);
            }

            try
            {
                _status = BusinessServiceStatus.Starting;
                await _host.StartAsync(cancellationToken);
                _status = BusinessServiceStatus.Running;
                _lastError = null;
                return GetStatus();
            }
            catch (Exception ex) when (ex is not DomainRuleException)
            {
                _status = BusinessServiceStatus.Faulted;
                _lastError = ex.Message;
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
            if (_status == BusinessServiceStatus.Stopped)
            {
                return GetStatus();
            }

            try
            {
                _status = BusinessServiceStatus.Stopping;
                await _host.StopAsync(cancellationToken);
                _status = BusinessServiceStatus.Stopped;
                return GetStatus();
            }
            catch (Exception ex) when (ex is not DomainRuleException)
            {
                _status = BusinessServiceStatus.Faulted;
                _lastError = ex.Message;
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
                return previous;
            }

            var result = await _backups.CreateAsync(request, cancellationToken);
            if (!result.IncludesDatabase || !result.IncludesProtectedConfiguration || !result.IncludesVersionedResources)
            {
                throw new DomainRuleException(ErrorCodes.PersistenceFailed, "备份不完整，不能标记为可恢复备份。");
            }

            _completedBackups.Add(request.RequestId, result);
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
                return previous;
            }

            if (_status != BusinessServiceStatus.Stopped)
            {
                throw new DomainRuleException(ErrorCodes.VersionConflict, "恢复前必须停止业务服务。");
            }

            await _backups.RestoreAsync(request, cancellationToken);
            _checks = Array.AsReadOnly((await _readiness.RunAsync(_configuration, cancellationToken)).ToArray());
            if (_checks.Any(check => !check.Passed && check.BlocksWrites))
            {
                throw new DomainRuleException(ErrorCodes.ServiceRecovering, "恢复数据未通过启动检查，业务写入保持关闭。", retryable: true);
            }

            var result = new RestoreResult(request.BackupId, _clock.UtcNow, _checks);
            _completedRestores.Add(request.RequestId, result);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Validate(ServiceConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(configuration.ServerId) || string.IsNullOrWhiteSpace(configuration.NodeId))
        {
            throw new ArgumentException("serverId 和 nodeId 不能为空。", nameof(configuration));
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
