using Exam.Infrastructure.ServiceManagement;

namespace Exam.Infrastructure.HighAvailability;

/// <summary>
/// Reuses verified file contents while names/metadata and the directory change notification
/// remain unchanged. Startup, failover and restore retain their independent full checks.
/// </summary>
internal sealed class ResourceDirectoryDigestCache(string root, Func<string, string> compute,
    int maximumEntries = ReplicaResourceTransfer.MaximumEntries) : IDisposable
{
    private sealed record Entry(string Path, long Length, long Created, long Written, FileAttributes Attributes);
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private string? _rootIdentity;
    private long _changes;
    private long _verifiedChanges = -1;
    private bool _watcherFailed;
    private Entry[]? _entries;
    private string? _digest;
    private bool _disposed;

    public ResourceDirectoryDigestCache(string root)
        : this(root, path => FileConfirmationStore.ResourcesDigest(path, persist: true)) { }

    internal long ChangesObserved => Interlocked.Read(ref _changes);

    public string Read()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureWatcher();
            var changes = ChangesObserved;
            var before = Inventory();
            if (_digest is not null && changes == _verifiedChanges && before.SequenceEqual(_entries!) &&
                ChangesObserved == changes && !Volatile.Read(ref _watcherFailed))
                return _digest;

            var digest = compute(root);
            if (!before.SequenceEqual(Inventory()))
                throw new IOException("资源目录在读取期间发生变化。");
            // Notifications invalidate cached contents, not a complete read. Keep the starting
            // count so a delayed notification still forces the next read to refresh.
            _digest = digest;
            _entries = before;
            _verifiedChanges = changes;
            return digest;
        }
    }

    private void EnsureWatcher()
    {
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new IOException("资源目录不可读。");
        PathBoundary.RejectExistingReparsePoints(root, root);
        var identity = ResourceDirectoryIdentity.Read(root);
        if (_watcher is not null && !Volatile.Read(ref _watcherFailed) && _rootIdentity == identity) return;
        _watcher?.Dispose();
        _watcher = null;
        _digest = null;
        Volatile.Write(ref _watcherFailed, false);
        if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new IOException("资源目录不可读。");
        PathBoundary.RejectExistingReparsePoints(root, root);
        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                           NotifyFilters.CreationTime | NotifyFilters.Size | NotifyFilters.Security
        };
        watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed;
        watcher.Renamed += (_, _) => Interlocked.Increment(ref _changes);
        watcher.Error += (_, _) => { Volatile.Write(ref _watcherFailed, true); Interlocked.Increment(ref _changes); };
        try
        {
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            _rootIdentity = identity;
        }
        catch { watcher.Dispose(); throw; }
    }

    private void Changed(object sender, FileSystemEventArgs args) => Interlocked.Increment(ref _changes);

    private Entry[] Inventory()
    {
        if (!Directory.Exists(root)) throw new IOException("资源目录不可读。");
        var entries = new List<Entry>();
        void Visit(string path)
        {
            // Inventory includes the root sentinel; the wire limit counts only children.
            if (entries.Count > maximumEntries) throw new IOException("资源清单超过容量限制。");
            PathBoundary.RejectExistingReparsePoints(root, path);
            var attributes = File.GetAttributes(path);
            var directory = (attributes & FileAttributes.Directory) != 0;
            FileSystemInfo info = directory ? new DirectoryInfo(path) : new FileInfo(path);
            entries.Add(new(Path.GetRelativePath(root, path), directory ? -1 : ((FileInfo)info).Length,
                info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks, attributes));
            if (directory)
                foreach (var child in Directory.EnumerateFileSystemEntries(path).Order(StringComparer.Ordinal)) Visit(child);
        }
        Visit(root);
        return entries.ToArray();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _watcher?.Dispose();
            _watcher = null;
        }
    }
}
