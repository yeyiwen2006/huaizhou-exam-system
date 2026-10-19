using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Operators;
using Exam.Infrastructure.Postgres.Operators;

namespace Exam.Infrastructure.ServiceManagement;

public sealed partial class ManagementOperatorSessions
{
    private sealed record ExchangedCredential(CachedOperatorCredential Credential, DateTimeOffset VerifiedAt);

    private string ExchangedCredentialPath(string key) => Path.Combine(cachePath + ".sessions",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".cache");

    private async Task<CachedOperatorCredential?> ReadCacheAsync(string key, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return null;
        await _cacheGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            CachedOperatorCredential? snapshotCredential = null;
            DateTimeOffset? snapshotAt = null;
            if (File.Exists(cachePath) && _time.GetUtcNow() - File.GetLastWriteTimeUtc(cachePath) <= OfflineLifetime)
            {
                snapshotAt = File.GetLastWriteTimeUtc(cachePath);
                var snapshot = await ReadProtectedCacheAsync<CachedOperatorCredential[]>(cachePath, token).ConfigureAwait(false);
                snapshotCredential = snapshot?.FirstOrDefault(item => OperatorRules.Key(item.Username) == key);
            }
            var known = Volatile.Read(ref _onlineCredentials);
            if (known is not null && _time.GetUtcNow() - known.ObservedAt <= OfflineLifetime &&
                (snapshotAt is null || known.ObservedAt >= snapshotAt.Value))
            {
                snapshotAt = known.ObservedAt;
                snapshotCredential = known.Credentials.GetValueOrDefault(key);
            }
            var exchanged = await ReadExchangedCredentialAsync(key, token).ConfigureAwait(false);
            // A newer full snapshot can explicitly revoke an older exchanged account.
            return exchanged is not null && (snapshotAt is null || exchanged.VerifiedAt > snapshotAt.Value)
                ? exchanged.Credential : snapshotCredential;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        { return null; }
        finally { _cacheGate.Release(); }
    }

    private async Task<ExchangedCredential?> ReadExchangedCredentialAsync(string key, CancellationToken token)
    {
        var path = ExchangedCredentialPath(key);
        if (!File.Exists(path)) return null;
        try
        {
            var value = await ReadProtectedCacheAsync<ExchangedCredential>(path, token).ConfigureAwait(false);
            return value is not null && OperatorRules.Key(value.Credential.Username) == key &&
                _time.GetUtcNow() - value.VerifiedAt <= OfflineLifetime ? value : null;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        { return null; }
    }

    private async Task WriteExchangedCredentialAsync(CachedOperatorCredential credential, DateTimeOffset verifiedAt, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return;
        var key = OperatorRules.Key(credential.Username);
        await _cacheGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var previous = await ReadExchangedCredentialAsync(key, token).ConfigureAwait(false);
            if (previous is not null && previous.VerifiedAt > verifiedAt) return;
            await WriteProtectedCacheAsync(ExchangedCredentialPath(key), new ExchangedCredential(credential, verifiedAt), token).ConfigureAwait(false);
        }
        finally { _cacheGate.Release(); }
    }

    private async Task WriteCacheAsync(IReadOnlyList<CachedOperatorCredential> credentials, DateTimeOffset observedAt, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return;
        await _cacheGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await WriteProtectedCacheAsync(cachePath, credentials, token).ConfigureAwait(false);
            // Retain the original snapshot format, including its timestamp-based expiry.
            File.SetLastWriteTimeUtc(cachePath, observedAt.UtcDateTime);
        }
        finally { _cacheGate.Release(); }
    }

    private static async Task<T?> ReadProtectedCacheAsync<T>(string path, CancellationToken token)
    {
        var plaintext = Unprotect(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false));
        try { return JsonSerializer.Deserialize<T>(plaintext); }
        finally { Array.Clear(plaintext); }
    }

    private static async Task WriteProtectedCacheAsync<T>(string path, T value, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] encrypted;
        try { encrypted = Protect(plaintext); }
        finally { Array.Clear(plaintext); }
        var temporary = Path.Combine(directory, $".operators.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, encrypted, token).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
