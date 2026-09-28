using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Exam.Contracts.Common;
using Exam.Desktop.Shared.Controls;
using Exam.Student.Core;

namespace Exam.Student.Views;

/// <summary>等待页完成密文及说明下载后才确认参试，状态由服务器结果和可靠指令驱动。</summary>
public partial class WaitingExamPage : UserControl
{
    private readonly MainWindow.Navigation _navigation;
    private readonly ExamOverview _exam;
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

    public string ExamName => _exam.Name;

    public WaitingExamPage(MainWindow.Navigation navigation, ExamOverview exam)
    {
        _navigation = navigation;
        _exam = exam;
        InitializeComponent();
        DataContext = App.Services.Session;
        TimeText.Text = $"开始时间：{exam.ScheduledStart:yyyy-MM-dd HH:mm}    结束时间：{exam.ScheduledEnd:yyyy-MM-dd HH:mm}";
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
        if (_busy || App.Services.Session.LoginResult is not { } session || App.Services.Device is not { } device)
        {
            return;
        }

        SetBusy(true, "正在准备并下载加密试题与考试说明…");
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
                ErrorBanner.Show(_runtime.LocalStateError ?? "本地恢复检查尚未完成，不能开始新的参试操作。");
                return;
            }
            if (_runtime.HasFixedSubmission)
            {
                ErrorBanner.Show("本机已有固定答卷，原记录已保留。正在转入原答卷补传和回执核对，不能重新开始作答。");
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
                ErrorBanner.Show(_runtime.Manager.LastError ?? "试题获取失败！请重试。");
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            ErrorBanner.Show("试题或考试说明获取失败！请检查服务器连接和本机存储后重试，原记录已保留。");
        }
        finally
        {
            SetBusy(false);
            RefreshState();
        }
    }

    private async void OnConfirmParticipation(object sender, RoutedEventArgs e)
    {
        if (_busy || _runtime is null || !_runtime.LocalRecoveryChecked || _runtime.HasFixedSubmission)
        {
            return;
        }

        SetBusy(true, "正在等待服务器确认参试…");
        ErrorBanner.Hide();
        try
        {
            var response = await _runtime.Manager.ConfirmParticipationAsync(_lifetime.Token);
            if (response?.IsSuccess != true || response.Result is null)
            {
                ErrorBanner.Show(response?.Error?.Message ?? _runtime.Manager.LastError ?? "参试确认尚未完成，请重试原请求。");
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
            ErrorBanner.Show("参试或指令连接尚未确认，请重试。原请求和参试登记已保留。");
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
        if (_busy || _runtime is null || !_runtime.LocalRecoveryChecked || _runtime.HasFixedSubmission)
        {
            return;
        }

        SetBusy(true, "正在恢复指令连接与解锁…");
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
            ErrorBanner.Show("连接或解锁尚未恢复，请继续重试。原参试登记保持不变。");
        }
        finally
        {
            SetBusy(false);
            RefreshState();
        }
    }

    private async void OnGoBack(object sender, RoutedEventArgs e)
    {
        if (_busy || Window.GetWindow(this) is not { } window)
        {
            return;
        }

        var confirmed = await ConfirmDialog.ShowAsync(window, "退出考试", "确定要退出考试吗？", "确定退出", "取消", isDanger: true);
        if (!confirmed)
        {
            return;
        }

        SetBusy(true, "退出处理中，正在等待服务器确认…");
        ErrorBanner.Hide();
        try
        {
            if (_runtime is not null && _runtime.Manager.CurrentPhase != ExamPackageManager.Phase.Withdrawn &&
                (_runtime.Manager.Participation is not null || _runtime.Manager.HasPendingConfirmation))
            {
                var response = await _runtime.Manager.WithdrawAsync(_lifetime.Token);
                if (response?.IsSuccess != true || response.Result?.BusinessState != ParticipationBusinessState.NotParticipating)
                {
                    ErrorBanner.Show(response?.Error?.Message ?? _runtime.Manager.LastError ?? "退出处理中，请重试。收到服务器确认前仍保留原参试登记。");
                    return;
                }
            }

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
            ErrorBanner.Show("退出处理中，请重试。收到服务器确认前仍保留原参试登记。");
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
        if (_busy || _runtime is null) { return; }
        SetBusy(true, "正在恢复本地保存…");
        try
        {
            await _runtime.RetryLocalSaveAsync(_lifetime.Token);
            if (_runtime.Manager.CurrentPhase == ExamPackageManager.Phase.Unlocked)
                await _runtime.SynchronizeLocalAnswersAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) when (IsRecoverable(error))
        {
            ErrorBanner.Show("本地保存尚未恢复，原记录和待保存内容均已保留，请检查存储后重试。");
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

    /// <summary>B06：等待/已下载阶段收到试卷更新事件后自动重新获取新版资料。</summary>
    private async void MaybeAutoRefreshForUpdate()
    {
        if (_busy || _navigated || _updateRefreshQueued ||
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

        if (!_busy && _runtime?.HasFixedSubmission == true)
        {
            _navigated = true;
            _navigation.GoToExamInProgress(_exam, _runtime);
            return;
        }
        var manager = _runtime?.Manager;
        InstructionList.ItemsSource = manager?.Instructions?.Lines.Select(line => InstructionTextRenderer.Create(line, (Style)FindResource("BodyText"))).ToArray();
        DownloadPill.Text = manager?.HasPackage == true ? "试题包已保存（加密）" : "试题包尚未完整获取";
        DownloadPill.Level = manager?.HasPackage == true ? StatusLevel.Success : StatusLevel.Warning;
        DownloadBar.Value = manager?.HasPackage == true ? 100 : DownloadBar.Value;
        ConfirmButton.IsEnabled = !_busy && _runtime?.LocalRecoveryChecked == true &&
            !_runtime.HasFixedSubmission && manager?.CanConfirmParticipation == true;
        ConfirmButton.Content = manager?.Participation is null ? "确认参试" : "核对并恢复原参试";
        RetryDownloadButton.Visibility = _runtime?.HasFixedSubmission == true ||
            manager?.CurrentPhase is ExamPackageManager.Phase.Unlocked or ExamPackageManager.Phase.Ended or ExamPackageManager.Phase.Withdrawn
            ? Visibility.Collapsed : Visibility.Visible;
        RetryConnectionButton.Visibility = _runtime?.LocalRecoveryChecked == true && !_runtime.HasFixedSubmission &&
            manager?.Participation is not null && !manager.ExamEnded && manager.CurrentPhase != ExamPackageManager.Phase.Revoked
            ? Visibility.Visible : Visibility.Collapsed;
        if (_exitButton is not null)
        {
            _exitButton.IsEnabled = !_busy && (manager?.Participation is null || manager.CurrentPhase is ExamPackageManager.Phase.ConfirmedWaiting or ExamPackageManager.Phase.Withdrawn);
        }

        (StatePill.Text, StatePill.Level, ConfirmHint.Text) = manager?.CurrentPhase switch
        {
            ExamPackageManager.Phase.ConfirmedWaiting when _runtime?.NotificationsStarted != true =>
                ("原参试登记已保留", StatusLevel.Warning, "正在核对当前状态或恢复指令连接，不能据此视为已退出或开始答题。"),
            ExamPackageManager.Phase.ConfirmedWaiting => ("已确认参试，等待开考", StatusLevel.Info, "已计入实际参试。请保持连接，收到服务器开考指令后解锁。"),
            ExamPackageManager.Phase.Unlocked => ("试题已解锁", StatusLevel.Success, "当前授权和试题版本已校验。"),
            ExamPackageManager.Phase.Ended => ("服务器已结束考试", StatusLevel.Warning, "考试已结束。原参试与待补传记录继续保留，当前版本尚不能完成补传，请联系考务人员。"),
            ExamPackageManager.Phase.Revoked => ("当前设备授权已撤销", StatusLevel.Danger, "本机不能继续答题，请联系考务人员。"),
            ExamPackageManager.Phase.Withdrawn => ("已确认退出考试", StatusLevel.Neutral, "服务器已将本次登记移出实际参试，请返回选择考试页面。"),
            ExamPackageManager.Phase.Downloaded when manager?.Participation is { BusinessState: not ParticipationBusinessState.NotParticipating } =>
                ("资料齐备，待核对原参试", StatusLevel.Success, "原参试登记继续保留。请核对并恢复原参试，服务器将返回当前授权与考试状态。"),
            ExamPackageManager.Phase.Downloaded => ("资料齐备，尚未参试", StatusLevel.Success, "请阅读考试说明，再确认参试。确认时服务器将再次核对准入条件。"),
            _ when manager?.DeviceDecisionPending == true =>
                ("设备冲突待裁决", StatusLevel.Warning, "同一账号在另一设备登录本场考试，正在等待考务人员裁决。本设备暂不能进入该考试，确认后将自动继续。"),
            _ when manager?.Participation is { BusinessState: not ParticipationBusinessState.NotParticipating } =>
                ("正在恢复原参试", StatusLevel.Warning, "原参试登记继续保留，资料准备或状态核对尚未完成，请重试。"),
            _ => ("尚未确认参试", StatusLevel.Info, "试题包与考试说明全部取得后才能确认参试。"),
        };

        if (_runtime?.HasFixedSubmission == true)
        {
            (StatePill.Text, StatePill.Level, ConfirmHint.Text) =
                (_runtime.Answers?.HasPendingSubmission == true ? "答卷冻结待保存" : "本机答卷已固定",
                    StatusLevel.Warning, "原冻结答卷等待可靠保存、核对和补传；本地冻结不等于服务器已收卷，不能重新开始作答。");
        }
        LocalStateText.Text = _runtime?.LocalStateSummary ?? "本地答题恢复尚未开始。";
        LocalStateErrorBanner.IsOpen = !string.IsNullOrWhiteSpace(_runtime?.LocalStateError);
        LocalStateErrorBanner.Message = _runtime?.LocalStateError ?? string.Empty;
        RetryLocalSaveButton.IsEnabled = !_busy && _runtime?.CanRetryLocalSave == true;
        RetryLocalSaveButton.Visibility = _runtime?.CanRetryLocalSave == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshRuntimeError();

        if (!_busy && manager?.CurrentPhase == ExamPackageManager.Phase.Unlocked && _runtime?.NotificationsStarted == true &&
            !_runtime.HasFixedSubmission && _runtime.Answers?.Status == AnswerSessionStatus.Active)
        {
            _navigated = true;
            _navigation.GoToExamInProgress(_exam, _runtime);
        }
    }

    private void UpdateCountdown()
    {
        RefreshRuntimeError();

        if (!App.Services.Clock.IsSynchronized)
        {
            CountdownText.Text = "服务器时间尚未同步";
            return;
        }

        var remain = _exam.ScheduledStart - App.Services.Clock.UtcNow;
        CountdownText.Text = remain > TimeSpan.Zero ? $"距计划开考 {remain:hh\\:mm\\:ss}" : "计划开始时间已到，请等待服务器确认";
        // 经过计划起止时间不会自行触发开考、结束、解锁或参试状态变化。
        MaybeRetryArbitration();
    }

    /// <summary>B06 设备裁决：待裁决状态每 15 秒自动重试准备，裁决通过后自动继续。</summary>
    private void MaybeRetryArbitration()
    {
        if (_navigated || _busy || _runtime?.Manager is not { DeviceDecisionPending: true })
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
        var error = _runtime?.ConnectionError ?? _runtime?.Manager.LastError;
        if (!string.IsNullOrWhiteSpace(error))
        {
            ErrorBanner.Show(error);
            _lastRuntimeError = error;
        }
        else if (_lastRuntimeError is not null && ErrorBanner.Message == _lastRuntimeError)
        {
            ErrorBanner.Hide();
            _lastRuntimeError = null;
        }
    }
}
