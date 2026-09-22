using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Exam.Desktop.Shared.Controls;
using Exam.Student.Core;

namespace Exam.Student.Views;

/// <summary>
/// 答题页框。正式答题、持久保存及交卷尚未接入时禁用相关操作。
/// </summary>
public partial class ExamInProgressPage : UserControl
{
    private readonly ExamOverview _exam;

    /// <summary>页头右侧时钟胶囊（因 XAML 命名作用域限制经绑定注入）。</summary>
    public StatusPill ClockPillContent { get; } = new()
    {
        Text = "服务器时间 --:--:--",
        Level = StatusLevel.Info,
    };
    private DispatcherTimer? _clock;

    public ExamInProgressPage(MainWindow.Navigation navigation, ExamOverview exam)
    {
        _exam = exam;
        InitializeComponent();
        DataContext = App.Services.Session;

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        UpdateClock();
        Unloaded += (_, _) => _clock?.Stop();
    }

    private void OnToggleMark(object sender, RoutedEventArgs e)
    {
        // B03/B04 接入可持久保存的真实题目后才能启用标记。
    }

    private void UpdateClock()
    {
        if (!App.Services.Clock.IsSynchronized)
        {
            ClockPillContent.Text = "服务器时间尚未同步";
            return;
        }

        var now = App.Services.Clock.UtcNow;
        ClockPillContent.Text = $"服务器时间 {now:HH:mm:ss}";
        var remain = _exam.ScheduledEnd - now;
        ClockPillContent.Text = remain > TimeSpan.Zero
            ? $"{ClockPillContent.Text} · 距结束 {remain:hh\\:mm\\:ss}"
            : $"{ClockPillContent.Text} · 计划结束时间已到";
    }

    private void OnSubmit(object sender, RoutedEventArgs e)
    {
        // B05 接入固定快照和服务器持久化回执之前不发起提交或显示成功。
    }
}
