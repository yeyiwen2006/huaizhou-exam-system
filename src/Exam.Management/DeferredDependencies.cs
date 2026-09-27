using Exam.Application.ServiceManagement;
using Exam.Contracts.ServiceManagement;

namespace Exam.Management;

internal sealed class DeferredReadinessProbe(Func<IStartupReadinessProbe> load) : IStartupReadinessProbe
{
    public Task<IReadOnlyList<StartupCheckResult>> RunAsync(ServiceConfiguration configuration, CancellationToken token) =>
        load().RunAsync(configuration, token);
}

internal sealed class DeferredBusinessHost(Func<IManagedBusinessHost> load) : IManagedBusinessHost
{
    public BusinessServiceStatus Status
    {
        get { try { return load().Status; } catch (Exception) { return BusinessServiceStatus.Faulted; } }
    }
    public Task StartAsync(CancellationToken cancellationToken) => load().StartAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => load().StopAsync(cancellationToken);
}

internal sealed class DeferredConfigurationStore(Func<IServiceConfigurationStore> load) : IServiceConfigurationStore
{
    public Task<ServiceConfiguration> LoadAsync(CancellationToken cancellationToken = default) => load().LoadAsync(cancellationToken);
    public Task<ServiceConfiguration> SaveAsync(ServiceConfiguration requested, long expectedVersion, CancellationToken cancellationToken = default) =>
        load().SaveAsync(requested, expectedVersion, cancellationToken);
}

internal sealed class DeferredBackupCoordinator(Func<IBackupCoordinator> load) : IBackupCoordinator, IBackupOperationEvidence
{
    public Task<BackupResult> CreateAsync(BackupRequest request, CancellationToken cancellationToken) => load().CreateAsync(request, cancellationToken);
    public Task RestoreAsync(RestoreRequest request, CancellationToken cancellationToken) => load().RestoreAsync(request, cancellationToken);
    public Task<BackupResult?> FindCompletedBackupAsync(BackupRequest request, CancellationToken cancellationToken) =>
        load() is IBackupOperationEvidence evidence ? evidence.FindCompletedBackupAsync(request, cancellationToken) : Task.FromResult<BackupResult?>(null);
    public Task<bool> FindStagedRestoreAsync(RestoreRequest request, CancellationToken cancellationToken) =>
        load() is IBackupOperationEvidence evidence ? evidence.FindStagedRestoreAsync(request, cancellationToken) : Task.FromResult(false);
}

internal sealed class DeferredOperationStore(Func<IManagementOperationStore> load) : IManagementOperationStore
{
    public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken) => load().AcquireAsync(cancellationToken);
    public Task<ManagementOperation?> FindAsync(string requestId, CancellationToken cancellationToken) => load().FindAsync(requestId, cancellationToken);
    public Task SaveAsync(ManagementOperation operation, CancellationToken cancellationToken) => load().SaveAsync(operation, cancellationToken);
    public Task<bool> HasUnverifiedOperationsAsync(CancellationToken cancellationToken) => load().HasUnverifiedOperationsAsync(cancellationToken);
}
