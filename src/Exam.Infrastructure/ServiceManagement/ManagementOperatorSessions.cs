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
    Func<CancellationToken, Task<IReadOnlyList<CachedOperatorCredential>>>? loadFromDatabase = null,
    Func<string, CancellationToken, Task<AuthenticatedOperator?>>? authenticateBusinessSession = null,
    Func<string, CancellationToken, Task<AuthenticatedOperator?>>? authenticatePeerBusinessSession = null,
    Func<bool>? localBusinessSessionAllowed = null,
    TimeProvider? time = null,
    Func<string, string, CancellationToken, Task<AuthenticatedOperator?>>? authenticatePeerManagementSession = null)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Huaizhou.Exam.Management.OperatorCache.v1");
    private sealed record Session(AuthenticatedOperator Operator, CachedOperatorCredential? Credential, DateTimeOffset VerifiedAt);
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, CachedOperatorCredential>? _onlineCredentials;
    private static readonly TimeSpan OfflineLifetime = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

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
        var session = new AuthenticatedOperator(match.Username, match.IsAdministrator);
        return CreateSession(session, match);
    }

    /// <summary>Exchanges a currently valid business session for an independent management session, without a password or offline fallback.</summary>
    public async Task<(string Token, AuthenticatedOperator Operator)?> ExchangeBusinessSessionAsync(string? token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(databaseTimeout ?? TimeSpan.FromSeconds(5));
        AuthenticatedOperator? account = null;
        if (localBusinessSessionAllowed?.Invoke() != false)
        {
            try { account = await (authenticateBusinessSession ?? AuthenticateBusinessSessionFromPostgresAsync)(token, deadline.Token).ConfigureAwait(false); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
        }
        if (account is null && authenticatePeerBusinessSession is not null)
        {
            account = await authenticatePeerBusinessSession(token, cancellationToken).ConfigureAwait(false);
            if (account is null) return null;
            var managementToken = OperatorPasswordHasher.NewSessionToken();
            _sessions[OperatorPasswordHasher.TokenDigest(managementToken)] = new(account, null, _time.GetUtcNow());
            return (managementToken, account);
        }
        if (account is null) return null;
        return CreateSession(account, null);
    }

    public async Task<(string Token, AuthenticatedOperator Operator)?> ExchangeManagementSessionAsync(string sourceNodeId,
        string credential, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > 256 || authenticatePeerManagementSession is null) return null;
        var account = await authenticatePeerManagementSession(sourceNodeId, credential, token).ConfigureAwait(false);
        return account is null ? null : CreateSession(account, null);
    }

    private (string Token, AuthenticatedOperator Operator) CreateSession(AuthenticatedOperator account, CachedOperatorCredential? credential)
    {
        var token = OperatorPasswordHasher.NewSessionToken();
        _sessions[OperatorPasswordHasher.TokenDigest(token)] = new(account, credential, _time.GetUtcNow());
        return (token, account);
    }

    public AuthenticatedOperator? Authenticate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return null;
        var digest = OperatorPasswordHasher.TokenDigest(token);
        if (!_sessions.TryGetValue(digest, out var session)) return null;
        var known = Volatile.Read(ref _onlineCredentials);
        if (_time.GetUtcNow() - session.VerifiedAt > OfflineLifetime ||
            session.Credential is not null && known is not null && (!known.TryGetValue(Exam.Contracts.Operators.OperatorRules.Key(session.Operator.Username), out var credential) ||
            credential != session.Credential))
        { _sessions.TryRemove(digest, out _); return null; }
        return session.Operator;
    }

    // Login/exchange establishes an independent management session. Refreshing the
    // page must not re-authenticate against a database or the active business node.
    public Task<AuthenticatedOperator?> AuthenticateAsync(string? token, CancellationToken cancellationToken = default) =>
        Task.FromResult(Authenticate(token));

    public bool Logout(string? token) =>
        !string.IsNullOrWhiteSpace(token) && _sessions.TryRemove(OperatorPasswordHasher.TokenDigest(token), out _);

    private async Task<IReadOnlyList<CachedOperatorCredential>?> TryRefreshFromDatabaseAsync(CancellationToken cancellationToken)
    {
        // Publish account snapshots in read order. A slower, older read must never
        // overwrite a revocation that another request has already observed.
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await RefreshFromDatabaseAsync(cancellationToken).ConfigureAwait(false); }
        finally { _refreshGate.Release(); }
    }

    private async Task<IReadOnlyList<CachedOperatorCredential>?> RefreshFromDatabaseAsync(CancellationToken cancellationToken)
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

        var current = credentials.ToDictionary(item => Exam.Contracts.Operators.OperatorRules.Key(item.Username), StringComparer.Ordinal);
        Volatile.Write(ref _onlineCredentials, current);
        foreach (var pair in _sessions)
            if (pair.Value.Credential is not null && (!current.TryGetValue(Exam.Contracts.Operators.OperatorRules.Key(pair.Value.Operator.Username), out var credential) ||
                credential != pair.Value.Credential)) _sessions.TryRemove(pair.Key, out _);
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

    private async Task<AuthenticatedOperator?> AuthenticateBusinessSessionFromPostgresAsync(string token, CancellationToken cancellationToken)
    {
        var connection = connectionString();
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("未配置数据库连接。");
        var builder = new NpgsqlConnectionStringBuilder(connection) { Timeout = 5 };
        await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        return await new PostgresOperatorAccounts(dataSource).AuthenticateAsync(token, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<CachedOperatorCredential>> ReadCacheAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return [];
        await _cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(cachePath) || DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(cachePath) > OfflineLifetime) return [];
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
