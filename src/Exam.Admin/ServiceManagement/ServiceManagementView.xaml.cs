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
        _viewModel.ConfirmAsync = _ => Task.FromResult(false);
        _viewModel.Deactivate();
    }
    private async void OnIndependentManagementLogin(object sender, RoutedEventArgs e)
    {
        var password = IndependentManagementPassword.Password;
        IndependentManagementPassword.Clear();
        try { await _viewModel.LoginAsync(password); }
        catch (Exception error) { _viewModel.ReportError(error); }
    }
    private void OnBack(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
    public void Detach()
    {
        Loaded -= OnLoaded; Unloaded -= OnUnloaded;
        _viewModel.ConfirmAsync = _ => Task.FromResult(false); _viewModel.Deactivate();
    }
}
