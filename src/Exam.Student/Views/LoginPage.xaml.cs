using System.Windows;
using System.Net.Http;
using System.Windows.Controls;
using Exam.Student.Core;

namespace Exam.Student.Views;

/// <summary>
/// 登录页合并服务器连接与考生身份输入，填写后直接连接并登录。
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
    }

    private async void OnExitSystem(object sender, RoutedEventArgs e)
    {
        if (_requesting || Window.GetWindow(this) is not { } window) return;
        _requesting = true;
        try { await StudentExitDialog.ShowAsync(window, App.Services.Session); }
        catch (InvalidOperationException error) { ErrorBanner.Show(error.Message); }
        finally { _requesting = false; OnInputChanged(null, EventArgs.Empty); }
    }

    private void OnInputChanged(object? sender, EventArgs e)
    {
        if (LoginButton is null || HostInput is null || PortInput is null || NameInput is null || ClassInput is null || AdmissionInput is null)
        {
            return;
        }

        LoginButton.IsEnabled = !_requesting && HostInput.Text.Trim().Length > 0 && PortInput.Text.Trim().Length > 0
            && NameInput.Text.Trim().Length > 0
            && ClassInput.Text.Trim().Length > 0
            && AdmissionInput.Text.Trim().Length > 0;
    }

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        if (_requesting)
        {
            return;
        }

        string host;
        try { host = DeviceStore.NormalizeHost(HostInput.Text); }
        catch (ArgumentException error) { ErrorBanner.Show(error.Message); return; }
        if (!int.TryParse(PortInput.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            ErrorBanner.Show("端口号必须在 1 到 65535 之间。");
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

        ErrorBanner.Hide();
        _requesting = true;
        LoginButton.IsEnabled = false;
        Busy.IsBusy = true;

        try
        {
            await App.Services.StopPresenceAsync();
            App.Services.StopClock();
            var workflow = new StudentLoginWorkflow(App.Services.DeviceStore, App.Services.Api);
            var (device, result) = await workflow.LoginAsync(host, port, name, classText, admissionNo);
            App.Services.ConfigureDevice(device);
            App.Services.Session.OnLoggedIn(result);
            App.Services.StartClock();
            App.Services.StartPresence();
            _navigation.GoToExamSelection();
        }
        catch (StudentLoginException error)
        {
            StudentMessages.ShowWithDetail(ErrorBanner, LoginFailure(error.Error), error.Message);
        }
        catch (StudentIdentityException error)
        {
            ErrorBanner.Show(error.Message);
        }
        catch (DeviceStoreException error)
        {
            StudentMessages.ShowWithDetail(ErrorBanner, "操作未完成，请重试；如仍未完成，请联系监考人员。", error.Message);
        }
        catch (HttpStudentApi.UntrustedServerException error)
        {
            StudentMessages.ShowWithDetail(
                ErrorBanner,
                "连接校验未通过，已拒绝连接不受信任的服务器，请联系监考人员。",
                error.Message);
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

    // 名册不匹配等明确原因照常提示；其余服务器原因统一为通俗说法。
    private static string LoginFailure(Exam.Contracts.Common.ApiError error) => error.Code switch
    {
        Exam.Contracts.Common.ErrorCodes.RosterMismatch => "姓名、班级或准考证号与考生名单不一致，请核对后重新填写。",
        Exam.Contracts.Common.ErrorCodes.NotActivated => "设备连接未能确认，请重新登录或联系监考人员。",
        _ => "登录未完成，请重试；如仍未完成，请联系监考人员。",
    };
}
