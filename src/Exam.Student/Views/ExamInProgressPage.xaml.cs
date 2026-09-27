using System.IO;
using System.Windows;
using System.Windows.Controls;
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

    /// <summary>页头右侧时钟胶囊（因 XAML 命名作用域限制经绑定注入）。</summary>
    public StatusPill ClockPillContent { get; } = new()
    {
        Text = "服务器时间 --:--:--",
        Level = StatusLevel.Info,
    };

    /// <summary>提交按钮启用状态（RightContent 命名作用域限制，经 INPC 绑定驱动）。</summary>
    public SubmitButtonState Submit { get; } = new();
    private DispatcherTimer? _clock;

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

        _sync = new SubmissionSyncService(App.Services.Api, runtime, device, clock: App.Services.Clock);
        _sync.StatusChanged += OnSyncStatusChanged;

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
            if (_started || _lifetime.IsCancellationRequested) return;
            _started = true;
            _ = RunSyncAsync();
            _ = InitializeWorkbenchAsync();
        };
        Unloaded += async (_, _) =>
        {
            _clock?.Stop();
            _lifetime.Cancel();
            _runtime.StateChanged -= OnRuntimeStateChanged;
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
            SyncStatusText.Text = "同步服务已停止，原答卷保留，请联系考务人员恢复后重试。";
        }
    }

    private bool CanAnswer =>
        !_lifetime.IsCancellationRequested && !_submitting && !_rendering &&
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
            _regionError ??= $"题目内容加载失败：{error.Message} 原答卷已保留，请重试或联系考务人员。";
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
            error = "选项坐标未通过完整校验，已停止坐标作答，请联系考务人员。";
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
            await QuestionView.LoadAsync(question.QuestionId, pdf, _regionMap?.ForQuestion(question.QuestionId),
                interactionEnabled: false, allowAbstain: question.AllowAbstain, ct);
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
                _regionError = "题目加载或导航保存失败，原答卷已保留，请恢复后重试。";
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
        _regionError = $"选项坐标校验失败：{message} 已停止坐标作答，请联系考务人员。";
        QuestionView.SetInteractionEnabled(false);
        RefreshStatus();
    }

    private async void OnNavBoxClick(object sender, RoutedEventArgs e)
    {
        if (!_answerBusy && !_rendering && !_submitting && !_lifetime.IsCancellationRequested &&
            sender is FrameworkElement { Tag: string questionId } &&
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
                _regionError ??= $"题目内容加载失败：{error.Message}";
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
        if (_submitting || _answerBusy || _rendering || _lifetime.IsCancellationRequested || _runtime.HasFixedSubmission)
        {
            return;
        }
        if (_runtime.Answers is not { Status: AnswerSessionStatus.Active } answers)
        {
            PaperBanner.Level = StatusLevel.Warning;
            PaperBanner.Message = "当前答卷尚未就绪或已被冻结，不能发起新的交卷。";
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
                "确定提前交卷吗？交卷后答案立即冻结，不能继续修改；只有取得服务器持久化回执才视为交卷成功。",
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
            ? "题目尚不可用"
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

        if (_regionError is not null)
        {
            PaperBanner.Level = StatusLevel.Danger;
            PaperBanner.Message = _regionError;
        }
        else if (_runtime.HasFixedSubmission)
        {
            PaperBanner.Level = StatusLevel.Info;
            PaperBanner.Message = "答卷已冻结，正在向服务器提交并等待持久化回执；期间请保持客户端运行，失败会自动沿用原提交重试。";
        }
        else
        {
            PaperBanner.Level = StatusLevel.Warning;
            PaperBanner.Message = _runtime.Manager.CurrentPhase switch
            {
                ExamPackageManager.Phase.Ended => "服务器已结束考试。原答卷固定后自动补传；尚未取得并保存服务器回执前不视为交卷成功。",
                ExamPackageManager.Phase.Revoked => "当前设备的答题授权已撤销，已停止访问试题。请联系考务人员。",
                ExamPackageManager.Phase.Unlocked when _navigator is null => "试题清单尚未就绪，正在按服务器授权准备原版题目显示。",
                ExamPackageManager.Phase.Unlocked when _regionMap is null => "当前试题包未提供选项坐标，仅原版显示题目内容，不能作答。",
                ExamPackageManager.Phase.Unlocked => string.Empty,
                _ => "试题尚未完成安全解锁，不能开始答题。请联系考务人员。",
            };
        }
        PaperBanner.IsOpen = !string.IsNullOrEmpty(PaperBanner.Message);

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

    private static string QuestionTypeTitle(string questionType) => questionType switch
    {
        "single" => "单项选择题",
        "multiple" => "多项选择题",
        "indeterminate" => "不定项选择题",
        _ => "选择题",
    };
}
