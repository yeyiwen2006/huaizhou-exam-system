namespace Exam.Student.Core;

/// <summary>登录页与选择考试页的正常退出；不持有设备存储或激活 API，不能撤销设备。</summary>
public sealed class StudentExitWorkflow(StudentSession session)
{
    private int _exiting;

    public async Task<bool> ExitAsync(Func<Task<bool>> confirm, Action releaseKioskAndClose)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(releaseKioskAndClose);
        if (Interlocked.Exchange(ref _exiting, 1) != 0) return false;
        try
        {
            if (session.ActiveExam is not null)
                throw new InvalidOperationException("当前考试尚未处理完成，请先返回考试页面。");
            var originalLogin = session.LoginResult;
            if (!await confirm()) return false;
            if (session.ActiveExam is not null || !ReferenceEquals(originalLogin, session.LoginResult))
                throw new InvalidOperationException("会话已发生变化，未退出系统，请重新核对当前页面。");
            session.Logout();
            releaseKioskAndClose();
            return true;
        }
        finally { Volatile.Write(ref _exiting, 0); }
    }
}
