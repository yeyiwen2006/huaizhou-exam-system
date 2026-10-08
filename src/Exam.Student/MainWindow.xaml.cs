using System.Windows;
using System.Windows.Controls;
using Exam.Student.Core;
using Exam.Student.Views;
using Exam.Contracts.Submissions;

namespace Exam.Student;

/// <summary>
/// 学生端主壳：承载页面区域与导航。所有页面切换经由 Navigation，
/// 统一处理编号姓名显示、账号数据隔离、全屏和断线重连。
/// 登录后霸屏——无边框最大化置顶 + 普通权限低级键盘钩子拦截退出快捷键；
/// 退出登录后恢复常规窗口。Ctrl+Alt+Del 用户态不可拦截，属文档注明边界。
/// </summary>
public partial class MainWindow : Window
{
    private Navigation _navigation = null!;
    private KioskGuard? _kiosk;
    private bool _applicationExitRequested;

    public MainWindow()
    {
        InitializeComponent();
        PresenceStatus.DataContext = App.Services.Session;
        Activated += (_, _) => App.Services.SetPresenceFocus(true);
        Deactivated += (_, _) => App.Services.SetPresenceFocus(false);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyKioskState();
    }

    /// <summary>登录后霸屏：拦截标题栏与系统关闭；退出登录后恢复常规关闭。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_applicationExitRequested && App.Services.Session.LoginResult is not null)
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

    /// <summary>启动流程：每次启动均进入连接与登录页。</summary>
    public Task StartAsync()
    {
        _navigation = new Navigation(PageRegion);
        _navigation.GoToLogin();
        return Task.CompletedTask;
    }

    /// <summary>根据登录状态切换霸屏与常规窗口；每次页面切换都会核对。</summary>
    private void ApplyKioskState()
    {
        var loggedIn = App.Services.Session.LoginResult is not null;
        if (loggedIn && _kiosk is null)
        {
            _kiosk = KioskGuard.Enable(this);
            Topmost = true;
            if (!_kiosk.IsInstalled)
                MessageBox.Show(this, $"本机考试防护未能开启，请联系监考人员。（错误代码 {_kiosk.InstallationError}）",
                    "考试防护未开启", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else if (!loggedIn && _kiosk is not null)
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
                var loggedIn = App.Services.Session.LoginResult is not null;
                window.WindowStyle = loggedIn ? WindowStyle.None : WindowStyle.SingleBorderWindow;
                window.ResizeMode = loggedIn ? ResizeMode.NoResize : ResizeMode.CanResize;
                window.WindowState = loggedIn ? WindowState.Maximized : WindowState.Normal;
                // 霸屏随登录状态启停。
                if (window is MainWindow main)
                {
                    main.ApplyKioskState();
                }
            }
        }

        public void GoToLogin()
        {
            App.Services.SetPresencePage("登录");
            Set(new LoginPage(this));
        }

        public void GoToExamSelection()
        {
            App.Services.SetPresencePage("选择考试");
            Set(new ExamSelectionPage(this));
        }

        public void GoToWaitingExam(ExamOverview exam)
        {
            App.Services.SetPresencePage("等待考试", exam.ExamId);
            Set(new WaitingExamPage(this, exam));
        }

        public void GoToExamInProgress(ExamOverview exam, StudentExamRuntime runtime)
        {
            App.Services.SetPresencePage("正在答题", exam.ExamId);
            Set(new ExamInProgressPage(this, exam, runtime));
        }

        public void GoToSubmitted(SubmissionReceipt receipt)
        {
            App.Services.SetPresencePage("交卷反馈", receipt.ExamId);
            Set(new SubmittedPage(this, receipt));
        }
    }
}
