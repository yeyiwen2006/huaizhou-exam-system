using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Exam.Desktop.Shared.Controls;
using Exam.Student.Core;

namespace Exam.Student.Views;

/// <summary>
/// 进入准备页框。下载与参试确认接入前不显示“已参试”，也不开放题目。
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

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateCountdown();
        _timer.Start();
        UpdateCountdown();
        Unloaded += (_, _) => _timer?.Stop();
    }

    private void UpdateCountdown()
    {
        if (!App.Services.Clock.IsSynchronized)
        {
            CountdownText.Text = "服务器时间尚未同步";
            return;
        }

        var remain = _exam.ScheduledStart - App.Services.Clock.UtcNow;
        if (remain <= TimeSpan.Zero)
        {
            CountdownText.Text = "计划开始时间已到，尚未确认参试";
            return;
        }

        CountdownText.Text = $"距开考 {remain:hh\\:mm\\:ss}";
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
            "当前尚未确认参试。确定返回选择考试页面吗？",
            "确定返回",
            "取消",
            isDanger: true);
        if (!confirmed)
        {
            return;
        }

        _navigation.GoToExamSelection();
    }

}
