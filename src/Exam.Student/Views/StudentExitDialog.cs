using System.Windows;
using Exam.Desktop.Shared.Controls;
using Exam.Student.Core;

namespace Exam.Student.Views;

internal static class StudentExitDialog
{
    public static Task<bool> ShowAsync(Window window, StudentSession session, Action returnToLogin) =>
        new StudentExitWorkflow(session).ExitAsync(
            () => ConfirmDialog.ShowAsync(window, "退出登录",
                "确定退出当前考生账号并返回登录页面吗？", "退出登录", "取消"),
            returnToLogin, async () =>
            {
                await App.Services.StopPresenceAsync();
                App.Services.StopClock();
            });

    public static Task<bool> ShowSystemExitAsync(Window window, StudentSession session) =>
        new StudentExitWorkflow(session).ExitAsync(
            () => ConfirmDialog.ShowAsync(window, "退出系统", "确定关闭学生端吗？", "退出", "取消"),
            () =>
            {
                if (window is MainWindow main) main.PrepareForApplicationExit();
                Application.Current.Shutdown();
            }, async () =>
            {
                await App.Services.StopPresenceAsync();
                App.Services.StopClock();
            });
}
