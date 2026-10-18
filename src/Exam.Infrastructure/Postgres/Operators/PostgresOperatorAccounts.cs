using System.Data;
using Exam.Application.Participation;
using Exam.Application.Security;
using Exam.Contracts.Operators;
using Exam.Contracts.Participation;
using Npgsql;

namespace Exam.Infrastructure.Postgres.Operators;

/// <summary>
/// 考务账号与会话。第一个注册的账号直接启用并成为管理员（以事务级锁串行判断），
/// 之后注册的账号为待审批。会话不自动过期，只在退出登录或账号停用时撤销。
/// </summary>
public sealed class PostgresOperatorAccounts(NpgsqlDataSource dataSource) : IOperatorAuthenticator
{
    private const string Columns = "username, display_name, status, is_administrator, created_at, approved_by";

    public async ValueTask<RegisterOperatorResult> RegisterAsync(RegisterOperatorRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var error = OperatorRules.DescribeUsername(request.Username) ?? OperatorRules.DescribePassword(request.Password);
        if (error is null && (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 64))
            error = "姓名不能为空，且不超过 64 个字符。";
        if (error is not null) throw Rule(ParticipationErrorCodes.InvalidRequest, error);

        var hash = OperatorPasswordHasher.Hash(request.Password);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        // 串行化“是否为第一个账号”的判断，避免并发注册出现两个自动启用的管理员。
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('operator-registration', 0));", connection, transaction))
            await gate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        bool first;
        await using (var any = new NpgsqlCommand("SELECT NOT EXISTS (SELECT 1 FROM exam.operator_accounts);", connection, transaction))
            first = (bool)(await any.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        try
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO exam.operator_accounts
                    (username_key, username, display_name, password_hash, status, is_administrator,
                     created_at, approved_by, approved_at, updated_at)
                VALUES (@key, @username, @display, @hash, @status, @admin, clock_timestamp(),
                        CASE WHEN @admin THEN '首个账号' END, CASE WHEN @admin THEN clock_timestamp() END, clock_timestamp());
                """, connection, transaction);
            insert.Parameters.AddWithValue("key", OperatorRules.Key(request.Username));
            insert.Parameters.AddWithValue("username", request.Username.Trim());
            insert.Parameters.AddWithValue("display", request.DisplayName.Trim());
            insert.Parameters.AddWithValue("hash", hash);
            insert.Parameters.AddWithValue("status", first ? "active" : "pending");
            insert.Parameters.AddWithValue("admin", first);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException failure) when (failure.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw Rule(OperatorErrorCodes.UsernameTaken, "该用户名已被注册，请换一个用户名。");
        }
        var account = await FindAsync(connection, transaction, request.Username, cancellationToken).ConfigureAwait(false)
            ?? throw new DataException("注册后未找到账号。");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RegisterOperatorResult(account);
    }

    public async ValueTask<OperatorSession> LoginAsync(LoginOperatorRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        string? hash = null;
        await using (var lookup = new NpgsqlCommand(
            "SELECT password_hash FROM exam.operator_accounts WHERE username_key = @key;", connection))
        {
            lookup.Parameters.AddWithValue("key", OperatorRules.Key(request.Username ?? string.Empty));
            hash = await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        }
        // 用户名不存在时仍做一次哈希计算，避免通过响应时间判断用户名是否存在。
        var verified = OperatorPasswordHasher.Verify(request.Password, hash ?? DummyHash);
        if (hash is null || !verified) throw Rule(OperatorErrorCodes.InvalidCredentials, "用户名或口令不正确。");
        var account = await FindAsync(connection, null, request.Username!, cancellationToken).ConfigureAwait(false)
            ?? throw Rule(OperatorErrorCodes.InvalidCredentials, "用户名或口令不正确。");
        EnsureActive(account);

        var token = OperatorPasswordHasher.NewSessionToken();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO exam.operator_sessions (token_digest, username_key, created_at)
            VALUES (@digest, @key, clock_timestamp());
            """, connection))
        {
            insert.Parameters.AddWithValue("digest", OperatorPasswordHasher.TokenDigest(token));
            insert.Parameters.AddWithValue("key", OperatorRules.Key(account.Username));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        return new OperatorSession(token, account);
    }

    public async ValueTask<AuthenticatedOperator?> AuthenticateAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return null;
        await using var command = dataSource.CreateCommand("""
            SELECT a.username, a.is_administrator
              FROM exam.operator_sessions s
              JOIN exam.operator_accounts a ON a.username_key = s.username_key
             WHERE s.token_digest = @digest AND s.revoked_at IS NULL AND a.status = 'active';
            """);
        command.Parameters.AddWithValue("digest", OperatorPasswordHasher.TokenDigest(token));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new AuthenticatedOperator(reader.GetString(0), reader.GetBoolean(1))
            : null;
    }

    public async ValueTask<bool> LogoutAsync(string token, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE exam.operator_sessions SET revoked_at = clock_timestamp() WHERE token_digest = @digest AND revoked_at IS NULL;");
        command.Parameters.AddWithValue("digest", OperatorPasswordHasher.TokenDigest(token));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async ValueTask<OperatorAccount> GetAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await FindAsync(connection, null, username, cancellationToken).ConfigureAwait(false)
            ?? throw Rule(ParticipationErrorCodes.SessionNotFound, "找不到该考务账号。");
    }

    public async ValueTask<IReadOnlyList<OperatorAccount>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand($"SELECT {Columns} FROM exam.operator_accounts ORDER BY created_at, username;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<OperatorAccount>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(Read(reader));
        return result;
    }

    /// <summary>管理员批准、停用或重新启用账号。停用时撤销该账号全部会话；不能停用自己或最后一个启用的管理员。</summary>
    public async ValueTask<OperatorAccount> SetStatusAsync(SetOperatorStatusRequest request, AuthenticatedOperator actor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsAdministrator) throw Rule(OperatorErrorCodes.NotAdministrator, "只有管理员可以批准或停用考务账号。");
        if (request.Status == OperatorStatus.Pending) throw Rule(ParticipationErrorCodes.InvalidRequest, "不能把账号改回待审批。");
        var key = OperatorRules.Key(request.Username ?? string.Empty);
        if (key == OperatorRules.Key(actor.Username) && request.Status == OperatorStatus.Disabled)
            throw Rule(OperatorErrorCodes.LastAdministrator, "不能停用当前登录的账号。");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using (var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('operator-registration', 0));", connection, transaction))
            await gate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        var target = await FindAsync(connection, transaction, request.Username!, cancellationToken).ConfigureAwait(false)
            ?? throw Rule(ParticipationErrorCodes.SessionNotFound, "找不到该考务账号。");
        if (request.Status == OperatorStatus.Disabled && target.IsAdministrator)
        {
            await using var admins = new NpgsqlCommand(
                "SELECT count(*) FROM exam.operator_accounts WHERE is_administrator AND status = 'active' AND username_key <> @key;",
                connection, transaction);
            admins.Parameters.AddWithValue("key", key);
            if ((long)(await admins.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L) == 0)
                throw Rule(OperatorErrorCodes.LastAdministrator, "不能停用最后一个管理员账号。");
        }
        await using (var update = new NpgsqlCommand("""
            UPDATE exam.operator_accounts
               SET status = @status, updated_at = clock_timestamp(), updated_by = @actor,
                   approved_by = CASE WHEN @status = 'active' AND approved_by IS NULL THEN @actor ELSE approved_by END,
                   approved_at = CASE WHEN @status = 'active' AND approved_at IS NULL THEN clock_timestamp() ELSE approved_at END
             WHERE username_key = @key;
            UPDATE exam.operator_sessions SET revoked_at = clock_timestamp()
             WHERE username_key = @key AND revoked_at IS NULL AND @status = 'disabled';
            """, connection, transaction))
        {
            update.Parameters.AddWithValue("key", key);
            update.Parameters.AddWithValue("status", request.Status == OperatorStatus.Active ? "active" : "disabled");
            update.Parameters.AddWithValue("actor", actor.Username);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var result = await FindAsync(connection, transaction, request.Username!, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>服务管理程序的本机缓存来源：全部启用账号的口令哈希与管理员标记。</summary>
    public async ValueTask<IReadOnlyList<CachedOperatorCredential>> ListActiveCredentialsAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT username, password_hash, is_administrator, (extract(epoch FROM updated_at)*1000000)::bigint FROM exam.operator_accounts WHERE status = 'active' ORDER BY username_key;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<CachedOperatorCredential>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new CachedOperatorCredential(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetInt64(3)));
        return result;
    }

    private static void EnsureActive(OperatorAccount account)
    {
        if (account.Status == OperatorStatus.Pending)
            throw Rule(OperatorErrorCodes.PendingApproval, "该账号正在等待管理员批准，批准后才能登录。");
        if (account.Status == OperatorStatus.Disabled)
            throw Rule(OperatorErrorCodes.Disabled, "该账号已被停用，请联系管理员。");
    }

    private static async Task<OperatorAccount?> FindAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string username, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT {Columns} FROM exam.operator_accounts WHERE username_key = @key;",
            connection, transaction);
        command.Parameters.AddWithValue("key", OperatorRules.Key(username));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static OperatorAccount Read(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1),
        reader.GetString(2) switch
        {
            "active" => OperatorStatus.Active,
            "disabled" => OperatorStatus.Disabled,
            _ => OperatorStatus.Pending,
        },
        reader.GetBoolean(3), reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5) ? null : reader.GetString(5));

    private static readonly string DummyHash = OperatorPasswordHasher.Hash("synthetic-timing-equalizer");

    private static ParticipationRuleException Rule(string code, string message) => new(code, message);
}

public sealed record CachedOperatorCredential(string Username, string PasswordHash, bool IsAdministrator, long AuthorizationVersion = 0);
