using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
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
public sealed partial class ManagementOperatorSessions(
    Func<string?> connectionString,
    string cachePath,
    TimeSpan? databaseTimeout = null,
    Func<CancellationToken, Task<IReadOnlyList<CachedOperatorCredential>>>? loadFromDatabase = null,
    Func<string, CancellationToken, Task<AuthenticatedOperator?>>? authenticateBusinessSession = null,
    Func<string, CancellationToken, Task<AuthenticatedOperator?>>? authenticatePeerBusinessSession = null,
    Func<bool>? localBusinessSessionAllowed = null,
    TimeProvider? time = null,
    Func<string, string, CancellationToken, Task<AuthenticatedOperator?>>? authenticatePeerManagementSession = null,
    Func<string, CancellationToken, Task<CachedOperatorCredential?>>? authenticateBusinessCredential = null)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Huaizhou.Exam.Management.OperatorCache.v1");
    private sealed record Session(AuthenticatedOperator Operator, CachedOperatorCredential? Credential, DateTimeOffset VerifiedAt, long Observation);
    private sealed record CredentialSnapshot(IReadOnlyDictionary<string, CachedOperatorCredential> Credentials, DateTimeOffset ObservedAt, long Observation);
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly object _identityGate = new();
    private CredentialSnapshot? _onlineCredentials;
    private long _observation;
    private static readonly TimeSpan OfflineLifetime = TimeSpan.FromHours(24);
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>登录成功返回会话令牌；用户名或口令不对、账号未启用时返回 null。</summary>
    public async Task<(string Token, AuthenticatedOperator Operator)?> LoginAsync(string? username, string? password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return null;
        // Keep the password check and issuance in the same refresh order. Another
        // login cannot publish a revocation while this one is using its old snapshot.
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var credentials = await RefreshFromDatabaseAsync(cancellationToken).ConfigureAwait(false);
            var key = Exam.Contracts.Operators.OperatorRules.Key(username);
            var match = credentials is null ? await ReadCacheAsync(key, cancellationToken).ConfigureAwait(false)
                : credentials.FirstOrDefault(item => Exam.Contracts.Operators.OperatorRules.Key(item.Username) == key);
            if (match is null || !OperatorPasswordHasher.Verify(password, match.PasswordHash)) return null;
            return CreateSession(new(match.Username, match.IsAdministrator), match, Interlocked.Read(ref _observation));
        }
        finally { _refreshGate.Release(); }
    }

    /// <summary>Exchanges a currently valid business session for an independent management session, without a password or offline fallback.</summary>
    public async Task<(string Token, AuthenticatedOperator Operator)?> ExchangeBusinessSessionAsync(string? token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(databaseTimeout ?? TimeSpan.FromSeconds(5));
        AuthenticatedOperator? account = null;
        CachedOperatorCredential? credential = null;
        Exception? localFailure = null;
        var observation = Interlocked.Increment(ref _observation);
        var verifiedAt = _time.GetUtcNow();
        if (localBusinessSessionAllowed?.Invoke() != false)
        {
            try
            {
                if (authenticateBusinessSession is not null)
                    account = await authenticateBusinessSession(token, deadline.Token).ConfigureAwait(false);
                else
                {
                    credential = await (authenticateBusinessCredential ?? AuthenticateBusinessCredentialFromPostgresAsync)(token, deadline.Token).ConfigureAwait(false);
                    if (credential is not null) account = new(credential.Username, credential.IsAdministrator);
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested) { localFailure = error; }
        }
        if (account is null && authenticatePeerBusinessSession is not null)
        {
            observation = Interlocked.Increment(ref _observation);
            account = await authenticatePeerBusinessSession(token, cancellationToken).ConfigureAwait(false);
        }
        if (account is null)
        {
            if (localFailure is not null) ExceptionDispatchInfo.Capture(localFailure).Throw();
            return null;
        }
        var session = CreateSession(account, credential, observation);
        if (session is not null && credential is not null)
        {
            try { await WriteExchangedCredentialAsync(credential, verifiedAt, cancellationToken).ConfigureAwait(false); }
            catch (Exception) { /* Offline cache maintenance must not reject an authenticated session. */ }
        }
        return session;
    }

    public async Task<(string Token, AuthenticatedOperator Operator)?> ExchangeManagementSessionAsync(string sourceNodeId,
        string credential, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > 256 || authenticatePeerManagementSession is null) return null;
        var observation = Interlocked.Increment(ref _observation);
        var account = await authenticatePeerManagementSession(sourceNodeId, credential, token).ConfigureAwait(false);
        return account is null ? null : CreateSession(account, null, observation);
    }

    private (string Token, AuthenticatedOperator Operator)? CreateSession(AuthenticatedOperator account, CachedOperatorCredential? credential, long observation)
    {
        lock (_identityGate)
        {
            if (_onlineCredentials is { } known && known.Observation > observation &&
                !MatchesSnapshot(account, credential, known.Credentials)) return null;
            var token = OperatorPasswordHasher.NewSessionToken();
            _sessions[OperatorPasswordHasher.TokenDigest(token)] = new(account, credential, _time.GetUtcNow(), observation);
            return (token, account);
        }
    }

    private static bool MatchesSnapshot(AuthenticatedOperator account, CachedOperatorCredential? original,
        IReadOnlyDictionary<string, CachedOperatorCredential> current) =>
        current.TryGetValue(Exam.Contracts.Operators.OperatorRules.Key(account.Username), out var credential) &&
        credential.IsAdministrator == account.IsAdministrator && (original is null || original == credential);

    public AuthenticatedOperator? Authenticate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return null;
        var digest = OperatorPasswordHasher.TokenDigest(token);
        if (!_sessions.TryGetValue(digest, out var session)) return null;
        if (_time.GetUtcNow() - session.VerifiedAt > OfflineLifetime)
        { _sessions.TryRemove(digest, out _); return null; }
        return session.Operator;
    }

    // Login/exchange establishes an independent management session. Refreshing the
    // page must not re-authenticate against a database or the active business node.
    public Task<AuthenticatedOperator?> AuthenticateAsync(string? token, CancellationToken cancellationToken = default) =>
        Task.FromResult(Authenticate(token));

    public bool Logout(string? token) =>
        !string.IsNullOrWhiteSpace(token) && _sessions.TryRemove(OperatorPasswordHasher.TokenDigest(token), out _);

    private async Task<IReadOnlyList<CachedOperatorCredential>?> RefreshFromDatabaseAsync(CancellationToken cancellationToken)
    {
        var observation = Interlocked.Increment(ref _observation);
        var observedAt = _time.GetUtcNow();
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
        lock (_identityGate)
        {
            Volatile.Write(ref _onlineCredentials, new(current, observedAt, observation));
            foreach (var pair in _sessions)
                if (pair.Value.Observation < observation && !MatchesSnapshot(pair.Value.Operator, pair.Value.Credential, current))
                    _sessions.TryRemove(pair.Key, out _);
        }
        try { await WriteCacheAsync(credentials, observedAt, cancellationToken).ConfigureAwait(false); }
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

    private async Task<CachedOperatorCredential?> AuthenticateBusinessCredentialFromPostgresAsync(string token, CancellationToken cancellationToken)
    {
        var connection = connectionString();
        if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("未配置数据库连接。");
        var builder = new NpgsqlConnectionStringBuilder(connection) { Timeout = 5 };
        await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
        return await new PostgresOperatorAccounts(dataSource).AuthenticateManagementCredentialAsync(token, cancellationToken).ConfigureAwait(false);
    }

    // 只缓存 PBKDF2 哈希；Windows 本机加密作为额外保护，防止缓存文件被直接复制到别处读取。
    private static byte[] Protect(byte[] plaintext) => OperatingSystem.IsWindows()
        ? ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.LocalMachine)
        : throw new PlatformNotSupportedException("服务管理程序只在 Windows 上运行。");

    private static byte[] Unprotect(byte[] encrypted) => OperatingSystem.IsWindows()
        ? ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.LocalMachine)
        : throw new PlatformNotSupportedException("服务管理程序只在 Windows 上运行。");
}
