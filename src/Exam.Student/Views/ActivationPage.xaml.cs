using System.Net;
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

    public ActivationPage(MainWindow.Navigation navigation)
    {
        _navigation = navigation;
        InitializeComponent();
        DataContext = App.Services.Session;
        UpdateRequestEnabled();
    }

    private void OnInputChanged(object? sender, EventArgs e) => UpdateRequestEnabled();

    private void UpdateRequestEnabled()
    {
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

        // 设备身份：本机生成并持久化（deviceId 与凭据在真实实现中由首次激活生成）
        var existing = App.Services.DeviceStore.Load();
        var deviceId = existing?.DeviceId ?? $"dev-{Guid.NewGuid().ToString("N")[..12]}";
        var credential = existing?.Credential ?? Guid.NewGuid().ToString("N");
        var requestId = RequestIds.New();

        ActivationPill.Text = "等待考务端批准…";
        ActivationPill.Level = StatusLevel.Warning;

        try
        {
            // 幂等提交 + 轮询：同 RequestId 重复提交返回当前审批状态
            var request = new SubmitActivationRequest(requestId, deviceId, credential);
            for (var attempt = 0; attempt < MaxPollAttempts; attempt++)
            {
                Busy.Message = attempt == 0 ? "正在向服务器提交激活申请…" : "正在等待考务端批准…";
                Busy.IsBusy = true;
                var envelope = await App.Services.Api.SubmitActivationAsync(request);
                Busy.IsBusy = false;

                if (!envelope.IsSuccess)
                {
                    ActivationPill.Text = "未激活";
                    ActivationPill.Level = StatusLevel.Danger;
                    ErrorBanner.Show(envelope.Error!.Message);
                    return;
                }

                var result = envelope.Result!;
                if (result.Status == ActivationStatus.Active && result.ClientNumber is not null)
                {
                    App.Services.DeviceStore.Save(new DeviceRecord(
                        deviceId, credential, result.ClientNumber, host, port, DateTimeOffset.Now));
                    App.Services.Session.OnActivated(result.ClientNumber);

                    ActivationPill.Text = $"激活成功（编号 {result.ClientNumber}）";
                    ActivationPill.Level = StatusLevel.Success;

                    await Task.Delay(900);
                    _navigation.GoToLogin();
                    return;
                }

                await Task.Delay(1500);
            }

            throw new TimeoutException("等待考务端批准超时。");
        }
        catch (TimeoutException ex)
        {
            ActivationPill.Text = "未激活";
            ActivationPill.Level = StatusLevel.Danger;
            ErrorBanner.Show(ex.Message + " 请稍后重试。");
        }
        finally
        {
            Busy.IsBusy = false;
            _requesting = false;
            UpdateRequestEnabled();
        }
    }
}
