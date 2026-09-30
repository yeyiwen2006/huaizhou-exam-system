using Exam.Application.ServiceManagement;
using Exam.Contracts.ServiceManagement;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>Checks explicitly selected local backup evidence without using the live database.</summary>
public sealed class BackupIntegrityStartupReadinessProbe(
    Func<PostgresBackupCoordinator> coordinatorFactory,
    string? directory,
    string? expectedBackupId) : IStartupReadinessProbe
{
    public async Task<IReadOnlyList<StartupCheckResult>> RunAsync(ServiceConfiguration configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(expectedBackupId))
            return [new("backup-integrity", false, "未指定待核验的备份目录及备份标识。", true)];
        try
        {
            await coordinatorFactory().VerifyAvailableBackupAsync(directory, expectedBackupId, cancellationToken);
            return [new("backup-integrity", true, "指定备份的数据库校验、资源及受保护材料摘要通过；仍须完成隔离恢复演练。", true)];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return [new("backup-integrity", false, "指定备份无法完成完整性核验，请检查受保护部署配置及备份材料。", true)];
        }
    }
}
