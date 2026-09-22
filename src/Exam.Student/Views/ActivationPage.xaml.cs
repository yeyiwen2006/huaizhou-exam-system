using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Exam.Contracts.Activation;
using Exam.Desktop.Shared.Controls;
using Exam.Student.Core;

namespace Exam.Student.Views;

/// <summary>
/// 激活页：输入服务器 IP 与端口，提交激活申请并轮询审批状态。
/// 激活获批后设备信息持久保存在本机，后续服务器关闭不影响激活状态。
/// </summary>
public partial class ActivationPage : UserControl
{
    private const int MaxPollAttempts = 30;

    private readonly MainWindow.Navigation _navigation;
    private bool _requesting;
    private readonly CancellationTokenSource _lifetime = new();

    public ActivationPage(MainWindow.Navigation navigation)
    {
        _navigation = navigation;
        InitializeComponent();
        DataContext = App.Services.Session;
        if (App.Services.Device is { } device)
        {
            HostInput.Text = device.Host;
            PortInput.Text = device.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        Unloaded += (_, _) => _lifetime.Cancel();
        UpdateRequestEnabled();
    }

    private void OnInputChanged(object? sender, EventArgs e) => UpdateRequestEnabled();

    private void UpdateRequestEnabled()
    {
        if (RequestButton is null || HostInput is null || PortInput is null)
        {
            return;
        }

        RequestButton.IsEnabled = !_requesting
            && HostInput.Text.Trim().Length > 0
            && PortInput.Text.Trim().Length > 0;
    }

    private async void OnRequestActivation(object sender, RoutedEventArgs e)
    {
        if (_requesting)
        {
            return;
        }

        var host = HostInput.Text.Trim();
        if (!int.TryParse(PortInput.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            ErrorBanner.Show("端口号必须是 1–65535 之间的数字！");
            return;
        }

        if (!IPAddress.TryParse(host, out _))
        {
            ErrorBanner.Show("服务器 IP 地址格式不正确！");
            return;
        }

        ErrorBanner.Hide();
        _requesting = true;
        UpdateRequestEnabled();

        ActivationPill.Text = "等待考务端批准…";
        ActivationPill.Level = StatusLevel.Warning;

        try
        {
            for (var attempt = 0; attempt < MaxPollAttempts; attempt++)
            {
                Busy.Message = attempt == 0 ? "正在申请或恢复设备激活…" : "正在等待考务端批准…";
                Busy.IsBusy = true;
                var result = await App.Services.Activation.RequestOrPollAsync(host, port, _lifetime.Token);
                Busy.IsBusy = false;
                if (result.Status == ActivationStatus.Active && result.ClientNumber is not null)
                {
                    App.Services.ConfigureDevice(result);
                    App.Services.StartClock();

                    ActivationPill.Text = $"激活成功（编号 {result.ClientNumber}）";
                    ActivationPill.Level = StatusLevel.Success;

                    _navigation.GoToLogin();
                    return;
                }

                if (result.Status == ActivationStatus.Rejected)
                {
                    throw new StudentIdentityException("激活申请已被拒绝，请联系考务人员。");
                }

                await Task.Delay(1500, _lifetime.Token);
            }

            throw new TimeoutException("等待考务端批准超时。");
        }
        catch (TimeoutException ex)
        {
            ActivationPill.Text = "未激活";
            ActivationPill.Level = StatusLevel.Danger;
            ErrorBanner.Show(ex.Message + " 请稍后重试。");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 离开页面只停止查询，已持久保存的申请和身份保持不变。
        }
        catch (Exception error) when (error is DeviceStoreException or StudentIdentityException)
        {
            ActivationPill.Text = "激活尚未完成";
            ActivationPill.Level = StatusLevel.Danger;
            ErrorBanner.Show(error.Message);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            ActivationPill.Text = "等待连接恢复";
            ActivationPill.Level = StatusLevel.Warning;
            ErrorBanner.Show("未收到服务器确认，请检查连接后重试。原设备身份和激活申请已保留。");
        }
        finally
        {
            Busy.IsBusy = false;
            _requesting = false;
            UpdateRequestEnabled();
        }
    }
}
