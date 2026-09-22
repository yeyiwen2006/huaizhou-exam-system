using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Exam.Desktop.Shared.Controls;
using Exam.Student.Core;

namespace Exam.Student.Views;

/// <summary>
/// 答题页框保留真实本地保存状态；真实题目交互和服务器交卷尚未接入时禁用相关操作。
/// </summary>
public partial class ExamInProgressPage : UserControl
{
    private readonly ExamOverview _exam;
    private readonly StudentExamRuntime _runtime;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _retryingLocalSave;
    private string? _localRetryError;

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
            _lifetime.Cancel();
            _runtime.StateChanged -= OnRuntimeStateChanged;
        };
    }

    private void OnToggleMark(object sender, RoutedEventArgs e)
    {
        // B04 is attached to the real runtime. B03 must still supply verified question IDs
        // before this button can mutate a private mark; no placeholder question is created.
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

    private async void OnRetryLocalSave(object sender, RoutedEventArgs e)
    {
        if (_retryingLocalSave) { return; }
        _retryingLocalSave = true;
        RefreshStatus();
        try
        {
            await _runtime.RetryLocalSaveAsync(_lifetime.Token);
            _localRetryError = null;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or
                                          System.Data.Common.DbException or InvalidOperationException)
        {
            _localRetryError = "本地保存尚未恢复，原记录和待保存修改均已保留。请联系考务人员检查存储后重试。";
        }
        finally { _retryingLocalSave = false; RefreshStatus(); }
    }

    private void RefreshStatus()
    {
        PaperBanner.Level = StatusLevel.Warning;
        PaperBanner.Message = _runtime.Manager.CurrentPhase switch
        {
            ExamPackageManager.Phase.Ended => "服务器已结束考试。原参试记录继续保留，当前版本尚未提供完整答卷补传功能，请联系考务人员。",
            ExamPackageManager.Phase.Revoked => "当前设备的答题授权已撤销，已停止访问试题。请联系考务人员。",
            ExamPackageManager.Phase.Unlocked => "加密试题包已按服务器授权解锁，并已接入本地答案恢复。当前版本尚未提供真实题目展示与完整交卷功能，不能用于正式考试。",
            _ => "试题尚未完成安全解锁，不能开始答题。请联系考务人员。",
        };
        RuntimeErrorBanner.IsOpen = !string.IsNullOrWhiteSpace(_runtime.ConnectionError) || !string.IsNullOrWhiteSpace(_runtime.Manager.LastError);
        RuntimeErrorBanner.Message = _runtime.ConnectionError ?? _runtime.Manager.LastError ?? string.Empty;
        LocalStateText.Text = _runtime.LocalStateSummary;
        var localError = _localRetryError ?? _runtime.LocalStateError;
        LocalStateErrorBanner.IsOpen = !string.IsNullOrWhiteSpace(localError);
        LocalStateErrorBanner.Message = localError ?? string.Empty;
        RetryLocalSaveButton.Visibility = _runtime.CanRetryLocalSave ? Visibility.Visible : Visibility.Collapsed;
        RetryLocalSaveButton.IsEnabled = !_retryingLocalSave && _runtime.CanRetryLocalSave;
    }
}
