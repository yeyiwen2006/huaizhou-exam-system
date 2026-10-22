using Exam.Infrastructure.ServiceManagement;

namespace Exam.Infrastructure.HighAvailability;

internal sealed class ReplicaResourceExchange(string target, string owner)
{
    internal sealed record Intent(string Id, string Digest, string PreviousDigest, string Owner, string? RetiredId, bool Committed);
    private string Journal => target + ".replica-swap.json";
    private string Staged(string id) => Owned(".incoming-", id);
    private string Previous(string id) => Owned(".replica-previous-", id);
    private string Owned(string kind, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new IOException("资源交换记录标识无效。");
        return PathBoundary.ResolveWithinRoot(Path.GetDirectoryName(target)!, target + kind + id, "资源交换路径越界。");
    }
    internal bool Holds(string id) => DurableJsonFile.Read<Intent>(Journal, 8192) is { Committed: false } pending && pending.Id == id;

    internal async Task RecoverAsync(Func<CancellationToken, Task> verify, CancellationToken token)
    {
        var intent = DurableJsonFile.Read<Intent>(Journal, 8192);
        if (intent is null) return;
        var staged = Staged(intent.Id); var previous = Previous(intent.Id);
        if (intent.Committed && intent.Owner != owner) return;
        if (intent.Owner != owner || intent.Digest.Length != 64 || intent.PreviousDigest.Length != 64)
            throw new IOException("未决资源交换属于其他节点身份或代次。");
        if (intent.Committed) { Prune(intent); return; }
        await verify(token);
        if (Directory.Exists(target) && FileConfirmationStore.ResourcesDigest(target) == intent.Digest)
        {
            await CommitAsync(intent, token); return;
        }
        if (Directory.Exists(staged) && FileConfirmationStore.ResourcesDigest(staged, persist: true) == intent.Digest)
        {
            var hasCurrent = Directory.Exists(target);
            if (hasCurrent)
            {
                if (Directory.Exists(previous) || FileConfirmationStore.ResourcesDigest(target) != intent.PreviousDigest)
                    throw new IOException("资源交换的原目录证据不一致。");
            }
            else if (!Directory.Exists(previous) || FileConfirmationStore.ResourcesDigest(previous) != intent.PreviousDigest)
                throw new IOException("资源交换的原件不可核实。");
            await verify(token);
            token.ThrowIfCancellationRequested();
            // Keep the current directory in place while the final role check can still fail.
            // Once it passes, complete both renames without an asynchronous cancellation gap.
            if (hasCurrent) ReplicaResourceTransfer.MoveDirectory(target, previous);
            ReplicaResourceTransfer.MoveDirectory(staged, target);
            await CommitAsync(intent, token);
            return;
        }
        // A failed staging write, or a completed rollback whose journal deletion was
        // interrupted, leaves the exact original in place. Release that intent for retry.
        if (Directory.Exists(target) && !Directory.Exists(previous) &&
            FileConfirmationStore.ResourcesDigest(target) == intent.PreviousDigest)
        {
            await verify(token); token.ThrowIfCancellationRequested();
            File.Delete(Journal);
            return;
        }
        // Roll back only the exact original that this intent moved. Never guess by time.
        if (!Directory.Exists(target) && Directory.Exists(previous) && FileConfirmationStore.ResourcesDigest(previous) == intent.PreviousDigest)
        {
            await verify(token);
            token.ThrowIfCancellationRequested();
            ReplicaResourceTransfer.MoveDirectory(previous, target);
            File.Delete(Journal);
            return;
        }
        throw new IOException("资源交换材料不完整，保留原件并等待核对。");
    }

    internal async Task ExchangeAsync(string id, string digest, string previousDigest,
        Func<CancellationToken, Task> verify, CancellationToken token)
    {
        var prior = DurableJsonFile.Read<Intent>(Journal, 8192);
        if (prior is { Committed: false }) throw new IOException("存在未决资源交换。");
        if (prior is not null && prior.Owner != owner) prior = null;
        var intent = new Intent(id, digest, previousDigest, owner, prior?.Id, false);
        await DurableJsonFile.WriteAsync(Journal, intent, token);
        await verify(token); token.ThrowIfCancellationRequested();
        ReplicaResourceTransfer.MoveDirectory(target, Previous(id));
        ReplicaResourceTransfer.MoveDirectory(Staged(id), target);
        await CommitAsync(intent, token);
    }

    private async Task CommitAsync(Intent intent, CancellationToken token)
    {
        await DurableJsonFile.WriteAsync(Journal, intent with { Committed = true }, token);
        Prune(intent);
    }
    private void Prune(Intent intent)
    {
        if (intent.RetiredId is null || intent.RetiredId == intent.Id) return;
        var retired = Previous(intent.RetiredId);
        if (!Directory.Exists(target)) throw new IOException("当前资源缺失，禁止清理历史。");
        // Only this journal's superseded replica copy is eligible. Quarantined original
        // databases, unknown legacy directories and an unresolved exchange are never removed.
        if (Directory.Exists(retired))
        {
            RejectLinks(retired);
            Directory.Delete(retired, true);
        }
    }
    private static void RejectLinks(string root)
    {
        PathBoundary.RejectExistingReparsePoints(root, root);
        foreach (var child in Directory.EnumerateFileSystemEntries(root))
        {
            PathBoundary.RejectExistingReparsePoints(root, child);
            if (Directory.Exists(child)) RejectLinks(child);
        }
    }
}
