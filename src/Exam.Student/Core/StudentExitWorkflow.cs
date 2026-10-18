namespace Exam.Student.Core;

/// <summary>退出当前考生登录；保留设备激活，随后返回登录页面。</summary>
public sealed class StudentExitWorkflow(StudentSession session)
{
    private int _exiting;

    public async Task<bool> ExitAsync(Func<Task<bool>> confirm, Action returnToLogin, Func<Task>? beforeReturn = null)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(returnToLogin);
        if (Interlocked.Exchange(ref _exiting, 1) != 0) return false;
        try
        {
            if (session.ActiveExam is not null)
                throw new InvalidOperationException("当前考试尚未处理完成，请先返回考试页面。");
            var originalLogin = session.LoginResult;
            if (!await confirm()) return false;
            if (session.ActiveExam is not null || !ReferenceEquals(originalLogin, session.LoginResult))
                throw new InvalidOperationException("会话已发生变化，未退出登录，请重新核对当前页面。");
            session.Logout();
            if (beforeReturn is not null) await beforeReturn();
            returnToLogin();
            return true;
        }
        finally { Volatile.Write(ref _exiting, 0); }
    }
}
