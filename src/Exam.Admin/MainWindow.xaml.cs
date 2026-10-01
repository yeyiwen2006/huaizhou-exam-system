using System.Windows;
using System.Windows.Controls;
using Exam.Admin.ViewModels;
using Exam.Admin.Views;
using Exam.Admin.ServiceManagement;

namespace Exam.Admin;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private AdminLoginPage? _loginPage;
    private readonly AdminWorkspaceRouter _router = new();
    public ServiceManagementViewModel Management { get; }
    public AdminWorkspaceRoute WorkspaceRoute => _router.Route;
    private ServiceManagementView? _managementPage;
    private int _businessPage;

    public MainWindow() : this(new MainViewModel(), ServiceManagementViewModel.CreateDefault()) { }

    public MainWindow(MainViewModel viewModel, ServiceManagementViewModel management)
    {
        _viewModel = viewModel;
        Management = management;
        if (Application.Current is null) Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml") });
        InitializeComponent();
        _viewModel.Monitoring.CollectionCompleted += OnCollectionCompleted;
        DataContext = _viewModel;

        _viewModel.Connection.PropertyChanged += OnConnectionPropertyChanged;
        ShowLoginPage();
    }

    private void ShowLoginPage()
    {
        DetachLogin();
        _loginPage = new AdminLoginPage(_viewModel.Connection);
        _loginPage.ManagementRequested += OpenManagement;
        PageRegion.Content = _loginPage;
        MainTabArea.Visibility = Visibility.Collapsed;
    }

    private void ShowMainArea()
    {
        DetachLogin();
        PageRegion.Content = null;
        MainTabArea.Visibility = Visibility.Visible;
    }

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConnectionViewModel.IsConnected)) return;
        _router.BusinessConnectionChanged(_viewModel.Connection.IsConnected);
        if (_router.Route == AdminWorkspaceRoute.LocalManagement) return;
        if (_viewModel.Connection.IsConnected)
            ShowMainArea();
        else
            ShowLoginPage();
    }

    private void OnManagementRequested(object sender, RoutedEventArgs e) => OpenManagement();
    public void OpenManagement()
    {
        if (_router.Route != AdminWorkspaceRoute.LocalManagement) _businessPage = _viewModel.SelectedPage;
        _router.OpenManagement();
        DetachLogin();
        _viewModel.Activation.StopPolling();
        _viewModel.Monitoring.IsPageActive = false;
        _viewModel.Reports.IsPageActive = false;
        MainTabArea.Visibility = Visibility.Collapsed;
        if (_managementPage is null)
        {
            _managementPage = new ServiceManagementView(Management);
            _managementPage.BackRequested += ReturnToBusiness;
        }
        PageRegion.Content = _managementPage;
        Management.Activate();
    }
    public void ReturnToBusiness()
    {
        Management.Deactivate();
        _router.BusinessConnectionChanged(_viewModel.Connection.IsConnected);
        _router.ReturnToBusiness();
        if (_router.Route == AdminWorkspaceRoute.BusinessWorkspace)
        {
            ShowMainArea();
            _viewModel.SelectedPage = _businessPage;
            if (_viewModel.SelectedPage == MainViewModel.ActivationPage) _viewModel.Activation.StartPolling();
            _viewModel.Monitoring.IsPageActive = _viewModel.SelectedPage == 3;
            _viewModel.Reports.IsPageActive = _viewModel.SelectedPage == 4;
        }
        else ShowLoginPage();
    }
    private void DetachLogin()
    {
        if (_loginPage is null) return;
        _loginPage.ManagementRequested -= OpenManagement;
        _loginPage.Detach(); _loginPage = null;
    }

    private void OnSelectedPageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_router.Route == AdminWorkspaceRoute.LocalManagement) return;
        if (_viewModel.SelectedPage == MainViewModel.ActivationPage)
            _viewModel.Activation.StartPolling();
        else
            _viewModel.Activation.StopPolling();
    }

    private void OnCollectionCompleted(Exam.Contracts.Submissions.CollectionCompletedNotification item)
    {
        if (!IsVisible) return;
        _ = Exam.Desktop.Shared.Controls.ConfirmDialog.ShowAsync(this, "本场考试全部答卷已成功回收",
            $"考试 {item.ExamId} 的 {item.ParticipantCount} 名实际参试考生答卷已全部保存（{item.CompletedAt:yyyy-MM-dd HH:mm:ss}）。\n如果之后又有考生进入考试，系统会重新统计；成绩在考试结束后生成。",
            confirmText: "知道了", cancelText: "关闭");
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Connection.PropertyChanged -= OnConnectionPropertyChanged;
        _viewModel.Monitoring.CollectionCompleted -= OnCollectionCompleted;
        DetachLogin();
        if (_managementPage is not null) { _managementPage.BackRequested -= ReturnToBusiness; _managementPage.Detach(); }
        Management.Dispose();
        _viewModel.Dispose();
    }
}
