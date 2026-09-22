using System.IO;
using System.Windows;
using Exam.Student.Core;

namespace Exam.Student;

/// <summary>
/// 学生端入口：组合根 + 启动流程（已激活 → 登录页；未激活 → 激活页）。
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// 本机学生端数据目录：激活设备信息存放于此，不入仓库。
    /// 注意：必须声明在 Services 之前——AppServices 的静态初始化会读取本字段，
    /// C# 静态字段按声明顺序初始化，颠倒顺序会使 Services 初始化时读到 null（B01 隐患，B04 修复）。
    /// </summary>
    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HuaizhouExam", "Student");

    public static AppServices Services { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var shell = new MainWindow();
        MainWindow = shell;
        shell.Show();
        shell.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.LocalState.Dispose();
        base.OnExit(e);
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
/// 组合根：B01/B02 使用 MockStudentApi 与 MockExamCommandFeed；
/// A01 契约的服务端 HTTP 客户端与 A04 SignalR 通道就绪后在此替换实现（页面不变）。
/// </summary>
public sealed class AppServices
{
    private readonly MockStudentApi _api = new();

    public IStudentApi Api => _api;
    public IExamCommandFeed CommandFeed { get; }
    public StudentSession Session { get; } = new();
    public DeviceStore DeviceStore { get; } = new(App.AppDataDirectory);

    /// <summary>
    /// B04 本地状态库（SQLite，WAL + FULL 同步，独立工作线程）：
    /// 答案、私人标记、待发送快照、固定提交与恢复记录，按服务器、考试和考生隔离。
    /// </summary>
    public SqliteLocalStateStore LocalState { get; } =
        new(Path.Combine(App.AppDataDirectory, "state.db"));

    /// <summary>本地状态隔离键的服务器维度：激活时保存的逻辑服务器地址（主备同键）。</summary>
    public string ServerKey =>
        DeviceStore.Load() is { } device ? $"{device.Host}:{device.Port}" : "mock-server";

    public AppServices()
    {
        CommandFeed = new MockExamCommandFeed(_api);
    }
}
