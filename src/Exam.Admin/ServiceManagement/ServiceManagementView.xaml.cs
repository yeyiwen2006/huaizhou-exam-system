using System.Windows;
using System.Windows.Controls;
using Exam.Desktop.Shared.Controls;

namespace Exam.Admin.ServiceManagement;

public partial class ServiceManagementView : UserControl
{
    private readonly ServiceManagementViewModel _viewModel;
    public event Action? BackRequested;
    public ServiceManagementView(ServiceManagementViewModel viewModel)
    {
        _viewModel = viewModel;
        if (System.Windows.Application.Current is null) Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml") });
        InitializeComponent(); DataContext = viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.ConfirmAsync = request => Window.GetWindow(this) is { } owner
            ? ConfirmDialog.ShowAsync(owner, request.Title, request.Body, isDanger: request.Dangerous) : Task.FromResult(false);
        _viewModel.Activate();
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        LoginPassword.Clear();
        _viewModel.ConfirmAsync = _ => Task.FromResult(false);
        _viewModel.Deactivate();
    }
    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        var password = LoginPassword.Password;
        LoginPassword.Clear();
        try { await _viewModel.LoginAsync(password); }
        catch (Exception error) { _viewModel.ReportError(error); }
        finally { password = string.Empty; LoginPassword.Clear(); }
    }
    private void OnImportConnection(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入部署人员提供的本机管理连接配置",
            Filter = "管理连接配置 (*.json)|*.json", CheckFileExists = true, Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            LoginPassword.Clear();
            _viewModel.ImportConnectionSettings(dialog.FileName);
        }
    }
    private async void OnReload(object sender, RoutedEventArgs e)
    { try { await _viewModel.ReloadConfigurationAsync(); } catch (Exception error) { _viewModel.ReportError(error); } }
    private void OnBack(object sender, RoutedEventArgs e) { LoginPassword.Clear(); BackRequested?.Invoke(); }
    public void Detach()
    {
        Loaded -= OnLoaded; Unloaded -= OnUnloaded;
        LoginPassword.Clear(); _viewModel.ConfirmAsync = _ => Task.FromResult(false); _viewModel.Deactivate();
    }
}
