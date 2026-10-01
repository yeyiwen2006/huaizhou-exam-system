namespace Exam.Contracts.Operators;

/// <summary>
/// 考务账号：用户名和口令注册登录。服务器上第一个注册的账号直接启用并成为管理员，
/// 之后注册的账号须由管理员批准后才能登录；停用账号时其全部会话立即失效。
/// </summary>
public enum OperatorStatus
{
    Pending,
    Active,
    Disabled,
}

public sealed record RegisterOperatorRequest(string Username, string DisplayName, string Password);

public sealed record LoginOperatorRequest(string Username, string Password);

public sealed record OperatorAccount(
    string Username,
    string DisplayName,
    OperatorStatus Status,
    bool IsAdministrator,
    DateTimeOffset CreatedAt,
    string? ApprovedBy);

/// <summary>注册结果：第一个账号直接启用；其余账号状态为待审批。</summary>
public sealed record RegisterOperatorResult(OperatorAccount Account);

/// <summary>登录成功后的会话。令牌只在本次响应中出现，服务器只保存其哈希。</summary>
public sealed record OperatorSession(string Token, OperatorAccount Account);

public sealed record SetOperatorStatusRequest(string Username, OperatorStatus Status);

public static class OperatorErrorCodes
{
    public const string UsernameTaken = "OPERATOR_USERNAME_TAKEN";
    public const string InvalidCredentials = "OPERATOR_INVALID_CREDENTIALS";
    public const string PendingApproval = "OPERATOR_PENDING_APPROVAL";
    public const string Disabled = "OPERATOR_DISABLED";
    public const string NotAdministrator = "OPERATOR_NOT_ADMINISTRATOR";
    public const string LastAdministrator = "OPERATOR_LAST_ADMINISTRATOR";
}

public static class OperatorRules
{
    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 128;

    /// <summary>用户名：2–32 个字符，字母、数字、下划线、点或连字符；比较时不区分大小写。</summary>
    public static string? DescribeUsername(string? username)
    {
        var value = username?.Trim() ?? string.Empty;
        if (value.Length is < 2 or > 32) return "用户名须为 2–32 个字符。";
        return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-')
            ? null
            : "用户名只能包含英文字母、数字、下划线、点或连字符。";
    }

    public static string? DescribePassword(string? password) =>
        password is null || password.Length < MinPasswordLength || password.Length > MaxPasswordLength
            ? $"口令须为 {MinPasswordLength}–{MaxPasswordLength} 个字符。"
            : null;

    public static string Key(string username) => username.Trim().ToLowerInvariant();
}
