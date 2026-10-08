using System.Windows;
using Exam.Desktop.Shared.Controls;
using Exam.Student.Core;

namespace Exam.Student.Views;

internal static class StudentExitDialog
{
    public static Task<bool> ShowAsync(Window window, StudentSession session) =>
        new StudentExitWorkflow(session).ExitAsync(
            () => ConfirmDialog.ShowAsync(window, "退出系统",
                "下次启动需要重新填写服务器连接信息并登录。确定退出系统吗？", "退出", "取消", isDanger: true),
            () =>
            {
                if (window is MainWindow main) main.PrepareForApplicationExit();
                Application.Current.Shutdown();
            }, App.Services.StopPresenceAsync);
}
