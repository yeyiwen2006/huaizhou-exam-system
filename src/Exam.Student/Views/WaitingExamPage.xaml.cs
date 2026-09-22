using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Exam.Desktop.Shared.Controls;
using Exam.Student.Core;

namespace Exam.Student.Views;

/// <summary>
/// 等待考试页：显示全部考试说明与时间；B02 将接入加密包下载、参试确认与自动进入。
/// 右上角红色“返回”可退回选择考试页面，此时默认退出考试。
/// </summary>
public partial class WaitingExamPage : UserControl
{
    private readonly MainWindow.Navigation _navigation;
    private readonly ExamOverview _exam;
    private DispatcherTimer? _timer;

    // 说明文字用黑体、数字用 Times New Roman 的字体要求由考务端说明文本自身携带（C01），
    // B01 先按共享主题正文呈现结构；说明内容由 B02 从考试说明资源获取。

    /// <summary>考试名称（页面 XAML 绑定）。</summary>
    public string ExamName => _exam.Name;

    public WaitingExamPage(MainWindow.Navigation navigation, ExamOverview exam)
    {
        _navigation = navigation;
        _exam = exam;
        InitializeComponent();
        DataContext = App.Services.Session;

        TimeText.Text = $"开始时间：{exam.ScheduledStart:yyyy-MM-dd HH:mm}    结束时间：{exam.ScheduledEnd:yyyy-MM-dd HH:mm}";
        InstructionList.ItemsSource = MockStudentApi.InstructionsFor(exam.ExamId);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateCountdown();
        _timer.Start();
        UpdateCountdown();
        Unloaded += (_, _) => _timer?.Stop();
    }

    private void UpdateCountdown()
    {
        var remain = _exam.ScheduledStart - DateTimeOffset.Now;
        if (remain <= TimeSpan.Zero)
        {
            CountdownText.Text = "即将开考";
            _timer?.Stop();
            return;
        }

        CountdownText.Text = $"距开考 {remain:hh\\:mm\\:ss}";
        if (remain <= TimeSpan.FromMinutes(5))
        {
            DownloadPill.Text = "下载中…";
            DownloadPill.Level = StatusLevel.Info;
        }
    }

    private async void OnGoBack(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is null)
        {
            return;
        }

        var confirmed = await ConfirmDialog.ShowAsync(
            window,
            "返回",
            "返回选择考试页面将退出本考试，再进入需重新申请。确定返回吗？",
            "确定返回",
            "取消",
            isDanger: true);
        if (!confirmed)
        {
            return;
        }

        _navigation.GoToExamSelection();
    }

    private void OnDemoStart(object sender, RoutedEventArgs e)
    {
        _timer?.Stop();
        _navigation.GoToExamInProgress(_exam);
    }
}
