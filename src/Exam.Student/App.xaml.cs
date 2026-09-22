using System.IO;
using System.Windows;
using Exam.Student.Core;

namespace Exam.Student;

/// <summary>
/// 学生端入口：组合根 + 启动流程（已激活 → 登录页；未激活 → 激活页）。
/// </summary>
public partial class App : Application
{
    public static AppServices Services { get; } = new();

    /// <summary>本机学生端数据目录：激活设备信息存放于此，不入仓库。</summary>
    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HuaizhouExam", "Student");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var shell = new MainWindow();
        MainWindow = shell;
        shell.Show();
        shell.Start();
    }

    private void OnDispatcherUnhandledException(object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // 未处理异常兜底：保留界面可响应，不静默吞错（B04 接入本地状态保护）
        MessageBox.Show(
            $"发生未处理的错误：\n{e.Exception.Message}",
            "错误",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}

/// <summary>
/// 组合根：B01 使用 MockStudentApi；A01 契约的服务端 HTTP 客户端就绪后在此替换实现（页面不变）。
/// </summary>
public sealed class AppServices
{
    public IStudentApi Api { get; } = new MockStudentApi();
    public StudentSession Session { get; } = new();
    public DeviceStore DeviceStore { get; } = new(App.AppDataDirectory);
}
