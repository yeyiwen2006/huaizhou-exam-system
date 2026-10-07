using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    public MainWindow(MainViewModel viewModel) : this(viewModel, ServiceManagementViewModel.CreateDefault()) { }

    public MainWindow(MainViewModel viewModel, ServiceManagementViewModel management)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        Management = management ?? throw new ArgumentNullException(nameof(management));
        if (System.Windows.Application.Current is null) Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("pack://application:,,,/Exam.Desktop.Shared;component/Themes/Theme.xaml") });
        InitializeComponent();
        _viewModel.Monitoring.CollectionCompleted += OnCollectionCompleted;
        _viewModel.Import.ConfirmPublicationAsync = message =>
            Exam.Desktop.Shared.Controls.ConfirmDialog.ShowAsync(this, "确认正式发布试卷", message,
                confirmText: "确认发布", cancelText: "取消", isDanger: true);
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

    private void ShowRegistrationSuccessPage()
    {
        DetachLogin();
        PageRegion.Content = new RegistrationSuccessPage(_viewModel.Connection);
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
        if (_router.Route == AdminWorkspaceRoute.LocalManagement) return;
        if (e.PropertyName == nameof(ConnectionViewModel.ShowRegistrationSuccess))
        {
            if (_viewModel.Connection.ShowRegistrationSuccess)
                ShowRegistrationSuccessPage();
            else
                ShowLoginPage();
            return;
        }
        if (e.PropertyName != nameof(ConnectionViewModel.IsConnected)) return;
        _router.BusinessConnectionChanged(_viewModel.Connection.IsConnected);
        if (_router.Route == AdminWorkspaceRoute.LocalManagement) return;
        if (_viewModel.Connection.IsConnected)
            ShowMainArea();
        else
            ShowLoginPage();
    }

    private void OnManagementRequested(object sender, RoutedEventArgs e) => OpenManagement();

    private void OnScoreFieldFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Panel fields && e.NewFocus is TextBox input)
            fields.Tag = input;
    }

    private void OnScoreFieldsDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Changing the selected group must not reuse an input selection from the previous group.
        if (sender is Panel fields) fields.Tag = null;
    }

    private void OnScoreTokenClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Panel { Tag: Panel { Tag: TextBox input } fields } ||
            e.OriginalSource is not Button { Tag: string token } || !input.IsEnabled || input.IsReadOnly ||
            !fields.IsAncestorOf(input) || !ReferenceEquals(input.DataContext, fields.DataContext)) return;

        var start = input.SelectionStart;
        var selected = input.SelectedText;
        input.BeginChange();
        try
        {
            input.SelectedText = token == "()" ? "(" + selected + ")" : token;
            if (token == "()") input.Select(start + 1, selected.Length);
            else input.Select(start + token.Length, 0);
        }
        finally { input.EndChange(); }
        input.Focus();
        e.Handled = true;
    }
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
            _viewModel.Monitoring.IsPageActive = _viewModel.SelectedPage == MainViewModel.MonitoringPage;
            _viewModel.Reports.IsPageActive = _viewModel.SelectedPage == MainViewModel.ReportsPage;
        }
        else if (_viewModel.Connection.ShowRegistrationSuccess) ShowRegistrationSuccessPage();
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

    private readonly Queue<Exam.Contracts.Submissions.CollectionCompletedNotification> _collectionNotifications = new();
    private bool _showingCollectionNotification;

    private async void OnCollectionCompleted(Exam.Contracts.Submissions.CollectionCompletedNotification item)
    {
        _collectionNotifications.Enqueue(item);
        if (_showingCollectionNotification) return;
        _showingCollectionNotification = true;
        try
        {
            while (_collectionNotifications.TryDequeue(out var notification))
            {
                // 收齐弹窗显示考试名称；查不到名称时用中性说法，不把内部标识显示给考务。
                var examName = _viewModel.DescribeExam(notification.ExamId);
                var subject = examName is null ? "本场考试" : $"“{examName}”考试";
                await Exam.Desktop.Shared.Controls.ConfirmDialog.ShowAsync(this, "本场考试全部答卷已成功回收",
                    $"{subject}的 {notification.ParticipantCount} 名实际参加考试考生答卷已全部保存（{notification.CompletedAt.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm:ss}）。\n如果之后又有考生进入考试，系统会重新统计；成绩在考试结束后生成。",
                    confirmText: "知道了", cancelText: "关闭");
            }
        }
        finally { _showingCollectionNotification = false; }
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
