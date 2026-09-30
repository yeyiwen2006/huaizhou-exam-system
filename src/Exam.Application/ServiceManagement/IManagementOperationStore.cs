using Exam.Contracts.ServiceManagement;

namespace Exam.Application.ServiceManagement;

public interface IManagementOperationStore
{
    // Exclusive across processes, retained until execution and final durable recording finish.
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);
    Task<ManagementOperation?> FindAsync(string requestId, CancellationToken cancellationToken);
    Task SaveAsync(ManagementOperation operation, CancellationToken cancellationToken);
    Task<bool> HasUnverifiedOperationsAsync(CancellationToken cancellationToken);
}
