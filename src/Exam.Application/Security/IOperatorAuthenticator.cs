namespace Exam.Application.Security;

/// <summary>已登录的考务身份；用户名用于全部操作记录。</summary>
public sealed record AuthenticatedOperator(string Username, bool IsAdministrator);

/// <summary>按会话令牌核对考务身份。账号须为启用状态且会话未被退出或停用撤销。</summary>
public interface IOperatorAuthenticator
{
    ValueTask<AuthenticatedOperator?> AuthenticateAsync(string token, CancellationToken cancellationToken = default);
}
