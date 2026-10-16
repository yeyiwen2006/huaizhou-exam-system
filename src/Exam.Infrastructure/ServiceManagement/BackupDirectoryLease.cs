namespace Exam.Infrastructure.ServiceManagement;

/// <summary>Serialize retention against built-in backup readers without locking PostgreSQL writes.</summary>
public static class BackupDirectoryLease
{
    public static Task<FileStream> ReadAsync(string root, CancellationToken token) => AcquireAsync(root, false, token);
    public static Task<FileStream> CleanupAsync(string root, CancellationToken token) => AcquireAsync(root, true, token);

    private static async Task<FileStream> AcquireAsync(string root, bool exclusive, CancellationToken token)
    {
        root = Path.GetFullPath(root);
        BackupRetentionPolicy.RejectLinks(root);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ".exam-backup-access.lock");
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            BackupRetentionPolicy.RejectLinks(path);
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    exclusive ? FileShare.None : FileShare.ReadWrite);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            { await Task.Delay(100, token); }
        }
    }
}
