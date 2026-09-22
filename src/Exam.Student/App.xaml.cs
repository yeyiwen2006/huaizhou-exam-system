using System.IO;
using System.Net.Http;
using System.Windows;
using Exam.Student.Core;

namespace Exam.Student;

/// <summary>
/// 学生端入口：组合根 + 启动流程（已激活 → 登录页；未激活 → 激活页）。
/// </summary>
public partial class App : Application
{
    /// <summary>本机学生端数据目录：激活设备信息存放于此，不入仓库。</summary>
    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HuaizhouExam", "Student");

    public static AppServices Services { get; } = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var shell = new MainWindow();
        MainWindow = shell;
        shell.Show();
        try
        {
            await shell.StartAsync();
        }
        catch (DeviceStoreException error)
        {
            MessageBox.Show(error.Message, "设备身份需要恢复", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // 未知异常可能已破坏业务状态，关闭程序并保留本地恢复记录。
        MessageBox.Show(
            "发生无法安全继续的错误，程序将关闭。已保存的身份和恢复记录会保留，请重新打开或联系考务人员。",
            "程序已停止当前操作",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.StopClock();
        Services.LocalState.Dispose();
        if (Services.Api is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnExit(e);
    }
}

/// <summary>
/// 正式入口始终使用 HTTPS 客户端；无服务器配置时停留在激活页。
/// </summary>
public sealed class AppServices
{
    private CancellationTokenSource? _clockCancellation;

    public IStudentApi Api { get; } = new HttpStudentApi();
    public StudentSession Session { get; } = new();
    public DeviceStore DeviceStore { get; } = new(App.AppDataDirectory);
    public SqliteLocalStateStore LocalState { get; } =
        new(Path.Combine(App.AppDataDirectory, "state.db"));
    public ActivationWorkflow Activation { get; }
    public ServerClock Clock { get; private set; } = new();
    public DeviceRecord? Device { get; private set; }

    public AppServices() => Activation = new ActivationWorkflow(DeviceStore, Api);

    public void ConfigureDevice(DeviceRecord device)
    {
        Device = device;
        Api.ConfigureConnection(device);
        Session.OnActivationChanged(device);
    }

    public void StartClock()
    {
        StopClock();
        _clockCancellation = new CancellationTokenSource();
        _ = RunClockAsync(Clock, _clockCancellation.Token);
    }

    public void StopClock()
    {
        _clockCancellation?.Cancel();
        _clockCancellation?.Dispose();
        _clockCancellation = null;
        Clock = new ServerClock();
    }

    private async Task RunClockAsync(ServerClock clock, CancellationToken ct)
    {
        var firstInterval = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await clock.SynchronizeAsync(Api, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or TimeoutException or OperationCanceledException)
            {
                // 保留最后一次可信时间；首次尚未校时的页面明确显示未同步，不采用本机墙钟。
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(firstInterval ? Random.Shared.Next(45, 61) : 60), ct);
                firstInterval = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
