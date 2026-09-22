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
    private readonly StudentExamRuntime _runtime;

    /// <summary>页头右侧时钟胶囊（因 XAML 命名作用域限制经绑定注入）。</summary>
    public StatusPill ClockPillContent { get; } = new()
    {
        Text = "服务器时间 --:--:--",
        Level = StatusLevel.Info,
    };
    private DispatcherTimer? _clock;

    public ExamInProgressPage(MainWindow.Navigation navigation, ExamOverview exam, StudentExamRuntime runtime)
    {
        _exam = exam;
        _runtime = runtime;
        if (runtime.Manager.ExamId != exam.ExamId || !ReferenceEquals(App.Services.Session.ActiveExam, runtime))
        {
            throw new InvalidOperationException("试题会话与当前考试不匹配。");
        }

        InitializeComponent();
        DataContext = App.Services.Session;

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        UpdateClock();
        RefreshStatus();
        _runtime.StateChanged += OnRuntimeStateChanged;
        Unloaded += (_, _) =>
        {
            _clock?.Stop();
            _runtime.StateChanged -= OnRuntimeStateChanged;
        };
    }

    private void OnToggleMark(object sender, RoutedEventArgs e)
    {
        // B03/B04 接入可持久保存的真实题目后才能启用标记。
    }

    private void UpdateClock()
    {
        RefreshStatus();
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

    private void OnRuntimeStateChanged(object? sender, EventArgs e) => RefreshStatus();

    private void RefreshStatus()
    {
        PaperBanner.Level = StatusLevel.Warning;
        PaperBanner.Message = _runtime.Manager.CurrentPhase switch
        {
            ExamPackageManager.Phase.Ended => "服务器已结束考试。原参试记录继续保留，当前版本尚未提供完整答卷补传功能，请联系考务人员。",
            ExamPackageManager.Phase.Revoked => "当前设备的答题授权已撤销，已停止访问试题。请联系考务人员。",
            ExamPackageManager.Phase.Unlocked => "加密试题包已按服务器授权解锁。当前版本尚未提供题目展示、作答持久保存与交卷功能，请勿用于正式考试。",
            _ => "试题尚未完成安全解锁，不能开始答题。请联系考务人员。",
        };
        RuntimeErrorBanner.IsOpen = !string.IsNullOrWhiteSpace(_runtime.ConnectionError) || !string.IsNullOrWhiteSpace(_runtime.Manager.LastError);
        RuntimeErrorBanner.Message = _runtime.ConnectionError ?? _runtime.Manager.LastError ?? string.Empty;
    }
}
