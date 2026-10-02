using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Exam.Contracts.Resources;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Desktop.Shared.Controls;
using Exam.Student.Core;

namespace Exam.Student.Views;

/// <summary>
/// 答题页（B03 接入后为真实作答页）：左侧导航跳题，右侧原版题目 PDF 与圆形选项控件；
/// 答案、当前题与私人标记经 B04 会话可靠落盘，交卷走 B05 同步服务——
/// 本地固定后由服务上传并换取服务器持久化回执，仅取得可信回执才跳转成功页。
/// 选项坐标来自 D03 契约的 option-regions.json；未提供坐标（v1 包）仅原版显示，不猜测点击位置。
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
    private bool _answerBusy;
    private string? _localRetryError;

    private PaperNavigator? _navigator;
    private StudentPaperManifest? _loadedPaper;
    private OptionRegionMap? _regionMap;
    private string? _regionError;
    private string? _currentQuestionId;
    private int _renderGeneration;
    private int _workbenchGeneration;
    private bool _rendering;
    private bool _started;
    private readonly FocusTracker _focus = new();
    private Window? _focusWindow;
    private bool _reviewPending;
    private bool _reviewSaving;
    private bool _jumping;
    private IReadOnlyList<string>? _changedQuestionIds;

    /// <summary>右下角时钟胶囊（因 XAML 命名作用域限制经绑定注入）。</summary>
    public StatusPill ClockPillContent { get; } = new()
    {
        Text = "--:--",
        Level = StatusLevel.Info,
    };

    /// <summary>提交按钮启用状态（RightContent 命名作用域限制，经 INPC 绑定驱动）。</summary>
    public SubmitButtonState Submit { get; } = new();
    private DispatcherTimer? _clock;
    private bool _syncStopped;

    /// <summary>提交按钮启用状态的可绑定载体。</summary>
    public sealed class SubmitButtonState : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isEnabled;
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) { return; }
                _isEnabled = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsEnabled)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

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

        _sync = new SubmissionSyncService(App.Services.Api, runtime, device, clock: App.Services.Clock, focus: _focus);
        _sync.StatusChanged += OnSyncStatusChanged;

        // B06 在线更新：编排器状态与切换结果驱动提示横幅与复核记录。
        runtime.PaperUpdate.StatusChanged += OnPaperUpdateStatusChanged;
        runtime.PaperUpdate.UpdateInstalled += OnPaperUpdateInstalled;

        QuestionView.OptionClicked += OnOptionClicked;
        QuestionView.AbstainClicked += OnAbstainClicked;
        QuestionView.RegionsInvalid += OnRegionsInvalid;

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        UpdateClock();
        RefreshStatus();
        _runtime.StateChanged += OnRuntimeStateChanged;
        Loaded += (_, _) =>
        {
            AttachFocusWindow();
            if (_started || _lifetime.IsCancellationRequested) return;
            _started = true;
            _ = RunSyncAsync();
            _ = InitializeWorkbenchAsync();
        };
        Unloaded += async (_, _) =>
        {
            _clock?.Stop();
            // B06 失焦留痕：页面卸载时闭合尚未恢复的失焦段，先落盘再触发一个补报回合。
            var wasUnfocused = _focus.IsUnfocused;
            _focus.Close();
            if (wasUnfocused && _focus.Snapshot().LastOrDefault() is { } closingFocus)
            {
                try
                {
                    if (await _runtime.RecordFocusLossAsync(closingFocus, CancellationToken.None))
                        await _sync.KickAsync(CancellationToken.None);
                }
                catch (Exception) { /* 卸载时的留痕失败不阻止清理；事件已尽最大努力落盘 */ }
            }
            _lifetime.Cancel();
            DetachFocusWindow();
            _runtime.StateChanged -= OnRuntimeStateChanged;
            _runtime.PaperUpdate.StatusChanged -= OnPaperUpdateStatusChanged;
            _runtime.PaperUpdate.UpdateInstalled -= OnPaperUpdateInstalled;
            _sync.StatusChanged -= OnSyncStatusChanged;
            QuestionView.OptionClicked -= OnOptionClicked;
            QuestionView.AbstainClicked -= OnAbstainClicked;
            QuestionView.RegionsInvalid -= OnRegionsInvalid;
            InvalidateWorkbench();
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
        catch (Exception error) when (error is InvalidOperationException or System.IO.IOException or
            System.Text.Json.JsonException or ArgumentException)
        {
            _syncStopped = true;
            RefreshStatus();
        }
    }

    /// <summary>B06 失焦检测：窗口失焦/复焦经窗口事件驱动；复焦立即唤醒同步服务补报。</summary>
    private void AttachFocusWindow()
    {
        if (_focusWindow is not null) return;
        if (Window.GetWindow(this) is not { } window) return;
        _focusWindow = window;
        window.Deactivated += OnWindowDeactivated;
        window.Activated += OnWindowActivated;
        window.PreviewKeyDown += OnNavigationKeyDown;
    }

    private void DetachFocusWindow()
    {
        if (_focusWindow is not { } window) return;
        window.Deactivated -= OnWindowDeactivated;
        window.Activated -= OnWindowActivated;
        window.PreviewKeyDown -= OnNavigationKeyDown;
        _focusWindow = null;
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_lifetime.IsCancellationRequested) return;
        if (_focus.OnFocusLost()) _ = KickSyncAfterRefocusAsync();
        RefreshFocusStatus();
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        if (_lifetime.IsCancellationRequested) return;
        if (_focus.OnFocusRegained())
        {
            // B06 失焦留痕：该段失焦写入本地发件箱；复焦立即触发同步回合补报。
            _ = PersistAndReportFocusAsync(_focus.Snapshot().LastOrDefault());
            _ = KickSyncAfterRefocusAsync();
        }
        RefreshFocusStatus();
    }

    /// <summary>把一段已完成的失焦先落盘再唤醒同步；本地失败不阻塞作答，由状态行提示。</summary>
    private async Task PersistAndReportFocusAsync(FocusLossEvent? focusEvent)
    {
        if (focusEvent is null || _lifetime.IsCancellationRequested) return;
        try
        {
            if (await _runtime.RecordFocusLossAsync(focusEvent, _lifetime.Token))
                await _sync.KickAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { /* 留痕失败由运行时错误状态呈现，不影响作答 */ }
        finally { RefreshStatus(); }
    }

    private async Task KickSyncAfterRefocusAsync()
    {
        try { await _sync.KickAsync(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { /* 补报失败由同步服务自身状态行呈现并按周期重试 */ }
        finally { RefreshStatus(); }
    }

    // 失焦次数随同步上报给考务端，不在考生界面显示。
    private void RefreshFocusStatus() { }

    /// <summary>
    /// 底部提示只在考生需要知道或处理时出现，使用通俗说法；正常保存与上报时不显示。
    /// 详细的内部状态保留在运行时对象中，供考务排查。
    /// </summary>
    private string? StudentNotice()
    {
        if (_runtime.Answers?.HasPendingSubmission == true)
            return "答卷已提交，但尚未保存到本机。请点击“重试保存”，或联系监考人员。";
        if (_runtime.Answers?.Status == AnswerSessionStatus.WriteBlocked)
            return "作答内容暂时无法保存到本机，已暂停作答。请点击“重试保存”，或联系监考人员。";
        if (_runtime.Answers?.Status == AnswerSessionStatus.PermissionDenied)
            return "本机暂停作答，已作答内容已保留。请联系监考人员。";
        if (_syncStopped || _sync.Phase == SubmissionSyncPhase.Blocked)
            return "作答内容已保存在本机，但暂时无法同步到服务器。请联系监考人员。";
        if (_runtime.HasFixedSubmission && _sync.Phase != SubmissionSyncPhase.Confirmed)
            return "答卷已提交，正在等待服务器确认，请勿关闭程序。";
        if (_sync.Phase == SubmissionSyncPhase.WaitingRetry)
            return "网络连接中断，作答内容已保存在本机，恢复后将自动同步。";
        return null;
    }

    /// <summary>B06 在线更新：状态横幅按阶段着色。</summary>
    private void OnPaperUpdateStatusChanged(object? sender, PaperUpdateStatusChangedEventArgs e)
    {
        if (_lifetime.IsCancellationRequested) { return; }
        PaperUpdateBanner.IsOpen = e.Phase != PaperUpdatePhase.None && !string.IsNullOrEmpty(e.Message);
        PaperUpdateBanner.Message = e.Message ?? string.Empty;
        PaperUpdateBanner.Level = e.Phase switch
        {
            PaperUpdatePhase.Applied => StatusLevel.Success,
            PaperUpdatePhase.Failed => StatusLevel.Danger,
            PaperUpdatePhase.Pending or PaperUpdatePhase.Syncing => StatusLevel.Warning,
            _ => StatusLevel.Warning,
        };
    }

    /// <summary>先允许考生阅读修订内容，再由按钮明确确认，不用挡住题目的弹窗代替复核。</summary>
    private void OnPaperUpdateInstalled(object? sender,
        (long Version, DateTimeOffset ReceivedAt, DateTimeOffset InstalledAt, IReadOnlyList<string> ChangedQuestionIds) e)
    {
        if (_lifetime.IsCancellationRequested) return;
        _runtime.RecordInstalledPaperUpdate(e.Version, e.ReceivedAt, e.InstalledAt);
        _changedQuestionIds = e.ChangedQuestionIds;
        RefreshStatus();
    }

    /// <summary>跳到本次修订的第一道变更题；清单为空或题目不在当前清单内时按钮不可见。</summary>
    private async void OnJumpToChangedQuestion(object sender, RoutedEventArgs e)
    {
        if (_jumping || _lifetime.IsCancellationRequested || _navigator is null || _rendering ||
            _changedQuestionIds is not { Count: > 0 }) return;
        var target = _changedQuestionIds
            .Select(id => _navigator.Find(id))
            .FirstOrDefault(question => question is not null);
        if (target is null) return;
        _jumping = true;
        try
        {
            _currentQuestionId = target.QuestionId;
            await RenderCurrentQuestionAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsWorkbenchError(error))
        {
            _regionError ??= "题目显示失败，请联系监考人员。";
            RefreshStatus();
        }
        finally { _jumping = false; RefreshStatus(); }
    }

    private async void OnConfirmReview(object sender, RoutedEventArgs e)
    {
        if (_reviewSaving || !_reviewPending || _lifetime.IsCancellationRequested ||
            _runtime.PendingReviewVersion is not { } version) return;
        _reviewSaving = true;
        ReviewUpdateButton.IsEnabled = false;
        try
        {
            var result = await _runtime.ConfirmPaperUpdateReviewAsync(version, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            PaperBanner.Level = result is { Succeeded: true } ? StatusLevel.Info : StatusLevel.Warning;
            PaperBanner.Message = result is { Succeeded: true }
                ? "已确认查看更正后的试题。"
                : "确认未保存成功，请查看当前试题后重新确认。";
            PaperBanner.IsOpen = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            _reviewSaving = false;
            if (!_lifetime.IsCancellationRequested) RefreshStatus();
        }
    }

    /// <summary>B06 设备裁决：本设备被撤销后返回选择考试（本机已有答案已保留）。</summary>
    private async void OnRevokedBack(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } window) { return; }
        var confirmed = await ConfirmDialog.ShowAsync(
            window, "返回选择考试", "确定返回选择考试页面吗？本机已停止作答，已作答内容已保留。",
            "返回", "留在本页", isDanger: false);
        if (!confirmed) { return; }
        _lifetime.Cancel();
        await App.Services.Session.ReleaseExamAsync();
        _navigation.GoToExamSelection();
    }

    private bool CanAnswer =>
        !_lifetime.IsCancellationRequested && !_submitting && !_rendering && !_runtime.IsPaperUpdateInProgress &&
        _sync.Phase is not (SubmissionSyncPhase.Blocked or SubmissionSyncPhase.Confirmed) &&
        _runtime.Manager.CurrentPhase == ExamPackageManager.Phase.Unlocked &&
        ReferenceEquals(_loadedPaper, _runtime.Paper) && QuestionView.HasQuestion &&
        _navigator is not null &&
        _regionMap is not null &&
        _regionError is null &&
        _runtime.Answers is { Status: AnswerSessionStatus.Active } &&
        !_runtime.HasFixedSubmission;

    private async Task InitializeWorkbenchAsync()
    {
        try
        {
            await EnsureWorkbenchAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsWorkbenchError(error))
        {
            _regionError ??= "题目显示失败，已作答内容已保存。请联系监考人员。";
            RefreshStatus();
        }
    }

    private static bool IsWorkbenchError(Exception error) => error is IOException or InvalidDataException or
        UnauthorizedAccessException or System.Data.Common.DbException or InvalidOperationException ||
        error is System.Text.Json.JsonException;

    /// <summary>试题解锁或版本切换后（重）建导航与坐标；同一份清单只初始化一次。</summary>
    private async Task EnsureWorkbenchAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var paper = _runtime.Paper;
        if (paper is null)
        {
            InvalidateWorkbench();
            RefreshNav();
            RefreshStatus();
            return;
        }
        if (ReferenceEquals(paper, _loadedPaper) && _navigator is not null) return;
        InvalidateWorkbench();
        var generation = _workbenchGeneration;
        _loadedPaper = paper;
        _navigator = new PaperNavigator(paper);
        var payload = _runtime.Manager.UnlockedPayload;
        if (payload is null) { InvalidateWorkbench(); RefreshStatus(); return; }
        OptionRegionMap? regions = null;
        string? error = null;
        try
        {
            regions = await Task.Run(() => OptionRegionMap.TryReadFromPackage(payload, paper), ct);
        }
        catch (Exception failure) when (IsWorkbenchError(failure))
        {
            error = "本场试卷的选项位置无法确定，暂无法作答。请联系监考人员。";
        }
        if (ct.IsCancellationRequested || generation != _workbenchGeneration || !ReferenceEquals(paper, _runtime.Paper) ||
            !ReferenceEquals(payload, _runtime.Manager.UnlockedPayload)) return;
        _regionMap = regions;
        _regionError = error;
        var restored = _runtime.Answers?.CurrentQuestionId;
        _currentQuestionId = _navigator.Find(restored ?? string.Empty) is not null
            ? restored : _navigator.FirstQuestion().QuestionId;
        await RenderCurrentQuestionAsync(ct);
    }

    private void InvalidateWorkbench()
    {
        _workbenchGeneration++;
        _renderGeneration++;
        _loadedPaper = null;
        _navigator = null;
        _regionMap = null;
        _regionError = null;
        _currentQuestionId = null;
        _rendering = false;
        QuestionView.ClearContent();
    }

    private async Task RenderCurrentQuestionAsync(CancellationToken ct)
    {
        if (_navigator is null || _currentQuestionId is null || _lifetime.IsCancellationRequested) return;
        var generation = ++_renderGeneration;
        var paper = _loadedPaper;
        var question = _navigator.Find(_currentQuestionId) ?? throw new InvalidOperationException("当前题不在学生清单内。");
        _rendering = true;
        RefreshStatus();
        byte[]? pdf = null;
        try
        {
            pdf = _runtime.ReadQuestionPdf(question.QuestionId);
            var display = _navigator.Manifest.Questions.Single(item => item.Structure.QuestionId == question.QuestionId);
            var captions = display.OptionCaptions is { } labels
                ? question.OptionIds.Select((id, index) => (id, labels[index])).ToDictionary(item => item.id, item => item.Item2, StringComparer.Ordinal)
                : null;
            await QuestionView.LoadAsync(question.QuestionId, pdf, _regionMap?.ForQuestion(question.QuestionId),
                interactionEnabled: false, allowAbstain: question.AllowAbstain, ct, captions);
            if (ct.IsCancellationRequested || generation != _renderGeneration || !ReferenceEquals(paper, _runtime.Paper) ||
                question.QuestionId != _currentQuestionId) return;
            if (_runtime.Answers is { } answers)
            {
                QuestionView.UpdateAnswer(answers.Answers.TryGetValue(question.QuestionId, out var value)
                    ? value : AnswerValue.Empty());
                if (_regionMap is not null && _regionError is null && answers.Status == AnswerSessionStatus.Active &&
                    !_runtime.HasFixedSubmission && answers.CurrentQuestionId != question.QuestionId)
                    await answers.SetCurrentQuestionAsync(question.QuestionId, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (IsWorkbenchError(error))
        {
            if (generation == _renderGeneration && ReferenceEquals(paper, _runtime.Paper))
                _regionError = "题目切换未完成，已作答内容已保存。请重新点击题号；如仍未完成，请联系监考人员。";
        }
        finally
        {
            if (pdf is not null) Array.Clear(pdf);
            if (generation == _renderGeneration)
            {
                _rendering = false;
                RefreshNav();
                RefreshStatus();
            }
        }
    }

    private async void OnOptionClicked(string optionId) => await ApplyAnswerAsync(
        (question, current) => OptionSelection.Toggle(question, current, optionId));

    private async void OnAbstainClicked() => await ApplyAnswerAsync(
        (question, current) => OptionSelection.ToggleAbstain(question, current));

    private async Task ApplyAnswerAsync(Func<PaperQuestionStructure, AnswerValue, AnswerValue> next)
    {
        if (!CanAnswer || _answerBusy || _lifetime.IsCancellationRequested ||
            _navigator is null || _currentQuestionId is null ||
            _navigator.Find(_currentQuestionId) is not { } question ||
            _runtime.Answers is not { } answers)
        {
            return;
        }
        _answerBusy = true;
        var generation = _renderGeneration;
        RefreshStatus();
        try
        {
            var current = answers.Answers.TryGetValue(question.QuestionId, out var value)
                ? value
                : AnswerValue.Empty();
            var updated = next(question, current);
            var result = await answers.SetAnswerAsync(question.QuestionId, updated, _lifetime.Token);
            if (_lifetime.IsCancellationRequested || generation != _renderGeneration ||
                question.QuestionId != _currentQuestionId || !ReferenceEquals(answers, _runtime.Answers)) return;
            if (!result.Succeeded)
            {
                // 写入失败时保持会话原状态展示；AnswerSession 已阻止内存快照覆盖可靠状态。
                QuestionView.UpdateAnswer(current);
                RefreshStatus();
                return;
            }
            var persisted = answers.Answers.TryGetValue(question.QuestionId, out var saved)
                ? saved
                : AnswerValue.Empty();
            QuestionView.UpdateAnswer(persisted);
            RefreshNav();
            RefreshStatus();
            // 只在本次选择已经可靠保存、页面与授权仍有效时前进；取消选择和多选普通选项留在原题。
            if (CanAnswer && _navigator?.NextQuestionAfterSelection(question.QuestionId, persisted) is { } nextQuestion)
            {
                _currentQuestionId = nextQuestion.QuestionId;
                await RenderCurrentQuestionAsync(_lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsWorkbenchError(error))
        {
            RefreshStatus();
        }
        finally
        {
            _answerBusy = false;
            RefreshNav();
            RefreshStatus();
        }
    }

    private void OnRegionsInvalid(string message)
    {
        if (_lifetime.IsCancellationRequested) { return; }
        _regionError = "本题的选项位置无法确定，暂无法作答。请联系监考人员。";
        QuestionView.SetInteractionEnabled(false);
        RefreshStatus();
    }

    private async void OnNavBoxClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string questionId }) await NavigateToQuestionAsync(questionId);
    }

    private async void OnNavigationKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right) || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        if (_navigator is null || _currentQuestionId is null) return;
        var adjacent = e.Key == Key.Right
            ? _navigator.NextQuestion(_currentQuestionId)
            : _navigator.PreviousQuestion(_currentQuestionId);
        if (adjacent is not null) await NavigateToQuestionAsync(adjacent.QuestionId);
    }

    private async Task NavigateToQuestionAsync(string questionId)
    {
        if (!_answerBusy && !_rendering && !_submitting && !_lifetime.IsCancellationRequested &&
            !string.Equals(questionId, _currentQuestionId, StringComparison.Ordinal) &&
            _navigator?.Find(questionId) is not null)
        {
            _currentQuestionId = questionId;
            try
            {
                await RenderCurrentQuestionAsync(_lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error) when (IsWorkbenchError(error))
            {
                _regionError ??= "题目显示失败，请联系监考人员。";
                RefreshStatus();
            }
        }
    }

    private async void OnToggleMark(object sender, RoutedEventArgs e)
    {
        if (!CanAnswer || _answerBusy || _currentQuestionId is null || _runtime.Answers is not { } answers)
        {
            return;
        }
        _answerBusy = true;
        RefreshStatus();
        try
        {
            await answers.ToggleMarkAsync(_currentQuestionId, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsWorkbenchError(error)) { }
        finally
        {
            _answerBusy = false;
            RefreshNav();
            RefreshStatus();
        }
    }

    private void OnRuntimeStateChanged(object? sender, EventArgs e)
    {
        if (_lifetime.IsCancellationRequested) { return; }
        RefreshStatus();
        RefreshNav();
        _ = InitializeWorkbenchAsync();
    }

    private void UpdateClock()
    {
        RefreshStatus();
        // 时间只取服务器时钟；尚未校时时显示占位，不使用本机时间。
        if (!App.Services.Clock.IsSynchronized)
        {
            ClockPillContent.Text = "--:--";
            return;
        }

        var now = App.Services.Clock.UtcNow;
        var clock = now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        // 优先使用 ScheduleChanged 指令中服务端下发的最新结束时间；未收到更新时回退到原始计划。
        var scheduledEnd = _runtime.Manager.OverriddenScheduledEnd ?? _exam.ScheduledEnd;
        var remain = scheduledEnd - now;
        ClockPillContent.Text = remain > TimeSpan.Zero
            ? $"{clock} · 剩余 {remain:hh\\:mm}"
            : $"{clock} · 考试时间已到";
    }

    private async void OnSubmit(object sender, RoutedEventArgs e)
    {
        if (_submitting || _answerBusy || _rendering || _lifetime.IsCancellationRequested || _runtime.HasFixedSubmission)
        {
            return;
        }
        if (_runtime.Answers is not { Status: AnswerSessionStatus.Active } answers || _navigator is not { } navigator)
        {
            PaperBanner.Level = StatusLevel.Warning;
            PaperBanner.Message = "答卷已提交或尚未就绪，当前不能交卷。";
            PaperBanner.IsOpen = true;
            return;
        }

        var window = Window.GetWindow(this);
        if (window is null) { return; }
        _submitting = true;
        RefreshStatus();
        try
        {
            var confirmed = await ConfirmDialog.ShowAsync(
                window,
                "提前交卷",
                $"当前还有 {navigator.CountUnanswered(answers.Answers)} 道题目未作答。\n确定提前交卷吗？交卷后不能修改答案。",
                "提前交卷",
                "继续答题",
                isDanger: true);
            if (!confirmed) { return; }

            _lifetime.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(answers, _runtime.Answers) || _runtime.HasFixedSubmission ||
                _runtime.Manager.CurrentPhase != ExamPackageManager.Phase.Unlocked) return;
            Busy.IsBusy = true;
            RefreshStatus();
            // 提前交卷与 END 共用同一固定提交入口；失败时原标识与快照保留，由同步服务自动重试。
            var fix = await answers.FixSubmissionAsync($"subm-{Guid.NewGuid():N}", _lifetime.Token);
            if (!fix.Succeeded)
            {
                PaperBanner.Level = StatusLevel.Danger;
                PaperBanner.Message = "答卷未能保存到本机，已作答内容仍保留。请点击“重试保存”，或联系监考人员。";
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
                PaperBanner.Message = "正在交卷，请勿关闭程序。网络中断时系统将自动重试。";
                PaperBanner.IsOpen = true;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or
            System.Data.Common.DbException or InvalidOperationException)
        {
            PaperBanner.Level = StatusLevel.Danger;
            PaperBanner.Message = "交卷尚未完成，答卷已保留，系统将自动重试。请勿关闭程序。";
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
            _localRetryError = "作答内容仍无法保存到本机，已有内容和最近的修改均已保留。请联系监考人员。";
        }
        finally { _retryingLocalSave = false; RefreshStatus(); }
    }

    private void RefreshNav()
    {
        if (_navigator is null)
        {
            NavList.ItemsSource = null;
            return;
        }
        var answers = _runtime.Answers;
        NavList.ItemsSource = _navigator.BuildSections(
            answers?.Answers, answers?.Marks, _currentQuestionId);
    }

    private void RefreshStatus()
    {
        var question = _currentQuestionId is not null ? _navigator?.Find(_currentQuestionId) : null;
        CurrentQuestionText.Text = question is null
            ? "正在准备试题"
            : $"第 {question.QuestionNo} 题 · {QuestionTypeTitle(question.QuestionType)}";
        MarkButton.Content = question is not null && _runtime.Answers?.Marks.Contains(question.QuestionId) == true
            ? "取消标记"
            : "标记本题";
        MarkButton.IsEnabled = CanAnswer && !_answerBusy;
        Submit.IsEnabled = CanAnswer && !_answerBusy;
        NavList.IsEnabled = !_answerBusy && !_rendering && !_submitting && !_lifetime.IsCancellationRequested &&
            _runtime.Paper is not null;
        QuestionView.SetInteractionEnabled(CanAnswer && !_answerBusy);
        if (!_rendering && question is not null && _runtime.Answers is { } currentAnswers)
            QuestionView.UpdateAnswer(currentAnswers.Answers.TryGetValue(question.QuestionId, out var saved)
                ? saved : AnswerValue.Empty());

        // B06 设备裁决：授权撤销后全页遮蔽，只保留返回入口。
        var revoked = _runtime.Manager.CurrentPhase == ExamPackageManager.Phase.Revoked ||
                      _runtime.Answers?.Status == AnswerSessionStatus.PermissionDenied;
        RevokedOverlay.Visibility = revoked ? Visibility.Visible : Visibility.Collapsed;

        if (_regionError is not null)
        {
            PaperBanner.Level = StatusLevel.Danger;
            PaperBanner.Message = _regionError;
        }
        else if (_runtime.HasFixedSubmission)
        {
            PaperBanner.Level = StatusLevel.Info;
            PaperBanner.Message = "正在交卷，请勿关闭程序。网络中断时系统将自动重试。";
        }
        else
        {
            PaperBanner.Level = StatusLevel.Warning;
            PaperBanner.Message = _runtime.Manager.CurrentPhase switch
            {
                ExamPackageManager.Phase.Ended => "考试已结束，正在自动交卷，请勿关闭程序。",
                ExamPackageManager.Phase.Revoked => "本机已停止作答，请联系监考人员。",
                ExamPackageManager.Phase.Unlocked when _navigator is null => "正在准备试题。",
                ExamPackageManager.Phase.Unlocked when _regionMap is null => "本场试卷暂无法作答，请联系监考人员。",
                ExamPackageManager.Phase.Unlocked => string.Empty,
                _ => "试题尚未解锁，请等待开考；如已到开考时间，请联系监考人员。",
            };
        }
        PaperBanner.IsOpen = !string.IsNullOrEmpty(PaperBanner.Message);

        // 连接或试题处理的技术原因只放在悬停说明里；考生看到通俗提示。
        var runtimeError = _runtime.ConnectionError ?? _runtime.Manager.LastError;
        RuntimeErrorBanner.IsOpen = !string.IsNullOrWhiteSpace(runtimeError);
        RuntimeErrorBanner.Message = string.IsNullOrWhiteSpace(runtimeError) ? string.Empty : StudentMessages.ConnectionProblem;
        RuntimeErrorBanner.ToolTip = string.IsNullOrWhiteSpace(runtimeError) ? null : runtimeError;
        // 本机保存失败的技术原因只放在提示框的悬停说明里，供监考老师排查；考生看到的是底部的通俗提示。
        var localError = _localRetryError ?? _runtime.LocalStateError;
        LocalStateErrorBanner.IsOpen = false;
        LocalStateErrorBanner.Message = string.Empty;
        var notice = StudentNotice();
        StatusNotice.Text = notice ?? string.Empty;
        StatusNotice.ToolTip = string.IsNullOrWhiteSpace(localError) ? null : localError;
        StatusNotice.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
        RetryLocalSaveButton.Visibility = _runtime.CanRetryLocalSave && notice is not null ? Visibility.Visible : Visibility.Collapsed;
        RetryLocalSaveButton.IsEnabled = !_retryingLocalSave && _runtime.CanRetryLocalSave;
        RefreshFocusStatus();
        _reviewPending = _runtime.PendingReviewVersion is not null && !revoked && !_runtime.HasFixedSubmission && !_runtime.Manager.ExamEnded;
        ReviewUpdateButton.Visibility = _reviewPending ? Visibility.Visible : Visibility.Collapsed;
        ReviewUpdateButton.IsEnabled = _reviewPending && !_reviewSaving && !_runtime.IsPaperUpdateInProgress;
        if (_reviewPending) ReviewUpdateButton.Content = "已查看更正后的试题";
        // B06 修订定位：复核期间提供“跳到第一道修订题”；清单缺失或不在当前试卷时隐藏。
        var changedQuestions = (_changedQuestionIds ?? [])
            .Select(id => _navigator?.Find(id)).OfType<PaperQuestionStructure>().ToArray();
        JumpToChangedButton.Visibility =
            _reviewPending && changedQuestions.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (changedQuestions.Length > 0)
            JumpToChangedButton.Content = $"查看修订试题（{changedQuestions.Length} 题）";
    }

    private static string QuestionTypeTitle(string questionType) => questionType switch
    {
        "single" => "单项选择题",
        "multiple" => "多项选择题",
        "indeterminate" => "不定项选择题",
        _ => "选择题",
    };
}
