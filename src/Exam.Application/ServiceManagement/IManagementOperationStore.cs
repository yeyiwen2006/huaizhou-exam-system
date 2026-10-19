using Exam.Contracts.ServiceManagement;

namespace Exam.Application.ServiceManagement;

public interface IManagementOperationStore
{
    // Exclusive across processes, retained until execution and final durable recording finish.
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken);
    Task<ManagementOperation?> FindAsync(string requestId, CancellationToken cancellationToken);
    Task SaveAsync(ManagementOperation operation, CancellationToken cancellationToken);
    Task<bool> HasUnverifiedOperationsAsync(CancellationToken cancellationToken);
    Task RequestCancellationAsync(string requestId, string actorId, CancellationToken token) =>
        Task.FromException(new NotSupportedException("此操作记录存储不支持取消。"));
    Task<bool> IsCancellationRequestedAsync(string requestId, CancellationToken token) => Task.FromResult(false);
    // Called under the journal lease before resuming a completed HA task's remaining startup.
    Task<bool> HasLaterBusinessStopAsync(ManagementOperation operation, CancellationToken token) => Task.FromResult(false);
}
