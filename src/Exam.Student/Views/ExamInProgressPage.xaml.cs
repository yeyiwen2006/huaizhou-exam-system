using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Exam.Contracts.Submissions;
using Exam.Desktop.Shared.Controls;
using Exam.Student.Core;

namespace Exam.Student.Views;

/// <summary>
/// 答题页框保留真实本地保存状态；真实题目交互尚未接入时禁用相关操作，
/// 交卷走 B05 同步服务：本地固定后由服务上传并换取服务器持久化回执。
/// </summary>
public partial class ExamInProgressPage : UserControl
{
    private readonly ExamOverview _exam;
    private readonly StudentExamRuntime _runtime;
    private readonly MainWindow.Navigation _navigation;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SubmissionSyncService _sync;
    private bool _retryingLocalSave;
    private bool _submitting;
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
        _navigation = navigation;
        _exam = exam;
        _runtime = runtime;
        if (runtime.Manager.ExamId != exam.ExamId || !ReferenceEquals(App.Services.Session.ActiveExam, runtime))
        {
            throw new InvalidOperationException("试题会话与当前考试不匹配。");
        }
        var device = App.Services.Device ??
            throw new InvalidOperationException("设备身份尚未配置，不能进入考试。");

        InitializeComponent();
        DataContext = App.Services.Session;

        _sync = new SubmissionSyncService(App.Services.Api, runtime, device);
        _sync.StatusChanged += OnSyncStatusChanged;

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        UpdateClock();
        RefreshStatus();
        _runtime.StateChanged += OnRuntimeStateChanged;
        _ = RunSyncAsync();
        Unloaded += async (_, _) =>
        {
            _clock?.Stop();
            _lifetime.Cancel();
            _runtime.StateChanged -= OnRuntimeStateChanged;
            _sync.StatusChanged -= OnSyncStatusChanged;
            try { await _sync.DisposeAsync(); }
            catch (Exception) { /* 页面卸载时的清理失败不影响已保存状态 */ }
        };
    }

    private async Task RunSyncAsync()
    {
        try
        {
            await _sync.RunAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
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

    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        if (_submitting || _runtime.HasFixedSubmission)
        {
            return;
        }
        if (_runtime.Answers is not { Status: AnswerSessionStatus.Active })
        {
            PaperBanner.Level = StatusLevel.Warning;
            PaperBanner.Message = "当前答卷尚未就绪或已被冻结，不能发起新的交卷。";
            PaperBanner.IsOpen = true;
            return;
        }

        var window = Window.GetWindow(this);
        if (window is null) { return; }
        var confirmed = await ConfirmDialog.ShowAsync(
            window,
            "提前交卷",
            "确定提前交卷吗？交卷后答案立即冻结，不能继续修改；只有取得服务器持久化回执才视为交卷成功。",
            "提前交卷",
            "继续答题",
            isDanger: true);
        if (!confirmed) { return; }

        _submitting = true;
        Busy.IsBusy = true;
        RefreshStatus();
        try
        {
            // 提前交卷与 END 共用同一固定提交入口；失败时原标识与快照保留，由同步服务自动重试。
            var fix = await _runtime.Answers.FixSubmissionAsync($"subm-{Guid.NewGuid():N}", _lifetime.Token);
            if (!fix.Succeeded)
            {
                PaperBanner.Level = StatusLevel.Danger;
                PaperBanner.Message = $"本地固定提交未完成：{fix.Error} 原答案已保留，恢复写入后重试。";
                PaperBanner.IsOpen = true;
                return;
            }
            await _sync.KickAsync(_lifetime.Token);
            if (_sync.ConfirmedReceipt is { } receipt)
            {
                NavigateToSubmitted(receipt);
            }
            else
            {
                PaperBanner.Level = StatusLevel.Info;
                PaperBanner.Message = "答卷已冻结，正在向服务器提交并等待持久化回执；期间请保持客户端运行，失败会自动沿用原提交重试。";
                PaperBanner.IsOpen = true;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or
            System.Data.Common.DbException or InvalidOperationException)
        {
            PaperBanner.Level = StatusLevel.Danger;
            PaperBanner.Message = $"交卷流程出现异常：{error.Message} 原答卷已保留，将自动重试。";
            PaperBanner.IsOpen = true;
        }
        finally
        {
            Busy.IsBusy = false;
            _submitting = false;
            RefreshStatus();
        }
    }

    private void OnSyncStatusChanged(object? sender, SubmissionSyncStatusChangedEventArgs e)
    {
        if (_lifetime.IsCancellationRequested) { return; }
        if (e.Phase == SubmissionSyncPhase.Confirmed && _sync.ConfirmedReceipt is { } receipt)
        {
            NavigateToSubmitted(receipt);
            return;
        }
        RefreshStatus();
    }

    private void NavigateToSubmitted(SubmissionReceipt receipt)
    {
        if (!_lifetime.IsCancellationRequested)
        {
            // SubmittedPage 构造时核对并登记回执（含再次进入拦截）；此处只负责停止本页循环并跳转。
            _lifetime.Cancel();
            _navigation.GoToSubmitted(receipt);
        }
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
            ExamPackageManager.Phase.Unlocked => "加密试题包已按服务器授权解锁，并已接入本地答案恢复和服务器交卷。真实题目展示（B03）尚未接入，不能用于正式考试。",
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
        SyncStatusText.Text = _sync.StatusText;
    }
}
