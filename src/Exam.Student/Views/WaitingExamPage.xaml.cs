using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Exam.Desktop.Shared.Controls;
using Exam.Student.Core;

namespace Exam.Student.Views;

/// <summary>
/// 等待考试页（B02）：进入后准备资源并下载加密包与说明；
/// 齐备后“确认参试”；等待开考指令（或已开始考试直接）解密进入考试；
/// 返回需确认并退出参试（失败可重试）；所有网络等待均有可见状态。
/// </summary>
public partial class WaitingExamPage : UserControl
{
    private readonly MainWindow.Navigation _navigation;
    private readonly ExamOverview _exam;
    private readonly ExamPackageManager _manager;
    private DispatcherTimer? _timer;
    private bool _commandSubscribed;
    private bool _busy;

    /// <summary>考试名称（页面 XAML 绑定）。</summary>
    public string ExamName => _exam.Name;

    public WaitingExamPage(MainWindow.Navigation navigation, ExamOverview exam)
    {
        _navigation = navigation;
        _exam = exam;
        _manager = new ExamPackageManager(App.Services.Api, PackageDirectory());
        InitializeComponent();
        DataContext = App.Services.Session;

        TimeText.Text = $"开始时间：{exam.ScheduledStart:yyyy-MM-dd HH:mm}    结束时间：{exam.ScheduledEnd:HH:mm}";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateCountdown();
        _timer.Start();
        UpdateCountdown();

        Loaded += async (_, _) => await PrepareAndDownloadAsync();
        Unloaded += (_, _) =>
        {
            _timer?.Stop();
            if (_commandSubscribed && App.Services.CommandFeed is { } feed)
            {
                feed.CommandReceived -= OnCommandReceived;
            }
        };
    }

    private static string PackageDirectory() => Path.Combine(App.AppDataDirectory, "packages");

    private void UpdateCountdown()
    {
        var remain = _exam.ScheduledStart - DateTimeOffset.Now;
        CountdownText.Text = remain > TimeSpan.Zero
            ? $"距开考 {remain:hh\\:mm\\:ss}"
            : "已到开考时间";
    }

    private void SetBusy(bool busy, string message = "")
    {
        _busy = busy;
        BusyLayer.IsBusy = busy;
        if (!string.IsNullOrEmpty(message))
        {
            BusyLayer.Message = message;
        }
    }

    private void ShowError(string message)
    {
        ErrorBanner.Message = message;
        ErrorBanner.IsOpen = true;
    }

    private void HideError() => ErrorBanner.IsOpen = false;

    /// <summary>准备（进入）→ 下载加密包与考试说明。已在开始后进入走同一流程。</summary>
    private async Task PrepareAndDownloadAsync()
    {
        if (_busy || _manager.CurrentPhase != ExamPackageManager.Phase.Idle)
        {
            return;
        }

        var session = App.Services.Session.LoginResult;
        if (session is null)
        {
            return;
        }

        SetBusy(true, "正在准备考试…");
        HideError();
        try
        {
            var prepare = await App.Services.Api.PrepareEntryAsync(
                new Contracts.Participation.PrepareExamEntryRequest(
                    RequestIds.New(), session.SessionId, _exam.ExamId));

            if (prepare?.IsSuccess != true || prepare.Result?.Resources is null)
            {
                SetBusy(false);
                DownloadPill.Text = "准备失败";
                DownloadPill.Level = StatusLevel.Danger;
                ShowError(prepare?.Error?.Message ?? "当前不可进入该考试！");
                return;
            }

            _manager.SetPreparation(prepare.Result.PreparationId);
            await DownloadAsync(session.SessionId, prepare.Result.Resources);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            DownloadPill.Text = "准备失败";
            DownloadPill.Level = StatusLevel.Danger;
            ShowError($"连接失败：{ex.Message}");
        }
    }

    private async Task DownloadAsync(string sessionId, Contracts.Participation.ExamResourceDescriptor descriptor)
    {
        DownloadPill.Text = "下载中…";
        DownloadPill.Level = StatusLevel.Info;
        DownloadDetail.Text = string.Empty;
        RetryDownloadButton.Visibility = Visibility.Collapsed;
        DownloadBar.Value = 0;

        var progress = new Progress<PackageDownloadProgress>(p =>
        {
            if (p.TotalBytes > 0)
            {
                DownloadBar.Value = Math.Clamp(p.ReceivedBytes * 100.0 / p.TotalBytes, 0, 100);
                DownloadDetail.Text = $"{p.ReceivedBytes / 1024.0:F0} / {p.TotalBytes / 1024.0:F0} KB";
            }
        });

        SetBusy(true, "正在下载试题包…");
        var ok = false;
        try
        {
            ok = await _manager.DownloadAsync(sessionId, _exam.ExamId, descriptor, progress);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowError($"下载失败：{ex.Message}");
        }

        SetBusy(false);
        OnDownloadFinished(ok);
    }

    private void OnDownloadFinished(bool ok)
    {
        if (ok)
        {
            DownloadPill.Text = "已下载（加密）";
            DownloadPill.Level = StatusLevel.Success;
            DownloadBar.Value = 100;
            RetryDownloadButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            DownloadPill.Text = "下载失败";
            DownloadPill.Level = StatusLevel.Danger;
            RetryDownloadButton.Visibility = Visibility.Visible;
        }

        RefreshConfirmGate();
    }

    /// <summary>确认参试门槛：加密包 + 考试说明齐备且考试未结束（缺一不可）。</summary>
    private void RefreshConfirmGate()
    {
        var examOver = _exam.ScheduledEnd <= DateTimeOffset.Now;
        if (examOver)
        {
            _manager.OnExamEnded();
        }

        if (_manager.ExamEnded)
        {
            ConfirmButton.IsEnabled = false;
            ConfirmHint.Text = "考试已结束，不能新增参试。";
            StatePill.Text = "已结束";
            StatePill.Level = StatusLevel.Neutral;
            return;
        }

        if (_manager.HasPackage && _manager.HasInstructions)
        {
            ConfirmButton.IsEnabled = _manager.CanConfirmParticipation;
            ConfirmHint.Text = "试题包与考试说明已就绪，请确认参试。";
            StatePill.Text = "待确认参试";
            StatePill.Level = StatusLevel.Warning;
        }
        else
        {
            ConfirmButton.IsEnabled = false;
            ConfirmHint.Text = _manager.HasPackage
                ? "正在获取考试说明…"
                : "试题包未就绪，不能确认参试。";
        }
    }

    private async void OnRetryDownload(object sender, RoutedEventArgs e)
    {
        var session = App.Services.Session.LoginResult;
        if (session is null || _manager.Descriptor is null)
        {
            return;
        }

        await DownloadAsync(session.SessionId, _manager.Descriptor);
    }

    private async void OnConfirmParticipation(object sender, RoutedEventArgs e)
    {
        var session = App.Services.Session.LoginResult;
        if (session is null || !_manager.CanConfirmParticipation)
        {
            RefreshConfirmGate();
            return;
        }

        SetBusy(true, "正在确认参试…");
        Contracts.Common.ApiEnvelope<Contracts.Participation.ParticipationResult>? result = null;
        try
        {
            result = await _manager.ConfirmParticipationAsync(session.SessionId, _exam.ExamId);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            ShowError($"确认参试失败：{ex.Message}");
            return;
        }

        SetBusy(false);
        if (result?.IsSuccess != true || result.Result is null)
        {
            ShowError(result?.Error?.Message ?? "确认参试失败，请稍后重试。");
            RefreshConfirmGate();
            return;
        }

        ConfirmButton.IsEnabled = false;

        var state = result.Result;
        if (state.BusinessState == Contracts.Common.ParticipationBusinessState.Answering)
        {
            // 已开始的考试：同一流程进入，确认后直接解锁
            await UnlockAndEnterAsync();
            return;
        }

        StatePill.Text = "已确认 · 等待开考";
        StatePill.Level = StatusLevel.Info;
        ConfirmHint.Text = "已确认参试。到达开考时间并收到指令后自动解密进入考试。";

        if (App.Services.CommandFeed is { } feed && !_commandSubscribed)
        {
            _commandSubscribed = true;
            feed.CommandReceived += OnCommandReceived;
            _ = feed.StartAsync(session.SessionId, _exam.ExamId);
        }
    }

    private async void OnCommandReceived(object? sender, Contracts.Commands.CommandDelivery delivery)
    {
        if (delivery.ExamId != _exam.ExamId)
        {
            return;
        }

        if (delivery.EventType == Contracts.Common.CommandEventType.End)
        {
            _manager.OnExamEnded();
            RefreshConfirmGate();
            ShowError("考试已结束。");
            return;
        }

        if (delivery.EventType != Contracts.Common.CommandEventType.Start)
        {
            return;
        }

        // Dispatcher 上下文：指令回调可能在后台线程
        await Dispatcher.InvokeAsync(async () => await UnlockAndEnterAsync(delivery));
    }

    private async Task UnlockAndEnterAsync(Contracts.Commands.CommandDelivery? delivery = null)
    {
        SetBusy(true, "正在解密试题…");
        var ok = false;
        try
        {
            ok = delivery is null
                ? await _manager.UnlockAsync()
                : await _manager.HandleStartCommandAsync(delivery);
        }
        catch (Exception ex)
        {
            ShowError($"解密失败：{ex.Message}");
        }

        SetBusy(false);
        if (!ok)
        {
            ShowError("试题解密失败，请监考员处理。");
            return;
        }

        _timer?.Stop();
        App.Services.Session.BeginExam(_exam.ExamId, _manager.UnlockedPayload!);
        _navigation.GoToExamInProgress(_exam);
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
            "返回选择考试页面将退出本考试（退出参试），再进入需重新下载与确认。确定返回吗？",
            "确定返回",
            "取消",
            isDanger: true);
        if (!confirmed)
        {
            return;
        }

        // 退出参试需服务器确认；失败给出重试（退出确认及重试）
        while (true)
        {
            var session = App.Services.Session.LoginResult;
            if (session is null)
            {
                _navigation.GoToExamSelection();
                return;
            }

            SetBusy(true, "正在退出参试…");
            try
            {
                var result = await _manager.WithdrawAsync(session.SessionId, expectedVersion: 1);
                SetBusy(false);
                if (result?.IsSuccess == true || result is null)
                {
                    _timer?.Stop();
                    _navigation.GoToExamSelection();
                    return;
                }

                var retry = await ConfirmDialog.ShowAsync(
                    window,
                    "退出失败",
                    $"退出参试失败：{result.Error?.Message}\n是否重试？",
                    "重试退出",
                    "留在此页");
                if (!retry)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                SetBusy(false);
                var retry = await ConfirmDialog.ShowAsync(
                    window,
                    "退出失败",
                    $"退出参试失败：{ex.Message}\n是否重试？",
                    "重试退出",
                    "留在此页");
                if (!retry)
                {
                    return;
                }
            }
        }
    }
}
