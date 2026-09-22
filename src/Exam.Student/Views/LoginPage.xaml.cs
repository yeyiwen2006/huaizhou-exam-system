using System.Windows;
using System.Windows.Controls;
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

    public LoginPage(MainWindow.Navigation navigation)
    {
        _navigation = navigation;
        InitializeComponent();
        DataContext = App.Services.Session;
    }

    private void OnInputChanged(object? sender, EventArgs e)
    {
        LoginButton.IsEnabled = NameInput.Text.Trim().Length > 0
            && ClassInput.Text.Trim().Length > 0
            && AdmissionInput.Text.Trim().Length > 0;
    }

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        var device = App.Services.DeviceStore.Load();
        if (device is null)
        {
            // 正常流程不会到达：未激活设备无法打开登录页
            ErrorBanner.Show("设备未激活，请先完成激活。");
            return;
        }

        var name = NameInput.Text.Trim();
        var classText = ClassInput.Text.Trim();
        var admissionNo = AdmissionInput.Text.Trim();

        // 登录前确认信息是否正确
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
        LoginButton.IsEnabled = false;
        Busy.IsBusy = true;

        var request = new LoginStudentRequest(
            RequestIds.New(), device.DeviceId, device.Credential, name, classText, admissionNo);
        var envelope = await App.Services.Api.LoginAsync(request);

        Busy.IsBusy = false;
        LoginButton.IsEnabled = true;

        if (!envelope.IsSuccess)
        {
            ErrorBanner.Show(envelope.Error!.Message);
            return;
        }

        App.Services.Session.OnLoggedIn(envelope.Result!);
        _navigation.GoToExamSelection();
    }

    private async void OnCancelActivation(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is null)
        {
            return;
        }

        var confirmed = await ConfirmDialog.ShowAsync(
            window,
            "取消激活",
            "取消激活后将清除本机的激活状态，需重新输入服务器地址并等待考务端批准。确定取消激活吗？",
            "确定取消",
            "返回",
            isDanger: true);
        if (!confirmed)
        {
            return;
        }

        App.Services.DeviceStore.Clear();
        App.Services.Session.Logout();
        _navigation.GoToActivation();
    }
}
