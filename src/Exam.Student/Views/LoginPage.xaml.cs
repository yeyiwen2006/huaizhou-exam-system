using System.Windows;
using System.Net.Http;
using System.Windows.Controls;
using Exam.Contracts.Activation;
using Exam.Contracts.Participation;
using Exam.Desktop.Shared.Controls;
using Exam.Student.Core;

namespace Exam.Student.Views;

/// <summary>
/// 登录页：姓名 / 班级 / 准考证号（后两项仅数字），登录前弹出确认页核对信息。
/// 名册不符（ROSTER_MISMATCH）时显示服务器返回的中文说明。
/// </summary>
public partial class LoginPage : UserControl
{
    private readonly MainWindow.Navigation _navigation;
    private bool _requesting;

    public LoginPage(MainWindow.Navigation navigation)
    {
        _navigation = navigation;
        InitializeComponent();
        DataContext = App.Services.Session;
        if (App.Services.Device?.PendingOperation == "cancel")
        {
            ErrorBanner.Show("取消激活未完成，请重新点击“取消激活”。完成前不能登录。");
        }
    }

    private void OnInputChanged(object? sender, EventArgs e)
    {
        if (LoginButton is null || NameInput is null || ClassInput is null || AdmissionInput is null)
        {
            return;
        }

        LoginButton.IsEnabled = !_requesting && App.Services.Device?.PendingOperation is null && NameInput.Text.Trim().Length > 0
            && ClassInput.Text.Trim().Length > 0
            && AdmissionInput.Text.Trim().Length > 0;
    }

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        if (_requesting)
        {
            return;
        }

        var device = App.Services.Device;
        if (device is not { Status: ActivationStatus.Active })
        {
            // 正常流程不会到达：未激活设备无法打开登录页
            ErrorBanner.Show("设备未激活，请先完成激活。");
            return;
        }

        var name = NameInput.Text.Trim();
        var classText = ClassInput.Text.Trim();
        var admissionNo = AdmissionInput.Text.Trim();

        if (name.Length == 0 || classText.Length == 0 || admissionNo.Length == 0 ||
            !classText.All(char.IsAsciiDigit) || !admissionNo.All(char.IsAsciiDigit))
        {
            ErrorBanner.Show("请填写姓名，班级和准考证号只能包含数字。");
            return;
        }

        var body = new TextBlock
        {
            Text = $"姓名：{name}\n班级：{classText}\n准考证号：{admissionNo}\n\n请确认以上信息是否正确。",
            TextWrapping = TextWrapping.Wrap,
        };
        var window = Window.GetWindow(this);
        if (window is null)
        {
            return;
        }

        var confirmed = await ConfirmDialog.ShowAsync(window, "确认登录信息", body, "确认正确");
        if (!confirmed)
        {
            return;
        }

        ErrorBanner.Hide();
        _requesting = true;
        LoginButton.IsEnabled = false;
        Busy.IsBusy = true;

        try
        {
            var persisted = await Task.Run(App.Services.DeviceStore.Load);
            if (persisted is null || persisted.DeviceId != device.DeviceId || persisted.PendingOperation is not null)
            {
                ErrorBanner.Show("本机激活状态尚未确认，请重新启动程序或联系监考人员。");
                return;
            }

            var request = new LoginStudentRequest(
                RequestIds.New(), device.DeviceId, device.Credential, name, classText, admissionNo);
            var envelope = await App.Services.Api.LoginAsync(request);
            if (!envelope.IsSuccess)
            {
                StudentMessages.ShowWithDetail(ErrorBanner, LoginFailure(envelope.Error!), envelope.Error!.Message);
                return;
            }

            var result = envelope.Result!;
            if (result.DeviceId != device.DeviceId || result.Candidate.Name != name ||
                result.Candidate.ClassText != classText || result.Candidate.AdmissionNo != admissionNo)
            {
                ErrorBanner.Show("登录未完成，请重新登录。");
                return;
            }

            App.Services.Session.OnLoggedIn(result);
            _navigation.GoToExamSelection();
        }
        catch (DeviceStoreException error)
        {
            StudentMessages.ShowWithDetail(ErrorBanner, "操作未完成，请重试；如仍未完成，请联系监考人员。", error.Message);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidOperationException or TimeoutException)
        {
            ErrorBanner.Show("无法完成登录，请检查服务器连接后重试。");
        }
        finally
        {
            Busy.IsBusy = false;
            _requesting = false;
            OnInputChanged(null, EventArgs.Empty);
        }
    }

    private async void OnCancelActivation(object sender, RoutedEventArgs e)
    {
        if (_requesting || App.Services.Device is not { } device)
        {
            return;
        }

        var window = Window.GetWindow(this);
        if (window is null)
        {
            return;
        }

        var confirmed = await ConfirmDialog.ShowAsync(
            window,
            "取消激活",
            "取消激活后本机将不能登录和参加考试，本机编号会保留。确定取消激活吗？",
            "确定取消",
            "返回",
            isDanger: true);
        if (!confirmed)
        {
            return;
        }

        _requesting = true;
        Busy.Message = "正在取消激活…";
        Busy.IsBusy = true;
        ErrorBanner.Hide();
        try
        {
            var persisted = await Task.Run(App.Services.DeviceStore.Load) ?? device;
            var revoked = await App.Services.Activation.CancelAsync(persisted);
            App.Services.StopClock();
            App.Services.ConfigureDevice(revoked);
            _navigation.GoToActivation();
        }
        catch (Exception error) when (error is DeviceStoreException or StudentIdentityException)
        {
            StudentMessages.ShowWithDetail(ErrorBanner, "操作未完成，请重试；如仍未完成，请联系监考人员。", error.Message);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or InvalidOperationException or TimeoutException)
        {
            ErrorBanner.Show("取消激活未完成，请重试。");
        }
        finally
        {
            Busy.IsBusy = false;
            Busy.Message = "正在登录…";
            var persisted = await Task.Run(App.Services.DeviceStore.Load);
            if (persisted is not null)
            {
                App.Services.ConfigureDevice(persisted);
            }

            _requesting = false;
            OnInputChanged(null, EventArgs.Empty);
        }
    }

    // 名册不匹配等明确原因照常提示；其余服务器原因统一为通俗说法。
    private static string LoginFailure(Exam.Contracts.Common.ApiError error) => error.Code switch
    {
        Exam.Contracts.Common.ErrorCodes.RosterMismatch => "姓名、班级或准考证号与考生名单不一致，请核对后重新填写。",
        Exam.Contracts.Common.ErrorCodes.NotActivated => "本机尚未激活或激活已取消，请联系监考人员。",
        _ => "登录未完成，请重试；如仍未完成，请联系监考人员。",
    };
}
