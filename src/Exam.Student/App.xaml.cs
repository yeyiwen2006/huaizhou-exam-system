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
    private CancellationTokenSource? _presenceCancellation;
    private StudentPresenceReporter? _presence;
    private StudentExamRuntime? _presenceRuntime;
    private readonly System.Windows.Threading.DispatcherTimer _presenceTimer = new()
        { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _presenceEnabled;
    private bool _presenceBusy;
    private string _page = "登录";
    private string? _pageExamId;
    private bool _focused = true;

    public IStudentApi Api { get; } = new HttpStudentApi();
    public StudentSession Session { get; } = new();
    public DeviceStore DeviceStore { get; } = new(App.AppDataDirectory);
    public SqliteLocalStateStore LocalState { get; } =
        new(Path.Combine(App.AppDataDirectory, "state.db"));
    public ActivationWorkflow Activation { get; }
    public ServerClock Clock { get; private set; } = new();
    public DeviceRecord? Device { get; private set; }

    public AppServices()
    {
        Activation = new ActivationWorkflow(DeviceStore, Api);
        _presenceTimer.Tick += (_, _) => RefreshPresence();
        Session.PropertyChanged += (_, _) => RefreshPresence();
    }

    public void ConfigureDevice(DeviceRecord device)
    {
        var replacePresence = Device?.DeviceId != device.DeviceId || Device?.ServerId != device.ServerId ||
            Device?.Credential != device.Credential;
        Device = device;
        Api.ConfigureConnection(device);
        Session.OnActivationChanged(device);
        if (replacePresence)
        {
            _presenceCancellation?.Cancel();
            _presenceCancellation?.Dispose();
            _presenceCancellation = new();
            _presence = Api is IStudentPresenceApi presenceApi ? new(presenceApi) : null;
        }
        RefreshPresence();
    }

    public void StartPresence()
    {
        _presenceEnabled = true;
        _presenceTimer.Start();
        RefreshPresence();
    }

    public void SetPresencePage(string page, string? examId = null)
    {
        _page = page;
        _pageExamId = examId;
        RefreshPresence();
    }

    public void SetPresenceFocus(bool focused)
    {
        _focused = focused;
        RefreshPresence();
    }

    private void OnPresenceRuntimeChanged(object? sender, EventArgs e) => RefreshPresence();

    private StudentPresenceState ReadPresence()
    {
        var session = Session.LoginResult;
        var runtime = Session.ActiveExam;
        var examId = session is null ? null : _pageExamId ?? runtime?.Manager.ExamId;
        var page = session is null ? "登录" : _page == "登录" ? "选择考试" : _page;
        if (page == "正在答题" && runtime?.HasFixedSubmission == true) page = "回收中";
        if (!_focused && page == "正在答题") page = "正在答题（已切出）";
        if (runtime?.Answers?.Status is AnswerSessionStatus.WriteBlocked or AnswerSessionStatus.FixPending)
            page = "本地保存失败";
        var manager = runtime?.Manager;
        var download = manager?.LastError is not null ? "失败" :
            manager?.CurrentPhase == ExamPackageManager.Phase.Downloading ? "下载中" :
            runtime?.IsPaperUpdateInProgress == true ? "更新中" :
            manager?.CurrentPhase is ExamPackageManager.Phase.Downloaded or ExamPackageManager.Phase.ConfirmedWaiting or
                ExamPackageManager.Phase.Unlocked or ExamPackageManager.Phase.Ended ? "已下载" : "未下载";
        var version = runtime?.Answers?.PaperVersion ?? (download == "已下载" ? manager?.Descriptor?.PaperVersion : 0) ?? 0;
        return new(session?.SessionId, examId, page,
            examId is null ? null : runtime?.Answers?.CurrentQuestionId, examId is null ? 0 : version,
            examId is null ? "未下载" : download, runtime?.ConnectionError is not null);
    }

    private async void RefreshPresence()
    {
        if (!_presenceEnabled || _presenceBusy || _presence is not { } reporter ||
            Device?.Status != Exam.Contracts.Activation.ActivationStatus.Active || _presenceCancellation is null) return;
        if (!ReferenceEquals(_presenceRuntime, Session.ActiveExam))
        {
            if (_presenceRuntime is not null) _presenceRuntime.StateChanged -= OnPresenceRuntimeChanged;
            _presenceRuntime = Session.ActiveExam;
            if (_presenceRuntime is not null) _presenceRuntime.StateChanged += OnPresenceRuntimeChanged;
        }
        _presenceBusy = true;
        var ct = _presenceCancellation.Token;
        try
        {
            await reporter.ReportAsync(ReadPresence(), ct);
            if (ReferenceEquals(reporter, _presence)) Session.PresenceStatus = reporter.Error ?? string.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { _presenceBusy = false; }
    }

    public async Task StopPresenceAsync()
    {
        _presenceEnabled = false;
        _presenceTimer.Stop();
        if (_presenceRuntime is not null) _presenceRuntime.StateChanged -= OnPresenceRuntimeChanged;
        if (_presence is { } reporter && Device?.Status == Exam.Contracts.Activation.ActivationStatus.Active)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await reporter.ReportAsync(new(null, null, "已退出"), timeout.Token, disconnect: true); }
            catch (OperationCanceledException) { }
        }
        _presenceCancellation?.Cancel();
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
