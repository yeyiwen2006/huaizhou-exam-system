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
        ApplyServerConfigState();
        ShowRequestCode(App.Services.Device?.ActivationRequestId);

        Unloaded += (_, _) => _lifetime.Cancel();
        UpdateRequestEnabled();
    }

    /// <summary>
    /// 部署配置（server.json）：有效时预填服务器地址并提示来源；无效时给出错误提示；
    /// 未部署时提示联系监考人员。三种情况下手动输入均保留，作为部署遗漏时的应急通道。
    /// </summary>
    private void ApplyServerConfigState()
    {
        if (App.ServerConfig is { } config)
        {
            HostInput.Text = config.Host;
            PortInput.Text = config.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ConfigBanner.Show(
                "服务器地址来自本机部署配置（server.json），无需手动输入。",
                StatusLevel.Success);
        }
        else if (App.ServerConfigError is { } error)
        {
            if (App.Services.Device is { } device)
            {
                HostInput.Text = device.Host;
                PortInput.Text = device.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            ConfigBanner.Show(
                $"服务器部署配置（{ServerEndpointConfig.FileName}）无效：{error.Message} 请联系监考人员检查后再试；"
                + "也可先手动输入已知的服务器地址。",
                StatusLevel.Danger);
        }
        else
        {
            if (App.Services.Device is { } device)
            {
                HostInput.Text = device.Host;
                PortInput.Text = device.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            ConfigBanner.Show(
                $"本机未找到服务器部署配置（{ServerEndpointConfig.FileName}），请联系监考人员部署；"
                + "也可手动输入服务器地址。",
                StatusLevel.Warning);
        }
    }

    private void OnInputChanged(object? sender, EventArgs e)
    {
        UpdateRequestEnabled();
        if (!_requesting && ActivationCodeText is not null) ActivationCodeText.Visibility = Visibility.Collapsed;
    }

    private void ShowRequestCode(string? requestId)
    {
        var code = ActivationDisplay.RequestCode(requestId);
        ActivationCodeText.Text = code.Length == 0 ? string.Empty : $"申请识别码：{code}（请与监考人员核对）";
        ActivationCodeText.ToolTip = requestId;
        ActivationCodeText.Visibility = code.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateRequestEnabled()
    {
        if (RequestButton is null || HostInput is null || PortInput is null)
        {
            return;
        }

        HostInput.IsEnabled = PortInput.IsEnabled = !_requesting;
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

        ActivationPill.Text = "正在请求激活…";
        ActivationPill.Level = StatusLevel.Warning;

        try
        {
            for (var attempt = 0; attempt < MaxPollAttempts; attempt++)
            {
                var result = await App.Services.Activation.RequestOrPollAsync(host, port, _lifetime.Token);
                ShowRequestCode(result.ActivationRequestId);
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
                    throw new StudentIdentityException("激活申请未获批准，请联系监考人员。");
                }

                ActivationPill.Text = "等待考务端批准…";
                await Task.Delay(1500, _lifetime.Token);
            }

            throw new TimeoutException("等待考务端批准超时。");
        }
        catch (TimeoutException ex)
        {
            ActivationPill.Text = "未激活";
            ActivationPill.Level = StatusLevel.Danger;
            StudentMessages.ShowWithDetail(ErrorBanner, "等待批准超时，请重新点击“请求激活”，或联系监考人员。", ex.Message);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // 离开页面只停止查询，已持久保存的申请和身份保持不变。
        }
        catch (Exception error) when (error is DeviceStoreException or StudentIdentityException)
        {
            ActivationPill.Text = "激活尚未完成";
            ActivationPill.Level = StatusLevel.Danger;
            StudentMessages.ShowWithDetail(ErrorBanner, "激活未完成，请联系监考人员。", error.Message);
        }
        catch (HttpStudentApi.UntrustedServerException error)
        {
            ActivationPill.Text = "激活尚未完成";
            ActivationPill.Level = StatusLevel.Danger;
            StudentMessages.ShowWithDetail(
                ErrorBanner,
                "服务器证书校验失败，已拒绝连接不受信任的服务器。请核对部署配置（server.json）中的地址与证书指纹。",
                error.Message);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            ActivationPill.Text = "等待连接恢复";
            ActivationPill.Level = StatusLevel.Warning;
            ErrorBanner.Show("未收到服务器响应，请检查网络后重试。");
        }
        finally
        {
            _requesting = false;
            UpdateRequestEnabled();
        }
    }
}
