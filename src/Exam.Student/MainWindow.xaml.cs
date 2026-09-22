using System.Windows;
using System.Windows.Controls;
using Exam.Student.Core;
using Exam.Student.Views;

namespace Exam.Student;

/// <summary>
/// 学生端主壳：承载页面区域与导航。所有页面切换经由 Navigation，
/// 保证编号姓名显示、账号数据隔离和后续全屏/重连（B06/B08）有统一挂载点。
/// </summary>
public partial class MainWindow : Window
{
    private Navigation _navigation = null!;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>启动流程：已激活 → 登录页；未激活 → 激活页。</summary>
    public void Start()
    {
        _navigation = new Navigation(PageRegion);

        var device = App.Services.DeviceStore.Load();
        if (device is { ClientNumber: not null })
        {
            App.Services.Session.OnActivated(device.ClientNumber);
            _navigation.GoToLogin();
            return;
        }

        _navigation.GoToActivation();
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
        }

        public void GoToActivation()
        {
            Set(new ActivationPage(this));
        }

        public void GoToLogin()
        {
            Set(new LoginPage(this));
        }

        public void GoToExamSelection()
        {
            Set(new ExamSelectionPage(this));
        }

        public void GoToWaitingExam(ExamOverview exam)
        {
            Set(new WaitingExamPage(this, exam));
        }

        public void GoToExamInProgress(ExamOverview exam)
        {
            Set(new ExamInProgressPage(this, exam));
        }

        public void GoToSubmitted()
        {
            Set(new SubmittedPage(this));
        }
    }
}
