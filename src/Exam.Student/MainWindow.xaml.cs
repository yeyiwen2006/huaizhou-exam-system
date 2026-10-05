using System.Windows;
using System.Windows.Controls;
using Exam.Student.Core;
using Exam.Student.Views;
using Exam.Contracts.Activation;
using Exam.Contracts.Submissions;

namespace Exam.Student;

/// <summary>
/// 学生端主壳：承载页面区域与导航。所有页面切换经由 Navigation，
/// 保证编号姓名显示、账号数据隔离和后续全屏/重连（B06/B08）有统一挂载点。
/// B06：激活态霸屏——无边框最大化置顶 + 普通权限低级键盘钩子拦截退出快捷键；
/// 取消激活后恢复常规窗口。Ctrl+Alt+Del 用户态不可拦截，属文档注明边界。
/// </summary>
public partial class MainWindow : Window
{
    private Navigation _navigation = null!;
    private KioskGuard? _kiosk;
    private bool _applicationExitRequested;

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyKioskState();
    }

    /// <summary>激活态霸屏：拦截标题栏与系统关闭；取消激活后恢复常规关闭。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_applicationExitRequested && App.Services.Session.IsActivated)
        {
            e.Cancel = true;
            if (WindowState != WindowState.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
            Activate();
        }
        else
        {
            _kiosk?.Disable();
            _kiosk = null;
        }
        base.OnClosing(e);
    }

    /// <summary>只由已确认的退出系统路径调用，先释放本窗口限制再结束进程。</summary>
    public void PrepareForApplicationExit()
    {
        _applicationExitRequested = true;
        _kiosk?.Disable();
        _kiosk = null;
        Topmost = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        _kiosk?.Disable();
        _kiosk = null;
        base.OnClosed(e);
    }

    /// <summary>启动流程：已激活 → 登录页；未激活 → 激活页。</summary>
    public async Task StartAsync()
    {
        _navigation = new Navigation(PageRegion);

        var device = await Task.Run(App.Services.DeviceStore.Load);
        if (device is not null)
        {
            App.Services.ConfigureDevice(device);
        }

        if (device is { Status: ActivationStatus.Active })
        {
            App.Services.StartClock();
            _navigation.GoToLogin();
            return;
        }

        _navigation.GoToActivation();
    }

    /// <summary>根据激活状态切换霸屏与常规窗口；每次页面切换都会核对。</summary>
    private void ApplyKioskState()
    {
        var activated = App.Services.Session.IsActivated;
        if (activated && _kiosk is null)
        {
            _kiosk = KioskGuard.Enable(this);
            Topmost = true;
            if (!_kiosk.IsInstalled)
                MessageBox.Show(this, $"本机考试防护未能开启，请联系监考人员。（错误代码 {_kiosk.InstallationError}）",
                    "考试防护未开启", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (!activated && _kiosk is not null)
        {
            _kiosk.Disable();
            _kiosk = null;
            Topmost = false;
        }
    }

    /// <summary>页面导航。所有页面切换经由本类，保证统一行为。</summary>
    public sealed class Navigation
    {
        private readonly ContentControl _region;

        public Navigation(ContentControl region)
        {
            _region = region;
        }

        private void Set(UserControl page)
        {
            _region.Content = page;
            if (Window.GetWindow(_region) is { } window)
            {
                window.WindowStyle = App.Services.Session.IsActivated ? WindowStyle.None : WindowStyle.SingleBorderWindow;
                window.ResizeMode = App.Services.Session.IsActivated ? ResizeMode.NoResize : ResizeMode.CanResize;
                window.WindowState = App.Services.Session.IsActivated ? WindowState.Maximized : WindowState.Normal;
                // B06 霸屏随激活状态启停（含取消激活后的恢复）。
                if (window is MainWindow main)
                {
                    main.ApplyKioskState();
                }
            }
        }

        public void GoToActivation()
        {
            Set(new ActivationPage(this));
        }

        public void GoToLogin()
        {
            Set(new LoginPage(this));
            App.Services.Presence.ReportPage(StudentPresencePages.Login);
        }

        public void GoToExamSelection()
        {
            Set(new ExamSelectionPage(this));
            App.Services.Presence.ReportPage(StudentPresencePages.SelectingExam);
        }

        public void GoToWaitingExam(ExamOverview exam)
        {
            Set(new WaitingExamPage(this, exam));
            App.Services.Presence.ReportPage(StudentPresencePages.Waiting);
        }

        public void GoToExamInProgress(ExamOverview exam, StudentExamRuntime runtime)
        {
            Set(new ExamInProgressPage(this, exam, runtime));
            App.Services.Presence.ReportPage(StudentPresencePages.Answering);
        }

        public void GoToSubmitted(SubmissionReceipt receipt)
        {
            Set(new SubmittedPage(this, receipt));
            App.Services.Presence.ReportPage(StudentPresencePages.SubmitFeedback);
        }
    }
}
