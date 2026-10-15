using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Exam.Contracts.Common;
using Exam.Contracts.Participation;
using Exam.Desktop.Shared.Controls;
using Exam.Student.Core;

namespace Exam.Student.Views;

/// <summary>等待页完成密文及说明下载后才确认参试，状态由服务器结果和可靠指令驱动。</summary>
public partial class WaitingExamPage : UserControl
{
    private readonly MainWindow.Navigation _navigation;
    private ExamOverview _exam;
    private readonly IStudentApi _overviewApi;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private StudentExamRuntime? _runtime;
    private Button? _exitButton;
    private bool _busy;
    private bool _loaded;
    private bool _navigated;
    private string? _lastRuntimeError;
    private int _arbitrationRetryCountdown;
    private bool _updateRefreshQueued;
    private bool _recovering;
    private int _unlockRetryCountdown;
    private int _scheduleRefreshCountdown;
    private bool _refreshingSchedule;

    public string ExamName => _exam.Name;

    public WaitingExamPage(MainWindow.Navigation navigation, ExamOverview exam, IStudentApi? overviewApi = null)
    {
        _navigation = navigation;
        _exam = exam;
        _overviewApi = overviewApi ?? App.Services.Api;
        InitializeComponent();
        DataContext = App.Services.Session;
        RefreshTimeText();
        _timer.Tick += (_, _) => UpdateCountdown();
        _timer.Start();
        UpdateCountdown();
        Loaded += async (_, _) =>
        {
            if (!_loaded)
            {
                _loaded = true;
                await PrepareAndDownloadAsync();
            }
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            _lifetime.Cancel();
            if (_runtime is not null)
            {
                _runtime.StateChanged -= OnRuntimeStateChanged;
            }
        };
    }

    private async Task PrepareAndDownloadAsync()
    {
        if (_lifetime.IsCancellationRequested || _busy || _recovering || App.Services.Session.LoginResult is not { } session || App.Services.Device is not { } device)
        {
            return;
        }

        SetBusy(true, "正在下载试题和考试说明…");
        ErrorBanner.Hide();
        try
        {
            if (_runtime is null)
            {
                _runtime = App.Services.Session.ActiveExam;
                if (_runtime is null)
                {
                    var candidateId = session.CandidateIdsByExam?.GetValueOrDefault(_exam.ExamId);
                    if (string.IsNullOrWhiteSpace(candidateId))
                    {
                        throw new InvalidOperationException("当前登录尚未提供该场考试的考生标识，不能混用另一场考试的恢复记录。");
                    }
                    if (App.Services.Api is not IExamPackageApi packageApi)
                    {
                        throw new InvalidOperationException("试题服务尚未配置。");
                    }

                    var manager = await Task.Run(() => new ExamPackageManager(packageApi,
                        Path.Combine(App.AppDataDirectory, "packages"), device, session, _exam.ExamId), _lifetime.Token);
                    if (_lifetime.IsCancellationRequested)
                    {
                        manager.Dispose();
                        return;
                    }

                    _runtime = new StudentExamRuntime(manager, device, App.Services.LocalState, candidateId);
                    App.Services.Session.RegisterExam(_runtime);
                }

                if (_runtime.Manager.ExamId != _exam.ExamId || _runtime.Manager.SessionId != session.SessionId)
                {
                    throw new InvalidOperationException("考试会话不匹配。");
                }

                _runtime.StateChanged += OnRuntimeStateChanged;
            }

            if (!await _runtime.InitializeLocalRecoveryAsync(_lifetime.Token))
            {
                ErrorBanner.Show("本机答题准备未完成，请联系监考人员。");
                return;
            }
            if (_runtime.HasFixedSubmission)
            {
                ErrorBanner.Show("本机存在已提交的答卷，正在继续交卷，不能重新作答。");
                return;
            }

            var progress = new Progress<PackageDownloadProgress>(value =>
            {
                DownloadBar.Value = value.TotalBytes > 0 ? Math.Clamp(value.ReceivedBytes * 100.0 / value.TotalBytes, 0, 100) : 0;
                DownloadDetail.Text = $"{value.ReceivedBytes / 1024.0:F0} / {value.TotalBytes / 1024.0:F0} KB";
            });
            var prepared = await _runtime.Manager.PrepareAndDownloadAsync(progress, _lifetime.Token);
            if (_runtime.Manager.CurrentPhase == ExamPackageManager.Phase.Withdrawn)
            {
                // 崩溃前退出的回执可能刚在恢复时重放成功，按已确认的退出结果返回。
                await App.Services.Session.ReleaseExamAsync();
                _navigated = true;
                _navigation.GoToExamSelection();
                return;
            }

            if (!prepared)
            {
                var manager = _runtime.Manager;
                var message = manager.HasPackage
                    ? "考试说明获取失败！请点击“重新下载”。"
                    : "试题获取失败！请点击“重新下载”。";
                if (manager.LastPreparationErrorCode == ParticipationErrorCodes.EntryNotAllowed)
                {
                    message = "当前不可进入该考试";
                    var earliestEntry = (manager.OverriddenScheduledStart ?? _exam.ScheduledStart).AddMinutes(-20);
                    if (!manager.ExamEnded && _exam.Lifecycle != ExamLifecycle.Ended &&
                        App.Services.Clock.IsSynchronized && App.Services.Clock.UtcNow < earliestEntry)
                    {
                        message += $"。最早可进入时间：{earliestEntry.ToLocalTime():yyyy-MM-dd HH:mm}（开考前20分钟）。";
                    }
                }
                StudentMessages.ShowWithDetail(ErrorBanner, message, manager.LastError);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            ErrorBanner.Show("试题或考试说明获取失败！请点击“重新下载”；如仍失败，请联系监考人员。");
        }
        finally
        {
            SetBusy(false);
            RefreshState();
        }
    }

    private async void OnConfirmParticipation(object sender, RoutedEventArgs e)
    {
        if (_busy || _recovering || _runtime is null || !_runtime.LocalRecoveryChecked || _runtime.HasFixedSubmission)
        {
            return;
        }

        SetBusy(true, "正在确认参加考试…");
        ErrorBanner.Hide();
        try
        {
            var response = await _runtime.Manager.ConfirmParticipationAsync(_lifetime.Token);
            if (response?.IsSuccess != true || response.Result is null)
            {
                var code = response?.Error?.Code;
                var definitive = code is ParticipationErrorCodes.EntryNotAllowed
                    or ParticipationErrorCodes.RosterMismatch
                    or ParticipationErrorCodes.NotActivated
                    or ParticipationErrorCodes.AlreadySubmitted;
                var main = definitive
                    ? "当前不可进入该考试！"
                    : "参加考试确认未完成，请重新点击“确认参加考试”。";
                StudentMessages.ShowWithDetail(ErrorBanner, main, response?.Error?.Message ?? _runtime.Manager.LastError);
                return;
            }

            await ConnectNotificationsAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            ErrorBanner.Show("与服务器的连接尚未恢复，请点击“重新连接”。");
        }
        finally
        {
            SetBusy(false);
            RefreshState();
        }
    }

    private async Task ConnectNotificationsAsync()
    {
        if (_runtime?.Manager.Participation is not { } participation)
        {
            return;
        }

        var server = _runtime.Manager.Server;
        if (server is null)
        {
            await App.Services.Clock.SynchronizeAsync(App.Services.Api, _lifetime.Token);
            server = App.Services.Clock.Server ?? throw new InvalidOperationException("服务器身份尚未核实。");
        }

        await _runtime.StartNotificationsAsync(participation, server);
        await _runtime.SynchronizeLocalAnswersAsync(_lifetime.Token);
    }

    private async void OnRetryConnection(object sender, RoutedEventArgs e)
    {
        if (_busy || _recovering || _runtime is null || !_runtime.LocalRecoveryChecked || _runtime.HasFixedSubmission)
        {
            return;
        }

        SetBusy(true, "正在重新连接…");
        ErrorBanner.Hide();
        try
        {
            await ConnectNotificationsAsync();
            if (_runtime.Manager.ExamStarted && !_runtime.Manager.ExamEnded)
            {
                await _runtime.Manager.UnlockAsync(_lifetime.Token);
            }
            await _runtime.SynchronizeLocalAnswersAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            ErrorBanner.Show("尚未连接到服务器，请重新点击“重新连接”。");
        }
        finally
        {
            SetBusy(false);
            RefreshState();
        }
    }

    private async void OnGoBack(object sender, RoutedEventArgs e)
    {
        if (_busy || _recovering || _runtime?.HasFixedSubmission == true ||
            _runtime?.Manager.CanLeaveWaitingPage == false || Window.GetWindow(this) is not { } window)
        {
            return;
        }

        // A modal confirmation still pumps dispatcher timers. Do not start recovery or
        // navigate away while the operator is deciding whether to leave.
        SetBusy(true, "请确认是否退出考试…");
        try
        {
            var confirmed = await ConfirmDialog.ShowAsync(window, "退出考试", "确定要退出考试吗？", "确定退出", "取消", isDanger: true);
            if (!confirmed) return;
            if (_runtime?.HasFixedSubmission == true || _runtime?.Manager.CanLeaveWaitingPage == false)
            {
                ErrorBanner.Show("考试状态已变化，请继续答题或完成交卷后退出考试。");
                return;
            }

            SetBusy(true, "正在退出考试…");
            ErrorBanner.Hide();
            if (_runtime is not null && !_runtime.Manager.ExamEnded && _runtime.Manager.CurrentPhase != ExamPackageManager.Phase.Withdrawn &&
                (_runtime.Manager.Participation?.BusinessState == ParticipationBusinessState.Waiting || _runtime.Manager.HasPendingConfirmation))
            {
                var response = await _runtime.Manager.WithdrawAsync(_lifetime.Token);
                if (response?.IsSuccess != true || response.Result?.BusinessState != ParticipationBusinessState.NotParticipating)
                {
                    ErrorBanner.Show(response?.Error?.Message ?? _runtime.Manager.LastError ?? "退出处理中，请重试。收到服务器确认前仍保留原参加考试登记。");
                    return;
                }
            }

            _lifetime.Cancel();
            await App.Services.Session.ReleaseExamAsync();
            _navigated = true;
            _navigation.GoToExamSelection();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            ErrorBanner.Show("退出未完成，请重试。");
        }
        finally
        {
            SetBusy(false);
            RefreshState();
        }
    }

    private async void OnRetryDownload(object sender, RoutedEventArgs e) => await PrepareAndDownloadAsync();

    private async void OnRetryLocalSave(object sender, RoutedEventArgs e)
    {
        if (_busy || _recovering || _runtime is null) { return; }
        SetBusy(true, "正在重试保存…");
        try
        {
            await _runtime.RetryLocalSaveAsync(_lifetime.Token);
            if (_runtime.Manager.CurrentPhase == ExamPackageManager.Phase.Unlocked)
                await _runtime.SynchronizeLocalAnswersAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsRecoverable(error))
        {
            ErrorBanner.Show("作答内容仍无法保存到本机，请联系监考人员。");
        }
        finally { SetBusy(false); RefreshState(); }
    }

    private void OnExitButtonLoaded(object sender, RoutedEventArgs e)
    {
        _exitButton = (Button)sender;
        RefreshState();
    }

    private void OnRuntimeStateChanged(object? sender, EventArgs e)
    {
        RefreshState();
        MaybeAutoRefreshForUpdate();
    }

    /// <summary>等待/已下载阶段收到试卷更新事件后自动重新获取新版资料。</summary>
    private async void MaybeAutoRefreshForUpdate()
    {
        if (_lifetime.IsCancellationRequested || _busy || _recovering || _navigated || _updateRefreshQueued ||
            _runtime?.Manager is not { } manager || manager.DeviceDecisionPending)
        {
            return;
        }
        var descriptor = manager.Descriptor;
        if (manager.CurrentPhase is not (ExamPackageManager.Phase.ConfirmedWaiting or
            ExamPackageManager.Phase.Downloaded) ||
            manager.NewestPaperVersion <= (descriptor?.PaperVersion ?? 0))
        {
            return;
        }
        _updateRefreshQueued = true;
        try
        {
            await PrepareAndDownloadAsync();
        }
        finally
        {
            _updateRefreshQueued = false;
        }
    }

    private void SetBusy(bool busy, string message = "")
    {
        _busy = busy;
        BusyLayer.IsBusy = busy;
        if (message.Length > 0)
        {
            BusyLayer.Message = message;
        }
    }

    private void RefreshState()
    {
        if (_navigated)
        {
            return;
        }

        if (!_busy && !_recovering && _runtime?.HasFixedSubmission == true)
        {
            _navigated = true;
            _navigation.GoToExamInProgress(_exam, _runtime);
            return;
        }
        var manager = _runtime?.Manager;
        InstructionList.ItemsSource = manager?.Instructions?.Lines.Select(line => InstructionTextRenderer.Create(line, (Style)FindResource("BodyText"))).ToArray();
        DownloadPill.Text = manager?.HasPackage == true ? "试题已下载" : "试题尚未下载完成";
        DownloadPill.Level = manager?.HasPackage == true ? StatusLevel.Success : StatusLevel.Warning;
        DownloadBar.Value = manager?.HasPackage == true ? 100 : DownloadBar.Value;
        ConfirmButton.IsEnabled = !_busy && !_recovering && _runtime?.LocalRecoveryChecked == true &&
            !_runtime.HasFixedSubmission && manager?.CanConfirmParticipation == true;
        ConfirmButton.Content = manager?.Participation is null ? "确认参加考试" : "继续参加考试";
        RetryDownloadButton.Visibility = _runtime?.HasFixedSubmission == true ||
            manager?.CurrentPhase is ExamPackageManager.Phase.Unlocked or ExamPackageManager.Phase.Ended or ExamPackageManager.Phase.Withdrawn
            ? Visibility.Collapsed : Visibility.Visible;
        RetryConnectionButton.Visibility = _runtime?.LocalRecoveryChecked == true && !_runtime.HasFixedSubmission &&
            manager?.Participation is not null && !manager.ExamEnded && manager.CurrentPhase != ExamPackageManager.Phase.Revoked
            ? Visibility.Visible : Visibility.Collapsed;
        RetryDownloadButton.IsEnabled = !_busy && !_recovering;
        RetryConnectionButton.IsEnabled = !_busy && !_recovering;
        if (_exitButton is not null)
        {
            _exitButton.IsEnabled = !_busy && !_recovering && _runtime?.HasFixedSubmission != true &&
                (manager?.CanLeaveWaitingPage ?? true);
        }

        (StatePill.Text, StatePill.Level, ConfirmHint.Text) = manager?.CurrentPhase switch
        {
            ExamPackageManager.Phase.ConfirmedWaiting when manager.ExamStarted =>
                ("已开考，正在恢复试题", StatusLevel.Warning, "服务器已确认开考，正在恢复试题解锁；如未恢复，可点击“重新连接”。"),
            ExamPackageManager.Phase.ConfirmedWaiting when _runtime?.NotificationsStarted != true =>
                ("正在恢复连接", StatusLevel.Warning, "已确认参加考试，正在重新连接服务器。"),
            ExamPackageManager.Phase.ConfirmedWaiting => ("已确认参加考试，等待开考", StatusLevel.Info, "请保持程序运行，开考后试题将自动显示。"),
            ExamPackageManager.Phase.Unlocked => ("试题已解锁", StatusLevel.Success, "即将进入答题。"),
            ExamPackageManager.Phase.Ended => ("考试已结束", StatusLevel.Warning, "考试已结束。未提交的答卷将自动提交；如有疑问，请联系监考人员。"),
            ExamPackageManager.Phase.Revoked => ("本机不能继续答题", StatusLevel.Danger, "请联系监考人员。"),
            ExamPackageManager.Phase.Withdrawn => ("已退出考试", StatusLevel.Neutral, "已退出本场考试，请返回选择考试页面。"),
            ExamPackageManager.Phase.Downloaded when manager?.Participation is { BusinessState: not ParticipationBusinessState.NotParticipating } =>
                ("资料已就绪", StatusLevel.Success, "已确认参加考试，请点击“继续参加考试”。"),
            ExamPackageManager.Phase.Downloaded => ("资料已就绪", StatusLevel.Success, "请阅读考试说明后点击“确认参加考试”。"),
            _ when manager?.DeviceDecisionPending == true =>
                ("等待监考人员处理", StatusLevel.Warning, "该考生账号已在另一台设备进入本场考试，请联系监考人员。处理完成后本机将自动继续。"),
            _ when manager?.Participation is { BusinessState: not ParticipationBusinessState.NotParticipating } =>
                ("正在恢复", StatusLevel.Warning, "已确认参加考试，考试资料正在准备，可点击“重新下载”。"),
            _ => ("尚未确认参加考试", StatusLevel.Info, "试题和考试说明下载完成后，才能确认参加考试。"),
        };

        if (_runtime?.HasFixedSubmission == true)
        {
            (StatePill.Text, StatePill.Level, ConfirmHint.Text) =
                (_runtime.Answers?.HasPendingSubmission == true ? "答卷待保存" : "正在交卷",
                    StatusLevel.Warning, "本机存在已提交的答卷，正在继续交卷，不能重新作答。");
        }
        // 本机答卷卡片只在需要考生知道时出现；技术原因放在悬停说明里。
        var localError = _runtime?.LocalStateError;
        var localNotice = !string.IsNullOrWhiteSpace(localError)
            ? _runtime?.LocalRecoveryChecked != true
                ? "本机答题准备未完成，请联系监考人员。"
                : "作答内容暂时无法保存到本机，已有内容仍保留。请点击“重试保存”，或联系监考人员。"
            : _runtime?.HasFixedSubmission == true ? "本机存在已提交的答卷，正在继续交卷。" : null;
        LocalStateCard.Visibility = localNotice is null ? Visibility.Collapsed : Visibility.Visible;
        LocalStateText.Text = localNotice ?? string.Empty;
        LocalStateText.ToolTip = null;
        LocalStateErrorBanner.IsOpen = false;
        RetryLocalSaveButton.IsEnabled = !_busy && !_recovering && _runtime?.CanRetryLocalSave == true;
        RetryLocalSaveButton.Visibility = _runtime?.CanRetryLocalSave == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshRuntimeError();
        RefreshTimeText();

        if (!_busy && !_recovering && manager?.CurrentPhase == ExamPackageManager.Phase.Unlocked && _runtime?.NotificationsStarted == true &&
            !_runtime.HasFixedSubmission && _runtime.Answers?.Status == AnswerSessionStatus.Active)
        {
            _navigated = true;
            _navigation.GoToExamInProgress(_exam, _runtime);
        }
    }

    private void UpdateCountdown()
    {
        if (++_scheduleRefreshCountdown >= 10)
        {
            _scheduleRefreshCountdown = 0;
            _ = RefreshScheduleAsync();
        }
        RefreshRuntimeError();

        if (_runtime?.Manager.ExamEnded == true)
        {
            CountdownText.Text = "考试已结束";
            RefreshState();
            return;
        }
        if (!App.Services.Clock.IsSynchronized)
        {
            CountdownText.Text = "正在同步时间…";
            return;
        }

        var scheduledStart = _runtime?.Manager.OverriddenScheduledStart ?? _exam.ScheduledStart;
        var remain = scheduledStart - App.Services.Clock.UtcNow;
        CountdownText.Text = remain > TimeSpan.Zero ? $"距开考 {remain:hh\\:mm\\:ss}" : "已到开考时间，请等待试题解锁";
        // 经过计划起止时间不会自行触发开考、结束、解锁或参试状态变化。
        MaybeRetryArbitration();
        MaybeRecoverUnlock();
    }

    private async Task RefreshScheduleAsync()
    {
        // Confirmed participants receive versioned schedule commands. Before confirmation,
        // refresh the read-only overview so a waiting page cannot keep an old start time.
        if (_refreshingSchedule || _lifetime.IsCancellationRequested || _navigated ||
            _runtime?.Manager.Participation is not null || App.Services.Session.LoginResult is not { } session) return;
        _refreshingSchedule = true;
        try
        {
            var overviews = await _overviewApi.GetExamOverviewsAsync(session.SessionId, _lifetime.Token);
            if (_lifetime.IsCancellationRequested || _navigated || _runtime?.Manager.Participation is not null ||
                !ReferenceEquals(session, App.Services.Session.LoginResult)) return;
            if (overviews.SingleOrDefault(item => item.ExamId == _exam.ExamId) is not { } latest) return;
            _exam = latest;
            RefreshTimeText();
            UpdateCountdown();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsRecoverable(error)) { /* Keep the last received schedule and retry. */ }
        finally { _refreshingSchedule = false; }
    }

    private void MaybeRecoverUnlock()
    {
        if (_lifetime.IsCancellationRequested || _navigated || _busy || _recovering ||
            _runtime is not { LocalRecoveryChecked: true, HasFixedSubmission: false } runtime ||
            runtime.Manager is not { ExamEnded: false } manager ||
            manager.CurrentPhase is ExamPackageManager.Phase.Unlocked or ExamPackageManager.Phase.Revoked or ExamPackageManager.Phase.Withdrawn ||
            manager.Participation?.BusinessState is not (ParticipationBusinessState.Waiting or ParticipationBusinessState.Answering)) return;
        var start = manager.OverriddenScheduledStart ?? _exam.ScheduledStart;
        if (!manager.ExamStarted && App.Services.Clock.UtcNow < start) return;
        if (_unlockRetryCountdown > 0) { _unlockRetryCountdown--; return; }
        _unlockRetryCountdown = 5;
        _ = RecoverUnlockAsync(runtime);
    }

    private async Task RecoverUnlockAsync(StudentExamRuntime runtime)
    {
        _recovering = true;
        RefreshState();
        try
        {
            // 只核对已确认的参试登记；开考与解锁仍以服务器响应为准。
            if (!await runtime.Manager.PrepareAndDownloadAsync(ct: _lifetime.Token)) return;
            var confirmation = await runtime.Manager.ConfirmParticipationAsync(_lifetime.Token);
            if (confirmation?.IsSuccess == true) await ConnectNotificationsAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsRecoverable(error))
        {
            StudentMessages.ShowWithDetail(ErrorBanner, "试题解锁尚未完成，将继续重试；也可点击“重新连接”。", error.Message);
        }
        finally
        {
            _recovering = false;
            if (!_lifetime.IsCancellationRequested) RefreshState();
        }
    }

    private void RefreshTimeText()
    {
        var start = _runtime?.Manager.OverriddenScheduledStart ?? _exam.ScheduledStart;
        var end = _runtime?.Manager.OverriddenScheduledEnd ?? _exam.ScheduledEnd;
        TimeText.Text = $"开始时间：{start:yyyy-MM-dd HH:mm}    结束时间：{end:yyyy-MM-dd HH:mm}";
    }

    /// <summary>设备裁决：待裁决状态每 15 秒自动重试准备，裁决通过后自动继续。</summary>
    private void MaybeRetryArbitration()
    {
        if (_lifetime.IsCancellationRequested || _navigated || _busy || _runtime?.Manager is not { DeviceDecisionPending: true })
        {
            _arbitrationRetryCountdown = 0;
            return;
        }
        if (_arbitrationRetryCountdown > 0)
        {
            _arbitrationRetryCountdown--;
            return;
        }
        _arbitrationRetryCountdown = 15;
        _ = PrepareAndDownloadAsync();
    }

    private static bool IsRecoverable(Exception error) => error is IOException or HttpRequestException or InvalidOperationException
        or OperationCanceledException or TimeoutException or CryptographicException or UnauthorizedAccessException or ArgumentException
        or System.Data.Common.DbException or System.Net.WebSockets.WebSocketException or Microsoft.AspNetCore.SignalR.HubException;

    private void RefreshRuntimeError()
    {
        var connectionError = _runtime?.ConnectionError;
        if (string.IsNullOrWhiteSpace(connectionError) && _runtime?.Manager is { ExamStarted: true, ExamEnded: false, LastError: { Length: > 0 } unlockError } manager &&
            manager.CurrentPhase != ExamPackageManager.Phase.Unlocked)
        {
            const string message = "试题尚未解锁，正在核对服务器状态；也可点击“重新连接”重试。";
            StudentMessages.ShowWithDetail(ErrorBanner, message, unlockError);
            _lastRuntimeError = message;
            return;
        }
        if (!string.IsNullOrWhiteSpace(connectionError))
        {
            StudentMessages.ShowWithDetail(ErrorBanner, StudentMessages.ConnectionProblem, connectionError);
            _lastRuntimeError = StudentMessages.ConnectionProblem;
        }
        else if (_lastRuntimeError is not null && ErrorBanner.Message == _lastRuntimeError)
        {
            ErrorBanner.Hide();
            _lastRuntimeError = null;
        }
    }
}
