using Exam.Application.Security;
using Exam.Contracts.Operators;
using Exam.Infrastructure.Postgres.Operators;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Exam.IntegrationTests.Postgres;

/// <summary>
/// 测试用合成考务账号：在测试数据库中注册（已存在则沿用）、确保已启用，再登录取得会话令牌。
/// 口令只用于测试数据库，随测试数据库一同删除。
/// </summary>
internal static class SyntheticOperators
{
    public const string Password = "synthetic-operator-password";

    public static async Task<string> LoginAsync(NpgsqlDataSource dataSource, string username, CancellationToken cancellationToken = default)
    {
        var accounts = new PostgresOperatorAccounts(dataSource);
        try
        {
            await accounts.RegisterAsync(new RegisterOperatorRequest(username, "合成考务人员", Password), cancellationToken);
        }
        catch (Exam.Application.Participation.ParticipationRuleException error) when (error.Code == OperatorErrorCodes.UsernameTaken)
        {
            // 同一测试数据库中已注册过该账号。
        }

        // 后注册的账号需要管理员批准；测试直接在数据库中启用，不经过批准接口。
        await using (var command = dataSource.CreateCommand(
                         "update exam.operator_accounts set status = 'active', approved_by = 'synthetic-test', approved_at = now() where username_key = $1 and status <> 'active'"))
        {
            command.Parameters.AddWithValue(OperatorRules.Key(username));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var session = await accounts.LoginAsync(new LoginOperatorRequest(username, Password), cancellationToken);
        return session.Token;
    }

    /// <summary>注册数据库考务账号鉴权（与正式服务器相同）。</summary>
    public static IServiceCollection AddPostgresOperatorAccounts(this IServiceCollection services)
    {
        services.AddSingleton<PostgresOperatorAccounts>();
        services.AddSingleton<IOperatorAuthenticator>(provider => provider.GetRequiredService<PostgresOperatorAccounts>());
        return services;
    }
}

/// <summary>不访问数据库的固定会话，只用于中间件单元级测试。</summary>
internal sealed class FixedOperatorAuthenticator(string token, string username, bool isAdministrator = true) : IOperatorAuthenticator
{
    public ValueTask<AuthenticatedOperator?> AuthenticateAsync(string candidate, CancellationToken cancellationToken) =>
        ValueTask.FromResult(string.Equals(candidate, token, StringComparison.Ordinal)
            ? new AuthenticatedOperator(username, isAdministrator)
            : null);
}
