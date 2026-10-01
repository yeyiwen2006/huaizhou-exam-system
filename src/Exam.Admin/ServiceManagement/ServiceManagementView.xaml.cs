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
        if (Application.Current is null) Resources.MergedDictionaries.Add(new ResourceDictionary
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
        LoginButton.IsEnabled = false;
        var password = LoginPassword.Password;
        LoginPassword.Clear();
        try { await _viewModel.LoginAsync(password); }
        catch (Exception error) { _viewModel.ReportError(error); }
        finally { password = string.Empty; LoginButton.IsEnabled = true; LoginPassword.Clear(); }
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
