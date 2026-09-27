using Exam.Contracts.ServiceManagement;

namespace Exam.Application.ServiceManagement;

/// <summary>Read-only observation of existing local backup/staging evidence; never repeats a backup or restore.</summary>
public interface IBackupOperationEvidence
{
    Task<BackupResult?> FindCompletedBackupAsync(BackupRequest request, CancellationToken cancellationToken);
    Task<bool> FindStagedRestoreAsync(RestoreRequest request, CancellationToken cancellationToken);
}
