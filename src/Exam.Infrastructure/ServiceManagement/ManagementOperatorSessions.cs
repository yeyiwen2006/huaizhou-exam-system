using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Exam.Application.Security;
using Exam.Infrastructure.Postgres.Operators;
using Npgsql;

namespace Exam.Infrastructure.ServiceManagement;

/// <summary>
/// 服务管理程序的考务账号登录。数据库可用时以数据库中的考务账号为准，并刷新本机缓存；
/// 业务服务或数据库停止时，按本机缓存核对（只缓存已启用账号的口令哈希与管理员标记，
/// 以 Windows 本机加密保存）。Linux 仅使用数据库认证，不写入离线口令缓存。
/// 会话只在本进程内存中，退出登录或程序重启后失效。
/// </summary>
public sealed class ManagementOperatorSessions(
    Func<string?> connectionString,
    string cachePath,
    TimeSpan? databaseTimeout = null,
    Func<CancellationToken, Task<IReadOnlyList<CachedOperatorCredential>>>? loadFromDatabase = null)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Huaizhou.Exam.Management.OperatorCache.v1");
    private readonly ConcurrentDictionary<string, AuthenticatedOperator> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    /// <summary>登录成功返回会话令牌；用户名或口令不对、账号未启用时返回 null。</summary>
    public async Task<(string Token, AuthenticatedOperator Operator)?> LoginAsync(string? username, string? password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return null;
        var credentials = await TryRefreshFromDatabaseAsync(cancellationToken).ConfigureAwait(false)
            ?? await ReadCacheAsync(cancellationToken).ConfigureAwait(false);
        var key = Exam.Contracts.Operators.OperatorRules.Key(username);
        var match = credentials.FirstOrDefault(item => Exam.Contracts.Operators.OperatorRules.Key(item.Username) == key);
        if (match is null || !OperatorPasswordHasher.Verify(password, match.PasswordHash)) return null;
        var token = OperatorPasswordHasher.NewSessionToken();
        var session = new AuthenticatedOperator(match.Username, match.IsAdministrator);
        _sessions[OperatorPasswordHasher.TokenDigest(token)] = session;
        return (token, session);
    }

    public AuthenticatedOperator? Authenticate(string? token) =>
        string.IsNullOrWhiteSpace(token) ? null
            : _sessions.TryGetValue(OperatorPasswordHasher.TokenDigest(token), out var session) ? session : null;

    public bool Logout(string? token) =>
        !string.IsNullOrWhiteSpace(token) && _sessions.TryRemove(OperatorPasswordHasher.TokenDigest(token), out _);

    private async Task<IReadOnlyList<CachedOperatorCredential>?> TryRefreshFromDatabaseAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CachedOperatorCredential> credentials;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(databaseTimeout ?? TimeSpan.FromSeconds(5));
            credentials = await (loadFromDatabase ?? LoadFromPostgresAsync)(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // 数据库或业务服务停止、连接配置不可用：改用本机缓存。
            return null;
        }

        try { await WriteCacheAsync(credentials, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            // 缓存写入失败不影响本次以数据库为准的登录；下次登录再刷新。
        }
        return credentials;
    }

    private async Task<IReadOnlyList<CachedOperatorCredential>> LoadFromPostgresAsync(CancellationToken cancellationToken)
    {
        var connection = connectionString();
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("未配置数据库连接。");
        var builder = new NpgsqlConnectionStringBuilder(connection) { Timeout = 5 };
        await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        return await new PostgresOperatorAccounts(dataSource).ListActiveCredentialsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<CachedOperatorCredential>> ReadCacheAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return [];
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(cachePath)) return [];
            var plaintext = Unprotect(await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false));
            try { return JsonSerializer.Deserialize<CachedOperatorCredential[]>(plaintext) ?? []; }
            finally { Array.Clear(plaintext); }
        }
        catch (Exception error) when (error is CryptographicException or JsonException or IOException)
        {
            return [];
        }
        finally { _cacheGate.Release(); }
    }

    private async Task WriteCacheAsync(IReadOnlyList<CachedOperatorCredential> credentials, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return;
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(cachePath))!;
            Directory.CreateDirectory(directory);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(credentials);
            byte[] encrypted;
            try { encrypted = Protect(plaintext); }
            finally { Array.Clear(plaintext); }
            var temporary = Path.Combine(directory, $".operators.{Guid.NewGuid():N}.tmp");
            await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, cachePath, overwrite: true);
        }
        finally { _cacheGate.Release(); }
    }

    // 只缓存 PBKDF2 哈希；Windows 本机加密作为额外保护，防止缓存文件被直接复制到别处读取。
    private static byte[] Protect(byte[] plaintext) => OperatingSystem.IsWindows()
        ? ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.LocalMachine)
        : throw new PlatformNotSupportedException("服务管理程序只在 Windows 上运行。");

    private static byte[] Unprotect(byte[] encrypted) => OperatingSystem.IsWindows()
        ? ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.LocalMachine)
        : throw new PlatformNotSupportedException("服务管理程序只在 Windows 上运行。");
}
