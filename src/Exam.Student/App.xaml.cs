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

    /// <summary>
    /// B07 部署配置（exe 旁 server.json）。null 且 ServerConfigError 为 null 表示文件未部署；
    /// ServerConfigError 非 null 表示文件存在但无效。任意时刻读取，线程安全只读。
    /// </summary>
    public static ServerEndpointConfig? ServerConfig { get; private set; }
    public static ServerEndpointConfig.LoadError? ServerConfigError { get; private set; }

    public static AppServices Services { get; } = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // B07：启动即读取部署的服务器地址与信任配置；TLS 指纹 pin 在握手时生效。
        var (serverConfig, serverConfigError) = ServerEndpointConfig.Load(AppContext.BaseDirectory);
        ServerConfig = serverConfig;
        ServerConfigError = serverConfigError;
        if (serverConfig?.CertificateSha256 is { } thumbprint)
        {
            ServerTrust.Configure(thumbprint);
        }

        var shell = new MainWindow();
        MainWindow = shell;
        shell.Show();
        try
        {
            await shell.StartAsync();
        }
        catch (DeviceStoreException error)
        {
            MessageBox.Show(error.Message, "本机激活信息异常", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // 未知异常可能已破坏业务状态，关闭程序并保留本地恢复记录。
        MessageBox.Show(
            "程序发生错误，即将关闭。已保存的作答内容不会丢失，请重新启动程序或联系监考人员。",
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

    /// <summary>在线状态上报：页面/题号/试卷版本/下载状态，考务端监测与回收统计据此显示真实状态。</summary>
    public StudentPresenceReporter Presence { get; }

    public AppServices()
    {
        Activation = new ActivationWorkflow(DeviceStore, Api);
        Presence = new StudentPresenceReporter(Api, SnapshotPresence);
    }

    /// <summary>供上报读取的当前上下文；尚未登录或尚未准入时除页面外全部为空。</summary>
    private StudentPresenceSnapshot SnapshotPresence()
    {
        if (Session.ActiveExam is not { } runtime) return StudentPresenceSnapshot.None;
        var manager = runtime.Manager;
        var sessionId = string.IsNullOrWhiteSpace(manager.SessionId) ? null : manager.SessionId;
        var examId = string.IsNullOrWhiteSpace(manager.ExamId) ? null : manager.ExamId;
        // 未登录时不得携带考生信息（服务端同样拒绝“登录页夹带上一名考生”）。
        if (sessionId is null || examId is null) return StudentPresenceSnapshot.None;
        return new StudentPresenceSnapshot(
            sessionId, examId,
            runtime.Answers?.CurrentQuestionId,
            runtime.Answers?.PaperVersion ?? manager.Descriptor?.PaperVersion ?? 0,
            StudentPresenceDownloads.From(manager, runtime.PaperUpdate),
            runtime.Reconnecting);
    }

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
                // 需求：60 秒校时带完整状态补全——同一轮顺手刷新在线状态（内容未变也刷新最后通信时间）。
                await Presence.HeartbeatAsync(ct);
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
